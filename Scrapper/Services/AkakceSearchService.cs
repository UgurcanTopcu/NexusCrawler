using Scrapper.Models;
using System.Collections.Concurrent;

namespace Scrapper.Services;

/// <summary>
/// Service to search products by name on Akakce and scrape seller information
/// </summary>
public class AkakceSearchService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();

    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            Console.WriteLine($"[AkakceSearch] Session {sessionId} cancelled");
        }
    }

    /// <summary>
    /// Search for products by name from Excel file and scrape seller information
    /// </summary>
    public async Task SearchAndScrapeFromExcelAsync(
        Stream excelStream,
        bool scanVariants,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessions[sessionId] = cts;
        }
        var cancellationToken = cts.Token;

        var products = new List<AkakceProductInfo>();

        try
        {
            await onProgress(1, "?? Reading Excel file...", "info");

            // Read product names from Excel
            var productNames = ReadProductNamesFromExcel(excelStream);

            if (productNames.Count == 0)
            {
                await onProgress(100, "?? No product names found in the Excel file", "warning");
                await SendComplete(onProgress, null, 0);
                return;
            }

            await onProgress(5, $"? Found {productNames.Count} product names to search", "success");

            using var scraper = new AkakceScraper();

            var progressPerProduct = 85.0 / productNames.Count;
            var currentProgress = 10.0;

            for (int i = 0; i < productNames.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, "?? Search stopped by user", "warning");
                    break;
                }

                var productName = productNames[i];
                await onProgress((int)currentProgress, $"?? Searching {i + 1}/{productNames.Count}: {TruncateName(productName, 50)}...", "info");

                try
                {
                    // Search for the product
                    var productUrl = await scraper.SearchProductAsync(productName);

                    if (string.IsNullOrEmpty(productUrl))
                    {
                        await onProgress((int)currentProgress, $"?? No results for: {TruncateName(productName, 40)}", "warning");
                        
                        // Add empty result to track failed searches
                        products.Add(new AkakceProductInfo
                        {
                            Name = productName,
                            ErrorMessage = "No search results found"
                        });
                        
                        currentProgress += progressPerProduct;
                        continue;
                    }

                    await onProgress((int)currentProgress, $"?? Found product, scraping sellers...", "info");

                    // Scrape the product page
                    var product = await scraper.ScrapeProductAsync(productUrl, scanVariants);
                    
                    // Store original search term
                    product.Description = $"Search term: {productName}";
                    
                    products.Add(product);

                    var sellerInfo = product.HasVariants 
                        ? $"{product.Variants.Count} variants, {product.Variants.Sum(v => v.SellerCount)} sellers"
                        : $"{product.SellerCount} sellers";
                    
                    await onProgress((int)currentProgress, $"? {TruncateName(product.Name, 40)}: {sellerInfo}", "success");
                }
                catch (Exception ex)
                {
                    await onProgress((int)currentProgress, $"? Error searching '{TruncateName(productName, 30)}': {ex.Message}", "error");
                    
                    products.Add(new AkakceProductInfo
                    {
                        Name = productName,
                        ErrorMessage = ex.Message
                    });
                }

                currentProgress += progressPerProduct;
            }

            // Export results
            if (products.Count > 0)
            {
                await onProgress(95, "?? Creating Excel report...", "info");

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"AkakceSearch_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                var exporter = new AkakceExcelExporter();
                exporter.Export(products, filePath);

                var successCount = products.Count(p => p.IsSuccess);
                var summary = $"? Done! {successCount}/{products.Count} products found";
                
                await onProgress(100, summary, "success");
                await SendComplete(onProgress, fileName, successCount);
            }
            else
            {
                await onProgress(100, "No products found", "warning");
                await SendComplete(onProgress, null, 0);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AkakceSearch] Fatal error: {ex.Message}");
            await onProgress(100, $"? Error: {ex.Message}", "error");
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
    /// Read product names from first column of Excel file
    /// </summary>
    private List<string> ReadProductNamesFromExcel(Stream excelStream)
    {
        var productNames = new List<string>();

        try
        {
            using var package = new OfficeOpenXml.ExcelPackage(excelStream);
            var worksheet = package.Workbook.Worksheets.FirstOrDefault();

            if (worksheet == null)
            {
                Console.WriteLine("[AkakceSearch] No worksheet found in Excel");
                return productNames;
            }

            var rowCount = worksheet.Dimension?.Rows ?? 0;
            Console.WriteLine($"[AkakceSearch] Excel has {rowCount} rows");

            // Start from row 1 (assuming no header, or header is a product name too)
            // Skip if first row looks like a header
            int startRow = 1;
            var firstCell = worksheet.Cells[1, 1].Value?.ToString()?.Trim() ?? "";
            if (firstCell.Equals("Product Name", StringComparison.OrdinalIgnoreCase) ||
                firstCell.Equals("Ürün Adý", StringComparison.OrdinalIgnoreCase) ||
                firstCell.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                firstCell.Equals("Ürün", StringComparison.OrdinalIgnoreCase))
            {
                startRow = 2;
                Console.WriteLine("[AkakceSearch] Detected header row, starting from row 2");
            }

            for (int row = startRow; row <= rowCount; row++)
            {
                var cellValue = worksheet.Cells[row, 1].Value?.ToString()?.Trim();
                
                if (!string.IsNullOrWhiteSpace(cellValue))
                {
                    productNames.Add(cellValue);
                }
            }

            Console.WriteLine($"[AkakceSearch] Found {productNames.Count} product names");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AkakceSearch] Error reading Excel: {ex.Message}");
        }

        return productNames;
    }

    private string TruncateName(string name, int maxLength)
    {
        if (string.IsNullOrEmpty(name)) return "";
        return name.Length > maxLength ? name.Substring(0, maxLength) + "..." : name;
    }

    private async Task SendComplete(Func<int, string, string, Task> onProgress, string? fileName, int productCount)
    {
        var data = new
        {
            complete = true,
            downloadUrl = fileName != null ? $"/api/download/{fileName}" : null,
            fileName = fileName,
            productCount = productCount
        };

        var json = System.Text.Json.JsonSerializer.Serialize(data);
        await onProgress(100, json, "complete");
    }
}
