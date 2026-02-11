using HtmlAgilityPack;
using OfficeOpenXml;
using Scrapper.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Scrapper.Services;

public class HepsiburadaBarcodeSearchService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private readonly HttpClient _httpClient;
    private readonly ScrapeDoConfig _config;
    private const int MaxParallelRequests = 5;
    private const int DelayBetweenRequestsMs = 1000;

    static HepsiburadaBarcodeSearchService()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
    }

    public HepsiburadaBarcodeSearchService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _config = new ScrapeDoConfig();
    }

    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
            cts.Cancel();
    }

    public async Task SearchBarcodesFromExcelAsync(
        Stream excelStream,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null,
        string? originalFileName = null)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;

        var results = new ConcurrentBag<BarcodeSearchResult>();

        try
        {
            await onProgress(1, "📂 Reading Excel file...", "info");

            var barcodes = ReadBarcodesFromExcel(excelStream);

            if (barcodes.Count == 0)
            {
                await onProgress(100, "⚠️ No barcodes found in the Excel file", "warning");
                await SendComplete(onProgress, null, 0, 0);
                return;
            }

            await onProgress(5, $"✅ Found {barcodes.Count} barcodes to search (processing {MaxParallelRequests} at a time)", "success");

            var progressPerBarcode = 85.0 / barcodes.Count;
            var completedCount = 0;
            var progressLock = new object();

            using var semaphore = new SemaphoreSlim(MaxParallelRequests);
            // Per-request throttle so concurrent requests don't block each other
            using var throttle = new SemaphoreSlim(1, 1);

            var tasks = barcodes.Select(async (barcode, index) =>
            {
                await semaphore.WaitAsync(cts.Token);
                try
                {
                    if (cts.Token.IsCancellationRequested)
                        return;

                    // Throttle: stagger requests so they don't all hit the API at once
                    await throttle.WaitAsync(cts.Token);
                    try { await Task.Delay(DelayBetweenRequestsMs, cts.Token); }
                    finally { throttle.Release(); }

                    BarcodeSearchResult result;
                    try
                    {
                        result = await SearchBarcodeAsync(barcode);
                    }
                    catch (Exception ex)
                    {
                        result = new BarcodeSearchResult
                        {
                            Barcode = barcode,
                            ProductExists = false,
                            Status = $"Error: {ex.Message}"
                        };
                    }

                    results.Add(result);

                    int currentCompleted;
                    lock (progressLock)
                    {
                        completedCount++;
                        currentCompleted = completedCount;
                    }

                    var currentProgress = 10.0 + (currentCompleted * progressPerBarcode);

                    if (result.Status.StartsWith("Error"))
                    {
                        await onProgress((int)currentProgress, $"⚠️ [{currentCompleted}/{barcodes.Count}] {barcode}: {result.Status}", "error");
                    }
                    else if (result.ProductExists)
                    {
                        await onProgress((int)currentProgress, $"✅ [{currentCompleted}/{barcodes.Count}] {barcode}: Found - {TruncateUrl(result.ProductUrl!, 50)}", "success");
                    }
                    else
                    {
                        await onProgress((int)currentProgress, $"❌ [{currentCompleted}/{barcodes.Count}] {barcode}: Not Found", "warning");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                await onProgress(50, "⛔ Search stopped by user", "warning");
            }

            var resultsList = results.ToList();
            if (resultsList.Count > 0)
            {
                await onProgress(95, "📊 Creating Excel report...", "info");

                string fileName;
                if (!string.IsNullOrEmpty(originalFileName))
                {
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
                    fileName = $"{fileNameWithoutExt}_Results.xlsx";
                }
                else
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    fileName = $"HepsiburadaBarcode_{timestamp}.xlsx";
                }
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                ExportResultsToExcel(resultsList, filePath);

                var foundCount = resultsList.Count(r => r.ProductExists);
                var totalCount = resultsList.Count;
                var summary = $"✅ Done! {foundCount}/{totalCount} products found";

                await onProgress(100, summary, "success");
                await SendComplete(onProgress, fileName, foundCount, totalCount);
            }
            else
            {
                await onProgress(100, "No results", "warning");
                await SendComplete(onProgress, null, 0, 0);
            }
        }
        catch (Exception ex)
        {
            await onProgress(100, $"❌ Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, 0, 0);
        }
        finally
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessions.TryRemove(sessionId, out _);
            }
            cts.Dispose();
        }
    }

    private async Task<BarcodeSearchResult> SearchBarcodeAsync(string barcode)
    {
        var searchUrl = $"https://www.hepsiburada.com/ara?q={barcode}";
        var html = await GetPageHtmlAsync(searchUrl);

        var result = new BarcodeSearchResult
        {
            Barcode = barcode,
            SearchUrl = searchUrl
        };

        if (html.Contains("Aramana uygun ürün bulunamadı") || 
            html.Contains("no-result-view") ||
            html.Contains("Aradığın ürünü bulamadık"))
        {
            result.ProductExists = false;
            result.Status = "Not Found";
            return result;
        }

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        string? productUrl = null;

        // Extract product name from various sources
        ExtractProductName(html, htmlDoc, result);

        // Extract category from various sources
        ExtractCategory(html, htmlDoc, result);

        // Match both old (-p-) and new (-pm-) Hepsiburada product URL patterns
        var productLinks = htmlDoc.DocumentNode.SelectNodes("//a[contains(@href, '-pm-') or contains(@href, '-p-')]");
        if (productLinks != null && productLinks.Count > 0)
        {
            foreach (var link in productLinks)
            {
                var href = link.GetAttributeValue("href", "");
                if (!string.IsNullOrEmpty(href) && Regex.IsMatch(href, @"-pm?-[A-Z0-9]+"))
                {
                    productUrl = href;
                    if (string.IsNullOrEmpty(result.ProductName))
                    {
                        var title = link.GetAttributeValue("title", "");
                        if (!string.IsNullOrEmpty(title))
                            result.ProductName = title;
                    }
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(productUrl))
        {
            var cardLinks = htmlDoc.DocumentNode.SelectNodes("//a[contains(@class, 'product')]");
            if (cardLinks != null && cardLinks.Count > 0)
            {
                foreach (var link in cardLinks)
                {
                    var href = link.GetAttributeValue("href", "");
                    if (!string.IsNullOrEmpty(href) && href.StartsWith("/"))
                    {
                        productUrl = href;
                        if (string.IsNullOrEmpty(result.ProductName))
                        {
                            var title = link.GetAttributeValue("title", "");
                            if (!string.IsNullOrEmpty(title))
                                result.ProductName = title;
                        }
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(productUrl))
        {
            var allLinks = htmlDoc.DocumentNode.SelectNodes("//a[@href]");
            if (allLinks != null)
            {
                foreach (var link in allLinks)
                {
                    var href = link.GetAttributeValue("href", "");
                    if (!string.IsNullOrEmpty(href) && 
                        Regex.IsMatch(href, @"-pm?-[A-Z0-9]+"))
                    {
                        productUrl = href;
                        if (string.IsNullOrEmpty(result.ProductName))
                        {
                            var title = link.GetAttributeValue("title", "");
                            if (!string.IsNullOrEmpty(title))
                                result.ProductName = title;
                        }
                        break;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(productUrl))
        {
            if (productUrl.StartsWith("/"))
                productUrl = "https://www.hepsiburada.com" + productUrl;
            
            productUrl = productUrl.Split('?')[0];
            
            result.ProductExists = true;
            result.ProductUrl = productUrl;
            result.Status = "Product Exists";
            return result;
        }

        var hasProductContent = html.Contains("productCard") || 
                               html.Contains("product-card") ||
                               html.Contains("productList") ||
                               html.Contains("listing-item");

        if (hasProductContent)
        {
            result.ProductExists = true;
            result.ProductUrl = searchUrl;
            result.Status = "Product Exists (link not extracted)";
        }
        else
        {
            result.ProductExists = false;
            result.Status = "Not Found";
        }
        
        return result;
    }

    private void ExtractProductName(string html, HtmlDocument htmlDoc, BarcodeSearchResult result)
    {
        // Try title attribute from the product card link (search results page)
        var productCardLink = htmlDoc.DocumentNode.SelectSingleNode(
            "//a[contains(@class, 'productCardLink')][@title]" +
            " | //a[contains(@href, '-pm-')][@title]" +
            " | //a[contains(@href, '-p-')][@title]");
        if (productCardLink != null)
        {
            var title = productCardLink.GetAttributeValue("title", "");
            if (!string.IsNullOrEmpty(title))
            {
                result.ProductName = System.Net.WebUtility.HtmlDecode(title);
                return;
            }
        }

        // Try embedded JSON from variantList
        if (string.IsNullOrEmpty(result.ProductName))
        {
            var nameMatch = Regex.Match(html, @"""variantList"":\s*\[\s*\{[^\}]*?""name""\s*:\s*""([^""]+)""", RegexOptions.Singleline);
            if (nameMatch.Success)
                result.ProductName = System.Net.WebUtility.HtmlDecode(nameMatch.Groups[1].Value);
        }

        // Try product name from search results JSON
        if (string.IsNullOrEmpty(result.ProductName))
        {
            var nameMatch = Regex.Match(html, @"""productName""\s*:\s*""([^""]+)""");
            if (nameMatch.Success)
                result.ProductName = System.Net.WebUtility.HtmlDecode(nameMatch.Groups[1].Value);
        }
    }

    private void ExtractCategory(string html, HtmlDocument htmlDoc, BarcodeSearchResult result)
    {
        // Try mainCategory from embedded JSON (e.g. "mainCategory":{"id":123,"name":"..."})
        var mainCatMatch = Regex.Match(html, @"""mainCategory""\s*:\s*\{[^}]*?""name""\s*:\s*""([^""]+)""", RegexOptions.Singleline);
        if (mainCatMatch.Success)
        {
            result.ProductCategory = System.Net.WebUtility.HtmlDecode(mainCatMatch.Groups[1].Value);
            return;
        }

        // Try sidebar tree category div nodes (search results page)
        // Categories render as <div class="seoAnchorLink-... treeCategoryContent-...">Text</div>
        var treeCategoryNodes = htmlDoc.DocumentNode.SelectNodes(
            "//*[contains(@class, 'seoAnchorLink') and contains(@class, 'treeCategoryContent')]");
        if (treeCategoryNodes != null)
        {
            var categoryTexts = treeCategoryNodes
                .Select(n => System.Net.WebUtility.HtmlDecode(n.InnerText.Trim()))
                .Where(t => !string.IsNullOrWhiteSpace(t) && t != "Tüm kategoriler")
                .Distinct()
                .ToList();
            if (categoryTexts.Count > 0)
            {
                result.ProductCategory = string.Join(" > ", categoryTexts);
                return;
            }
        }

        // Try categoryName from JSON, skip "Tüm kategoriler"
        var catNameMatches = Regex.Matches(html, @"""categoryName""\s*:\s*""([^""]+)""");
        foreach (Match m in catNameMatches)
        {
            var name = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(name) && name != "Tüm kategoriler")
            {
                result.ProductCategory = name;
                return;
            }
        }
    }

    private async Task<string> GetPageHtmlAsync(string url)
    {
        var encodedUrl = System.Net.WebUtility.UrlEncode(url);
        var apiUrl = $"{_config.BaseUrl}?url={encodedUrl}&token={_config.ApiToken}";

        var response = await _httpClient.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private List<string> ReadBarcodesFromExcel(Stream excelStream)
    {
        var barcodes = new List<string>();

        try
        {
            using var package = new ExcelPackage(excelStream);
            var worksheet = package.Workbook.Worksheets.FirstOrDefault();

            if (worksheet == null)
                return barcodes;

            var rowCount = worksheet.Dimension?.Rows ?? 0;
            int startRow = 1;
            var firstCell = worksheet.Cells[1, 1].Value?.ToString()?.Trim()?.ToLower() ?? "";
            if (firstCell == "barcode" || firstCell == "barkod" || firstCell == "ean" || firstCell == "upc" || firstCell == "gtin")
                startRow = 2;

            for (int row = startRow; row <= rowCount; row++)
            {
                var cellValue = worksheet.Cells[row, 1].Value?.ToString()?.Trim();

                if (!string.IsNullOrWhiteSpace(cellValue))
                {
                    var individualBarcodes = cellValue.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    
                    foreach (var barcode in individualBarcodes)
                    {
                        var cleanBarcode = barcode.Trim().Replace(" ", "").Replace("-", "");
                        
                        if (!string.IsNullOrWhiteSpace(cleanBarcode))
                            barcodes.Add(cleanBarcode);
                    }
                }
            }
        }
        catch { }

        return barcodes;
    }

    private void ExportResultsToExcel(List<BarcodeSearchResult> results, string filePath)
    {
        using var package = new ExcelPackage();
        var worksheet = package.Workbook.Worksheets.Add("Barcode Results");

        worksheet.Cells[1, 1].Value = "Barcode";
        worksheet.Cells[1, 2].Value = "Status";
        worksheet.Cells[1, 3].Value = "Product Name";
        worksheet.Cells[1, 4].Value = "Product Category";
        worksheet.Cells[1, 5].Value = "Product URL";

        using (var range = worksheet.Cells[1, 1, 1, 5])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightBlue);
        }

        int row = 2;
        foreach (var result in results)
        {
            worksheet.Cells[row, 1].Value = result.Barcode;
            worksheet.Cells[row, 2].Value = result.Status;
            worksheet.Cells[row, 3].Value = result.ProductName ?? "";
            worksheet.Cells[row, 4].Value = result.ProductCategory ?? "";
            worksheet.Cells[row, 5].Value = result.ProductUrl ?? "";

            var color = result.ProductExists ? System.Drawing.Color.LightGreen : System.Drawing.Color.LightCoral;
            worksheet.Cells[row, 2].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
            worksheet.Cells[row, 2].Style.Fill.BackgroundColor.SetColor(color);

            row++;
        }

        worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
        worksheet.Column(5).Width = 80;
        package.SaveAs(new FileInfo(filePath));
    }

    private string TruncateUrl(string url, int maxLength) =>
        string.IsNullOrEmpty(url) ? "" : 
        url.Length > maxLength ? url.Substring(0, maxLength) + "..." : url;

    private async Task SendComplete(Func<int, string, string, Task> onProgress, string? fileName, int foundCount, int totalCount)
    {
        var data = new
        {
            complete = true,
            downloadUrl = fileName != null ? $"/api/download/{fileName}" : null,
            fileName,
            foundCount,
            totalCount
        };

        await onProgress(100, System.Text.Json.JsonSerializer.Serialize(data), "complete");
    }
}

public class BarcodeSearchResult
{
    public string Barcode { get; set; } = "";
    public string SearchUrl { get; set; } = "";
    public bool ProductExists { get; set; }
    public string Status { get; set; } = "";
    public string? ProductUrl { get; set; }
    public string? ProductName { get; set; }
    public string? ProductCategory { get; set; }
}
