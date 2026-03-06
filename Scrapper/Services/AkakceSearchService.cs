using Scrapper.Models;
using System.Collections.Concurrent;
using OfficeOpenXml;

namespace Scrapper.Services;

public class AkakceSearchService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private const int MAX_RETRIES = 3; // Retry failed searches up to 3 times
    private const int CHECKPOINT_INTERVAL = 50; // Save progress every 50 products
    private const int CONNECTION_CHECK_INTERVAL = 100; // Check Edge connection every 100 products
    private const int MAX_CONSECUTIVE_FAILURES = 3; // Re-warmup after this many back-to-back failures

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

            int successCount = 0;
            int failedCount = 0;
            int retryCount = 0;
            int consecutiveFailures = 0;

            for (int i = 0; i < productNames.Count; i++)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, "⛔ Search stopped by user", "warning");
                    break;
                }

                var productName = productNames[i];
                await onProgress((int)currentProgress, $"🔍 [{i + 1}/{productNames.Count}] {TruncateName(productName, 50)}... (✓{successCount} ✗{failedCount} 🔄{retryCount})", "info");

                // Periodic connection health check
                if (i > 0 && i % CONNECTION_CHECK_INTERVAL == 0)
                {
                    await onProgress((int)currentProgress, $"🔗 Connection health check... ({i} products processed)", "info");
                    try
                    {
                        // Try a simple operation to verify connection is still alive
                        var testUrl = scraper.GetType().GetProperty("Method")?.GetValue(scraper);
                        Console.WriteLine($"[AkakceSearch] Connection OK at product {i}");
                    }
                    catch (Exception checkEx)
                    {
                        Console.WriteLine($"[AkakceSearch] Connection check failed: {checkEx.Message}");
                        await onProgress((int)currentProgress, "⚠️ Connection issue detected, may need to reconnect...", "warning");
                    }
                }

                // Checkpoint progress periodically
                if (i > 0 && i % CHECKPOINT_INTERVAL == 0 && products.Count > 0)
                {
                    await onProgress((int)currentProgress, $"💾 Progress checkpoint: {i}/{productNames.Count} processed...", "info");
                    try
                    {
                        var checkpointFile = $"AkakceSearch_Checkpoint_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                        var checkpointPath = Path.Combine(Directory.GetCurrentDirectory(), checkpointFile);
                        var exporter = new AkakceExcelExporter();
                        exporter.Export(products, checkpointPath);
                        Console.WriteLine($"[AkakceSearch] Checkpoint saved: {checkpointFile}");
                    }
                    catch (Exception cpEx)
                    {
                        Console.WriteLine($"[AkakceSearch] Checkpoint save failed: {cpEx.Message}");
                    }
                }

                // Retry logic for failed searches
                AkakceProductInfo? product = null;
                string? productUrl = null;
                bool searchSuccess = false;
                int attemptNum = 0;

                for (attemptNum = 1; attemptNum <= MAX_RETRIES && !searchSuccess; attemptNum++)
                {
                    try
                    {
                        if (attemptNum > 1)
                        {
                            retryCount++;
                            await onProgress((int)currentProgress, $"🔄 Retry {attemptNum}/{MAX_RETRIES} for: {TruncateName(productName, 40)}...", "warning");
                            await Task.Delay(3000 * attemptNum); // Exponential backoff
                        }

                        productUrl = await scraper.SearchProductAsync(productName);

                        if (string.IsNullOrEmpty(productUrl))
                        {
                            if (attemptNum == MAX_RETRIES)
                            {
                                await onProgress((int)currentProgress, $"⚠️ No results after {MAX_RETRIES} tries: {TruncateName(productName, 40)}", "warning");
                                product = new AkakceProductInfo
                                {
                                    Name = productName,
                                    ErrorMessage = $"No search results found after {MAX_RETRIES} attempts (may be Cloudflare blocked)"
                                };
                                failedCount++;
                            }
                            continue; // Try again
                        }

                        await onProgress((int)currentProgress, "📊 Found product, scraping sellers...", "info");

                        product = await scraper.ScrapeProductAsync(productUrl, scanVariants);
                        product.Description = $"Search term: {productName}";
                        searchSuccess = true;
                        successCount++;

                        var sellerInfo = product.HasVariants 
                            ? $"{product.Variants.Count} variants, {product.Variants.Sum(v => v.SellerCount)} sellers"
                            : $"{product.SellerCount} sellers";
                        
                        await onProgress((int)currentProgress, $"✅ {TruncateName(product.Name, 40)}: {sellerInfo}", "success");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AkakceSearch] Attempt {attemptNum} failed for '{productName}': {ex.Message}");
                        
                        if (attemptNum == MAX_RETRIES)
                        {
                            await onProgress((int)currentProgress, $"❌ Failed after {MAX_RETRIES} tries '{TruncateName(productName, 30)}': {ex.Message}", "error");
                            product = new AkakceProductInfo
                            {
                                Name = productName,
                                ErrorMessage = $"Failed after {MAX_RETRIES} attempts: {ex.Message}"
                            };
                            failedCount++;
                        }
                    }
                }

                if (product != null)
                {
                    products.Add(product);
                }

                // Track consecutive failures; re-warmup the browser when too many stack up
                if (searchSuccess)
                {
                    consecutiveFailures = 0;
                }
                else
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= MAX_CONSECUTIVE_FAILURES && !cts.Token.IsCancellationRequested)
                    {
                        await onProgress((int)currentProgress,
                            $"⚠️ {consecutiveFailures} consecutive failures — pausing 20s and reconnecting browser...",
                            "warning");
                        await Task.Delay(20000);
                        var rewarmSuccess = await scraper.WarmupAsync(onProgress);
                        await onProgress((int)currentProgress,
                            rewarmSuccess ? "✅ Browser reconnected, resuming..." : "⚠️ Reconnect failed, continuing anyway...",
                            rewarmSuccess ? "success" : "warning");
                        if (rewarmSuccess)
                            consecutiveFailures = 0;
                    }
                }

                currentProgress += progressPerProduct;
                
                // Add a small delay between searches to avoid rate limiting
                await Task.Delay(500);
            }

            if (products.Count > 0)
            {
                await onProgress(95, "📊 Creating Excel report...", "info");

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"AkakceSearch_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                var exporter = new AkakceExcelExporter();
                exporter.Export(products, filePath);

                var finalSuccessCount = products.Count(p => p.IsSuccess);
                var finalFailedCount = products.Count - finalSuccessCount;
                await onProgress(100, $"✅ Done! {finalSuccessCount} succeeded, {finalFailedCount} failed (Total attempts: {retryCount} retries)", "success");
                await SendComplete(onProgress, fileName, finalSuccessCount);
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
