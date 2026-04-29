using HtmlAgilityPack;
using OfficeOpenXml;
using Scrapper.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Scrapper.Services;

public class HepsiburadaProductSearchService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private const int ScrapeDoParallelRequestLimit = 10;
    private readonly HttpClient _httpClient;
    private readonly ScrapeDoConfig _config;

    static HepsiburadaProductSearchService()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
    }

    public HepsiburadaProductSearchService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _config = new ScrapeDoConfig();
    }

    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
            cts.Cancel();
    }

    public async Task SearchAndScrapeFromExcelAsync(
        Stream excelStream,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null,
        string? originalFileName = null)
    {
        ArgumentNullException.ThrowIfNull(excelStream);
        ArgumentNullException.ThrowIfNull(onProgress);

        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;

        var products = new ConcurrentBag<ProductInfo>();
        int foundCount = 0;
        int notFoundCount = 0;
        int completedCount = 0;

        using var progressLock = new SemaphoreSlim(1, 1);

        async Task ReportProgressAsync(int progress, string message, string type)
        {
            await progressLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await onProgress(progress, message, type).ConfigureAwait(false);
            }
            finally
            {
                progressLock.Release();
            }
        }

        try
        {
            await ReportProgressAsync(1, "?? Reading Excel file...", "info");

            var productNames = ReadProductNamesFromExcel(excelStream);
            if (productNames.Count == 0)
            {
                await ReportProgressAsync(100, "?? No product names found in the Excel file", "warning");
                await SendComplete(onProgress, null, 0, 0);
                return;
            }

            await ReportProgressAsync(5, $"? Found {productNames.Count:N0} product names to search", "success");
            await ReportProgressAsync(6, $"? Using Scrape.do with {ScrapeDoParallelRequestLimit} parallel requests", "info");

            var searchItems = productNames
                .Select((productName, index) => (Index: index, ProductName: productName))
                .ToArray();

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = ScrapeDoParallelRequestLimit,
                CancellationToken = cts.Token
            };

            await Parallel.ForEachAsync(searchItems, options, async (item, cancellationToken) =>
            {
                var itemLabel = $"[{item.Index + 1}/{productNames.Count}]";

                await ReportProgressAsync(
                    CalculateProgress(Volatile.Read(ref completedCount), productNames.Count),
                    $"?? {itemLabel} Searching: {TruncateName(item.ProductName, 60)}",
                    "info");

                try
                {
                    var productUrl = await SearchProductAsync(item.ProductName, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(productUrl))
                    {
                        Interlocked.Increment(ref notFoundCount);
                        var completed = Interlocked.Increment(ref completedCount);
                        await ReportProgressAsync(
                            CalculateProgress(completed, productNames.Count),
                            $"? {itemLabel} No product found for: {TruncateName(item.ProductName, 60)}",
                            "warning");
                        return;
                    }

                    await ReportProgressAsync(
                        CalculateProgress(Volatile.Read(ref completedCount), productNames.Count),
                        $"?? {itemLabel} Scraping product page via Scrape.do...",
                        "info");

                    using var scraper = new HepsiburadaScraper
                    {
                        Method = ScrapeMethod.ScrapeDo
                    };

                    var product = await scraper.GetProductDetailsAsync(productUrl).ConfigureAwait(false);
                    if (product == null)
                    {
                        Interlocked.Increment(ref notFoundCount);
                        var completed = Interlocked.Increment(ref completedCount);
                        await ReportProgressAsync(
                            CalculateProgress(completed, productNames.Count),
                            $"?? {itemLabel} Scrape returned no product data for: {TruncateName(item.ProductName, 60)}",
                            "warning");
                        return;
                    }

                    product.Source = "hepsiburada-search";
                    product.Attributes["Search Term"] = item.ProductName;
                    if (string.IsNullOrWhiteSpace(product.Name))
                        product.Name = item.ProductName;

                    products.Add(product);
                    Interlocked.Increment(ref foundCount);

                    var done = Interlocked.Increment(ref completedCount);
                    await ReportProgressAsync(
                        CalculateProgress(done, productNames.Count),
                        $"? {itemLabel} Scraped: {TruncateName(product.Name, 60)}",
                        "success");
                }
                catch (HttpRequestException ex)
                {
                    Interlocked.Increment(ref notFoundCount);
                    var completed = Interlocked.Increment(ref completedCount);
                    await ReportProgressAsync(
                        CalculateProgress(completed, productNames.Count),
                        $"?? {itemLabel} Request failed: {ex.Message}",
                        "warning");
                }
                catch (InvalidOperationException ex)
                {
                    Interlocked.Increment(ref notFoundCount);
                    var completed = Interlocked.Increment(ref completedCount);
                    await ReportProgressAsync(
                        CalculateProgress(completed, productNames.Count),
                        $"?? {itemLabel} Scrape failed: {ex.Message}",
                        "warning");
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref notFoundCount);
                    var completed = Interlocked.Increment(ref completedCount);
                    await ReportProgressAsync(
                        CalculateProgress(completed, productNames.Count),
                        $"?? {itemLabel} Request timed out: {ex.Message}",
                        "warning");
                }
            }).ConfigureAwait(false);

            await ExportResultsAsync(onProgress, products.ToList(), foundCount, productNames.Count, originalFileName, isPartial: false);
        }
        catch (OperationCanceledException)
        {
            await ReportProgressAsync(90, "? Search stopped by user", "warning");
            await ExportResultsAsync(onProgress, products.ToList(), foundCount, foundCount + notFoundCount, originalFileName, isPartial: true);
        }
        catch (Exception ex)
        {
            await ReportProgressAsync(100, $"? Error: {ex.Message}", "error");
            await ExportResultsAsync(onProgress, products.ToList(), foundCount, foundCount + notFoundCount, originalFileName, isPartial: true);
        }
        finally
        {
            if (!string.IsNullOrEmpty(sessionId))
                _sessions.TryRemove(sessionId, out _);

            cts.Dispose();
        }
    }

    private async Task<string?> SearchProductAsync(string productName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(productName))
            return null;

        var searchUrl = $"https://www.hepsiburada.com/ara?q={Uri.EscapeDataString(productName)}";
        var html = await GetPageHtmlAsync(searchUrl, cancellationToken);

        if (html.Contains("Aramana uygun ürün bulunamadý", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("no-result-view", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("Aradýðýn ürünü bulamadýk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        var productUrl = ExtractFirstProductUrl(htmlDoc);
        if (string.IsNullOrEmpty(productUrl))
            return null;

        if (productUrl.StartsWith('/'))
            productUrl = "https://www.hepsiburada.com" + productUrl;

        return productUrl.Split('?')[0].Split('#')[0];
    }

    private static string? ExtractFirstProductUrl(HtmlDocument htmlDoc)
    {
        var productLinks = htmlDoc.DocumentNode.SelectNodes("//a[contains(@href, '-pm-') or contains(@href, '-p-')]");
        if (productLinks != null)
        {
            foreach (var link in productLinks)
            {
                var href = link.GetAttributeValue("href", "");
                if (!string.IsNullOrWhiteSpace(href) &&
                    !href.Contains("adservice", StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(href, @"-pm?-[A-Z0-9]+", RegexOptions.IgnoreCase))
                {
                    return href;
                }
            }
        }

        var allLinks = htmlDoc.DocumentNode.SelectNodes("//a[@href]");
        if (allLinks == null)
            return null;

        foreach (var link in allLinks)
        {
            var href = link.GetAttributeValue("href", "");
            if (!string.IsNullOrWhiteSpace(href) &&
                !href.Contains("adservice", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(href, @"-pm?-[A-Z0-9]+", RegexOptions.IgnoreCase))
            {
                return href;
            }
        }

        return null;
    }

    private async Task<string> GetPageHtmlAsync(string url, CancellationToken cancellationToken)
    {
        var encodedUrl = System.Net.WebUtility.UrlEncode(url);
        var apiUrl = $"{_config.BaseUrl}?url={encodedUrl}&token={_config.ApiToken}";

        var response = await _httpClient.GetAsync(apiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static int CalculateProgress(int completedCount, int totalCount)
    {
        if (totalCount <= 0)
            return 100;

        var progress = 10 + (int)((completedCount / (double)totalCount) * 80);
        return Math.Clamp(progress, 10, 90);
    }

    private static List<string> ReadProductNamesFromExcel(Stream excelStream)
    {
        var productNames = new List<string>();

        using var package = new ExcelPackage(excelStream);
        var worksheet = package.Workbook.Worksheets.FirstOrDefault();
        if (worksheet == null)
            return productNames;

        var rowCount = worksheet.Dimension?.Rows ?? 0;
        int startRow = 1;
        var firstCell = worksheet.Cells[1, 1].Value?.ToString()?.Trim() ?? string.Empty;
        if (firstCell.Equals("Product Name", StringComparison.OrdinalIgnoreCase) ||
            firstCell.Equals("Ürün Adý", StringComparison.OrdinalIgnoreCase) ||
            firstCell.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
            firstCell.Equals("Ürün", StringComparison.OrdinalIgnoreCase))
        {
            startRow = 2;
        }

        for (int row = startRow; row <= rowCount; row++)
        {
            var cellValue = worksheet.Cells[row, 1].Value?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(cellValue))
                productNames.Add(cellValue);
        }

        return productNames;
    }

    private async Task ExportResultsAsync(
        Func<int, string, string, Task> onProgress,
        List<ProductInfo> products,
        int foundCount,
        int totalCount,
        string? originalFileName,
        bool isPartial)
    {
        if (products.Count == 0)
        {
            await onProgress(100, "No products scraped", "warning");
            await SendComplete(onProgress, null, foundCount, totalCount);
            return;
        }

        await onProgress(95, $"?? Creating Excel report for {products.Count:N0} products...", "info");

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var prefix = string.IsNullOrEmpty(originalFileName)
            ? "HepsiburadaProductSearch"
            : Path.GetFileNameWithoutExtension(originalFileName);
        var suffix = isPartial ? $"_Partial_{timestamp}" : $"_{timestamp}";
        var fileName = $"{prefix}{suffix}.xlsx";
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

        new ExcelExporter().ExportToExcel(products, filePath);

        var summary = isPartial
            ? $"?? Partial export ready: {foundCount:N0}/{totalCount:N0} scraped"
            : $"? Done! {foundCount:N0}/{totalCount:N0} products scraped";
        await onProgress(100, summary, isPartial ? "warning" : "success");
        await SendComplete(onProgress, fileName, foundCount, totalCount);
    }

    private static string TruncateName(string name, int maxLength) =>
        string.IsNullOrEmpty(name)
            ? string.Empty
            : name.Length > maxLength ? name[..maxLength] + "..." : name;

    private static async Task SendComplete(Func<int, string, string, Task> onProgress, string? fileName, int foundCount, int totalCount)
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
