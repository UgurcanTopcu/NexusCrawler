using Scrapper.Models;
using System.Collections.Concurrent;
using OfficeOpenXml;

namespace Scrapper.Services;

public class AkakceSearchService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();

    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
            cts.Cancel();
    }

    public async Task SearchAndScrapeFromExcelAsync(
        Stream excelStream,
        bool scanVariants,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;

        var products = new List<AkakceProductInfo>();

        try
        {
            await onProgress(1, "📂 Reading Excel file...", "info");

            var productNames = ReadProductNamesFromExcel(excelStream);

            if (productNames.Count == 0)
            {
                await onProgress(100, "⚠️ No product names found in the Excel file", "warning");
                await SendComplete(onProgress, null, 0);
                return;
            }

            await onProgress(5, $"✅ Found {productNames.Count} product names to search", "success");

            using var scraper = new AkakceScraper();

            await onProgress(6, "🔗 Connecting to your Edge browser...", "info");
            var warmupSuccess = await scraper.WarmupAsync(onProgress);
            
            if (!warmupSuccess)
            {
                await onProgress(100, "❌ Could not connect to Edge. See console for setup instructions.", "error");
                await SendComplete(onProgress, null, 0);
                return;
            }

            var progressPerProduct = 82.0 / productNames.Count;
            var currentProgress = 12.0;

            for (int i = 0; i < productNames.Count; i++)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, "⛔ Search stopped by user", "warning");
                    break;
                }

                var productName = productNames[i];
                await onProgress((int)currentProgress, $"🔍 Searching {i + 1}/{productNames.Count}: {TruncateName(productName, 50)}...", "info");

                try
                {
                    var productUrl = await scraper.SearchProductAsync(productName);

                    if (string.IsNullOrEmpty(productUrl))
                    {
                        await onProgress((int)currentProgress, $"⚠️ No results for: {TruncateName(productName, 40)}", "warning");
                        products.Add(new AkakceProductInfo
                        {
                            Name = productName,
                            ErrorMessage = "No search results found"
                        });
                        currentProgress += progressPerProduct;
                        continue;
                    }

                    await onProgress((int)currentProgress, "📊 Found product, scraping sellers...", "info");

                    var product = await scraper.ScrapeProductAsync(productUrl, scanVariants);
                    product.Description = $"Search term: {productName}";
                    products.Add(product);

                    var sellerInfo = product.HasVariants 
                        ? $"{product.Variants.Count} variants, {product.Variants.Sum(v => v.SellerCount)} sellers"
                        : $"{product.SellerCount} sellers";
                    
                    await onProgress((int)currentProgress, $"✅ {TruncateName(product.Name, 40)}: {sellerInfo}", "success");
                }
                catch (Exception ex)
                {
                    await onProgress((int)currentProgress, $"❌ Error searching '{TruncateName(productName, 30)}': {ex.Message}", "error");
                    products.Add(new AkakceProductInfo
                    {
                        Name = productName,
                        ErrorMessage = ex.Message
                    });
                }

                currentProgress += progressPerProduct;
            }

            if (products.Count > 0)
            {
                await onProgress(95, "📊 Creating Excel report...", "info");

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"AkakceSearch_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                var exporter = new AkakceExcelExporter();
                exporter.Export(products, filePath);

                var successCount = products.Count(p => p.IsSuccess);
                await onProgress(100, $"✅ Done! {successCount}/{products.Count} products found", "success");
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
            await onProgress(100, $"❌ Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, 0);
        }
        finally
        {
            if (!string.IsNullOrEmpty(sessionId))
                _sessions.TryRemove(sessionId, out _);
            cts.Dispose();
        }
    }

    private List<string> ReadProductNamesFromExcel(Stream excelStream)
    {
        var productNames = new List<string>();

        try
        {
            using var package = new ExcelPackage(excelStream);
            var worksheet = package.Workbook.Worksheets.FirstOrDefault();

            if (worksheet == null)
                return productNames;

            var rowCount = worksheet.Dimension?.Rows ?? 0;
            int startRow = 1;
            var firstCell = worksheet.Cells[1, 1].Value?.ToString()?.Trim() ?? "";
            
            if (firstCell.Equals("Product Name", StringComparison.OrdinalIgnoreCase) ||
                firstCell.Equals("Ürün Adı", StringComparison.OrdinalIgnoreCase) ||
                firstCell.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                firstCell.Equals("Ürün", StringComparison.OrdinalIgnoreCase))
                startRow = 2;

            for (int row = startRow; row <= rowCount; row++)
            {
                var cellValue = worksheet.Cells[row, 1].Value?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(cellValue))
                    productNames.Add(cellValue);
            }
        }
        catch { }

        return productNames;
    }

    private string TruncateName(string name, int maxLength) =>
        string.IsNullOrEmpty(name) ? "" : 
        name.Length > maxLength ? name.Substring(0, maxLength) + "..." : name;

    private async Task SendComplete(Func<int, string, string, Task> onProgress, string? fileName, int productCount)
    {
        var data = new
        {
            complete = true,
            downloadUrl = fileName != null ? $"/api/download/{fileName}" : null,
            fileName,
            productCount
        };

        await onProgress(100, System.Text.Json.JsonSerializer.Serialize(data), "complete");
    }
}
