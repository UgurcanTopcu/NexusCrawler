using HtmlAgilityPack;
using OfficeOpenXml;
using Scrapper.Models;
using System.Collections.Concurrent;

namespace Scrapper.Services;

/// <summary>
/// Service to search products by barcode on Hepsiburada and check if they exist
/// Uses ScrapeDo API to bypass any protection
/// </summary>
public class HepsiburadaBarcodeSearchService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private readonly HttpClient _httpClient;
    private readonly ScrapeDoConfig _config;

    static HepsiburadaBarcodeSearchService()
    {
        // Set EPPlus license for EPPlus 8+
        ExcelPackage.License.SetNonCommercialPersonal("Scrapper");
    }

    public HepsiburadaBarcodeSearchService()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _config = new ScrapeDoConfig();
    }

    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            Console.WriteLine($"[HepsiburadaBarcode] Session {sessionId} cancelled");
        }
    }

    /// <summary>
    /// Search for products by barcode from Excel file
    /// </summary>
    public async Task SearchBarcodesFromExcelAsync(
        Stream excelStream,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessions[sessionId] = cts;
        }
        var cancellationToken = cts.Token;

        var results = new List<BarcodeSearchResult>();

        try
        {
            await onProgress(1, "📂 Reading Excel file...", "info");

            // Read barcodes from Excel
            var barcodes = ReadBarcodesFromExcel(excelStream);

            if (barcodes.Count == 0)
            {
                await onProgress(100, "⚠️ No barcodes found in the Excel file", "warning");
                await SendComplete(onProgress, null, 0);
                return;
            }

            await onProgress(5, $"✅ Found {barcodes.Count} barcodes to search", "success");

            var progressPerBarcode = 85.0 / barcodes.Count;
            var currentProgress = 10.0;

            for (int i = 0; i < barcodes.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, "⛔ Search stopped by user", "warning");
                    break;
                }

                var barcode = barcodes[i];
                await onProgress((int)currentProgress, $"🔍 Searching {i + 1}/{barcodes.Count}: {barcode}...", "info");

                try
                {
                    var result = await SearchBarcodeAsync(barcode);
                    results.Add(result);

                    if (result.ProductExists)
                    {
                        await onProgress((int)currentProgress, $"✅ {barcode}: Found - {TruncateUrl(result.ProductUrl!, 50)}", "success");
                    }
                    else
                    {
                        await onProgress((int)currentProgress, $"❌ {barcode}: Not Found", "warning");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HepsiburadaBarcode] Error searching {barcode}: {ex.Message}");
                    results.Add(new BarcodeSearchResult
                    {
                        Barcode = barcode,
                        ProductExists = false,
                        Status = $"Error: {ex.Message}"
                    });
                    await onProgress((int)currentProgress, $"⚠️ {barcode}: Error - {ex.Message}", "error");
                }

                currentProgress += progressPerBarcode;

                // Small delay between requests to be nice to the API
                await Task.Delay(500);
            }

            // Export results
            if (results.Count > 0)
            {
                await onProgress(95, "📊 Creating Excel report...", "info");

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"HepsiburadaBarcode_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                ExportResultsToExcel(results, filePath);

                var foundCount = results.Count(r => r.ProductExists);
                var summary = $"✅ Done! {foundCount}/{results.Count} products found";

                await onProgress(100, summary, "success");
                await SendComplete(onProgress, fileName, foundCount);
            }
            else
            {
                await onProgress(100, "No results", "warning");
                await SendComplete(onProgress, null, 0);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HepsiburadaBarcode] Fatal error: {ex.Message}");
            await onProgress(100, $"❌ Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, 0);
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

    /// <summary>
    /// Search for a single barcode on Hepsiburada
    /// </summary>
    private async Task<BarcodeSearchResult> SearchBarcodeAsync(string barcode)
    {
        var searchUrl = $"https://www.hepsiburada.com/ara?q={barcode}";
        Console.WriteLine($"[HepsiburadaBarcode] Searching: {searchUrl}");

        // Use ScrapeDo to fetch the page
        var html = await GetPageHtmlAsync(searchUrl);

        var result = new BarcodeSearchResult
        {
            Barcode = barcode,
            SearchUrl = searchUrl
        };

        // Check for "no results" message in raw HTML first
        if (html.Contains("Aramana uygun ürün bulunamadı") || 
            html.Contains("no-result-view") ||
            html.Contains("Aradığın ürünü bulamadık"))
        {
            result.ProductExists = false;
            result.Status = "Not Found";
            Console.WriteLine($"[HepsiburadaBarcode] {barcode}: No results found");
            return result;
        }

        // Parse the HTML
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        // Try multiple selectors to find product links
        // Hepsiburada uses various patterns for product links
        string? productUrl = null;

        // Pattern 1: Links containing "-p-" (product page pattern)
        var productLinks = htmlDoc.DocumentNode.SelectNodes("//a[contains(@href, '-p-')]");
        if (productLinks != null && productLinks.Count > 0)
        {
            foreach (var link in productLinks)
            {
                var href = link.GetAttributeValue("href", "");
                if (!string.IsNullOrEmpty(href) && href.Contains("-p-"))
                {
                    productUrl = href;
                    break;
                }
            }
        }

        // Pattern 2: Product card links
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
                        break;
                    }
                }
            }
        }

        // Pattern 3: Any link that looks like a product URL
        if (string.IsNullOrEmpty(productUrl))
        {
            var allLinks = htmlDoc.DocumentNode.SelectNodes("//a[@href]");
            if (allLinks != null)
            {
                foreach (var link in allLinks)
                {
                    var href = link.GetAttributeValue("href", "");
                    // Hepsiburada product URLs typically have format: /product-name-p-HBCV00001234567
                    if (!string.IsNullOrEmpty(href) && 
                        System.Text.RegularExpressions.Regex.IsMatch(href, @"-p-[A-Z0-9]+"))
                    {
                        productUrl = href;
                        break;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(productUrl))
        {
            // Make sure it's a full URL
            if (productUrl.StartsWith("/"))
            {
                productUrl = "https://www.hepsiburada.com" + productUrl;
            }
            
            // Clean up URL (remove query params)
            productUrl = productUrl.Split('?')[0];
            
            result.ProductExists = true;
            result.ProductUrl = productUrl;
            result.Status = "Product Exists";
            Console.WriteLine($"[HepsiburadaBarcode] {barcode}: Found - {productUrl}");
            return result;
        }

        // If we got here but no "not found" message, assume product exists but we couldn't extract link
        // Check if there are any product-related elements
        var hasProductContent = html.Contains("productCard") || 
                               html.Contains("product-card") ||
                               html.Contains("productList") ||
                               html.Contains("listing-item");

        if (hasProductContent)
        {
            result.ProductExists = true;
            result.ProductUrl = searchUrl; // Use search URL as fallback
            result.Status = "Product Exists (link not extracted)";
            Console.WriteLine($"[HepsiburadaBarcode] {barcode}: Product exists but couldn't extract direct link");
        }
        else
        {
            result.ProductExists = false;
            result.Status = "Not Found";
            Console.WriteLine($"[HepsiburadaBarcode] {barcode}: No product found");
        }
        
        return result;
    }

    /// <summary>
    /// Fetch page HTML using ScrapeDo API
    /// </summary>
    private async Task<string> GetPageHtmlAsync(string url)
    {
        var encodedUrl = System.Net.WebUtility.UrlEncode(url);
        var apiUrl = $"{_config.BaseUrl}?url={encodedUrl}&token={_config.ApiToken}";

        var response = await _httpClient.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Read barcodes from first column of Excel file
    /// </summary>
    private List<string> ReadBarcodesFromExcel(Stream excelStream)
    {
        var barcodes = new List<string>();

        try
        {
            using var package = new ExcelPackage(excelStream);
            var worksheet = package.Workbook.Worksheets.FirstOrDefault();

            if (worksheet == null)
            {
                Console.WriteLine("[HepsiburadaBarcode] No worksheet found in Excel");
                return barcodes;
            }

            var rowCount = worksheet.Dimension?.Rows ?? 0;
            Console.WriteLine($"[HepsiburadaBarcode] Excel has {rowCount} rows");

            // Check if first row is a header
            int startRow = 1;
            var firstCell = worksheet.Cells[1, 1].Value?.ToString()?.Trim()?.ToLower() ?? "";
            if (firstCell == "barcode" || firstCell == "barkod" || firstCell == "ean" || firstCell == "upc" || firstCell == "gtin")
            {
                startRow = 2;
                Console.WriteLine("[HepsiburadaBarcode] Detected header row, starting from row 2");
            }

            for (int row = startRow; row <= rowCount; row++)
            {
                var cellValue = worksheet.Cells[row, 1].Value?.ToString()?.Trim();

                if (!string.IsNullOrWhiteSpace(cellValue))
                {
                    // Clean barcode - remove any non-numeric characters for validation
                    var cleanBarcode = cellValue.Replace(" ", "").Replace("-", "");
                    barcodes.Add(cleanBarcode);
                }
            }

            Console.WriteLine($"[HepsiburadaBarcode] Found {barcodes.Count} barcodes");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HepsiburadaBarcode] Error reading Excel: {ex.Message}");
        }

        return barcodes;
    }

    /// <summary>
    /// Export results to Excel file
    /// </summary>
    private void ExportResultsToExcel(List<BarcodeSearchResult> results, string filePath)
    {
        // License is set in static constructor
        using var package = new ExcelPackage();
        var worksheet = package.Workbook.Worksheets.Add("Barcode Results");

        // Headers
        worksheet.Cells[1, 1].Value = "Barcode";
        worksheet.Cells[1, 2].Value = "Status";
        worksheet.Cells[1, 3].Value = "Product URL";

        // Style headers
        using (var range = worksheet.Cells[1, 1, 1, 3])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightBlue);
        }

        // Data
        int row = 2;
        foreach (var result in results)
        {
            worksheet.Cells[row, 1].Value = result.Barcode;
            worksheet.Cells[row, 2].Value = result.Status;
            worksheet.Cells[row, 3].Value = result.ProductUrl ?? "";

            // Color code the status
            if (result.ProductExists)
            {
                worksheet.Cells[row, 2].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                worksheet.Cells[row, 2].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGreen);
            }
            else
            {
                worksheet.Cells[row, 2].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                worksheet.Cells[row, 2].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightCoral);
            }

            row++;
        }

        // Auto-fit columns
        worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

        // Make URL column wider
        worksheet.Column(3).Width = 80;

        package.SaveAs(new FileInfo(filePath));
        Console.WriteLine($"[HepsiburadaBarcode] Exported results to {filePath}");
    }

    private string TruncateUrl(string url, int maxLength)
    {
        if (string.IsNullOrEmpty(url)) return "";
        return url.Length > maxLength ? url.Substring(0, maxLength) + "..." : url;
    }

    private async Task SendComplete(Func<int, string, string, Task> onProgress, string? fileName, int foundCount)
    {
        var data = new
        {
            complete = true,
            downloadUrl = fileName != null ? $"/api/download/{fileName}" : null,
            fileName = fileName,
            foundCount = foundCount
        };

        var json = System.Text.Json.JsonSerializer.Serialize(data);
        await onProgress(100, json, "complete");
    }
}

/// <summary>
/// Result of a barcode search
/// </summary>
public class BarcodeSearchResult
{
    public string Barcode { get; set; } = "";
    public string SearchUrl { get; set; } = "";
    public bool ProductExists { get; set; }
    public string Status { get; set; } = "";
    public string? ProductUrl { get; set; }
}
