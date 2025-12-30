using Scrapper.Models;
using System.Collections.Concurrent;

namespace Scrapper.Services;

/// <summary>
/// Orchestrates the Akakce scraping workflow: read URLs, scrape, export
/// NOTE: Akakce uses Cloudflare protection, so we ALWAYS use Selenium regardless of user selection
/// </summary>
public class AkakceScraperService
{
    // Store cancellation tokens by session ID
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private static readonly Random _random = new Random();
    
    // Minimum delay between products to avoid triggering Cloudflare
    private const int MIN_DELAY_BETWEEN_PRODUCTS_MS = 3000;
    private const int MAX_DELAY_BETWEEN_PRODUCTS_MS = 6000;
    
    // Max retries for a single product before skipping
    private const int MAX_PRODUCT_RETRIES = 2;
    
    // Cooldown after Cloudflare block
    private const int CLOUDFLARE_COOLDOWN_MS = 30000;
    
    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            Console.WriteLine($"[AkakceService] Session {sessionId} cancelled");
        }
    }
    
    /// <summary>
    /// Process a category URL - extract product URLs then scrape each
    /// </summary>
    /// <param name="startFrom">Start scraping from this product number (1-based index). Products before this will be skipped.</param>
    public async Task ProcessCategoryUrlAsync(
        string categoryUrl,
        int maxProducts,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null,
        int startFrom = 1)
    {
        // Create cancellation token for this session
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessions[sessionId] = cts;
        }
        var cancellationToken = cts.Token;
        
        var products = new List<AkakceProductInfo>();
        AkakceScraper? scraper = null;
        
        try
        {
            await onProgress(1, "🔍 Starting Akakce category scraper...", "info");
            await onProgress(2, $"🌐 URL: {categoryUrl}", "info");
            await onProgress(3, $"🎯 Target: {maxProducts} products", "info");
            
            if (startFrom > 1)
            {
                await onProgress(4, $"⏭️ Starting from product #{startFrom} (skipping first {startFrom - 1})", "info");
            }
            else
            {
                await onProgress(4, "⚠️ Note: Akakce has Cloudflare protection. Delays added between products.", "info");
            }
            
            scraper = new AkakceScraper();
            scraper.Method = ScrapeMethod.Selenium;
            
            // Step 1: Extract product URLs from category page
            // Need to fetch enough URLs to cover both skipped and target products
            int totalUrlsNeeded = startFrom + maxProducts - 1;
            var productUrls = await scraper.GetProductUrlsFromCategoryAsync(categoryUrl, totalUrlsNeeded, onProgress);
            
            if (productUrls.Count == 0)
            {
                await onProgress(100, "❌ No product URLs found on the category page", "error");
                await SendComplete(onProgress, null, null);
                return;
            }

            // Validate startFrom parameter
            if (startFrom > productUrls.Count)
            {
                await onProgress(100, $"❌ Start position ({startFrom}) exceeds available products ({productUrls.Count})", "error");
                await SendComplete(onProgress, null, null);
                return;
            }

            // Skip products before startFrom
            var urlsToScrape = productUrls.Skip(startFrom - 1).Take(maxProducts).ToList();
            
            await onProgress(15, $"✅ Found {productUrls.Count} total products. Will scrape {urlsToScrape.Count} starting from #{startFrom}", "success");

            // NEW: Wait extra before starting first product to avoid Cloudflare
            int initialWaitSeconds = Math.Min(20, Math.Max(8, urlsToScrape.Count * 2));
            await onProgress(18, $"⏳ Waiting {initialWaitSeconds}s before starting product scraping...", "info");
            await Task.Delay(initialWaitSeconds * 1000);

            // Step 2: Scrape each product with significant delays and skip logic
            var progressPerProduct = 75.0 / urlsToScrape.Count;
            var currentProgress = 20.0;

            int successCount = 0;
            int errorCount = 0;
            int skippedCount = 0;
            
            for (int i = 0; i < urlsToScrape.Count; i++)
            {
                // Check for cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, $"⏹️ Stopped at product {startFrom + i}/{productUrls.Count}", "warning");
                    break;
                }
                
                var url = urlsToScrape[i];
                int absoluteProductNumber = startFrom + i;
                
                // Add delay between products (except first one)
                if (i > 0)
                {
                    var delayMs = _random.Next(MIN_DELAY_BETWEEN_PRODUCTS_MS, MAX_DELAY_BETWEEN_PRODUCTS_MS);
                    await onProgress((int)currentProgress, $"⏱️ Waiting {delayMs/1000}s to avoid Cloudflare...", "info");
                    await Task.Delay(delayMs);
                }
                
                await onProgress((int)currentProgress, $"📦 Scraping product #{absoluteProductNumber} ({i + 1}/{urlsToScrape.Count})...", "info");
                
                // Retry logic with automatic skip
                bool productScraped = false;
                AkakceProductInfo? product = null;
                int retryCount = 0;
                
                while (!productScraped && retryCount <= MAX_PRODUCT_RETRIES)
                {
                    try
                    {
                        product = await scraper.ScrapeProductAsync(url);
                        
                        if (product.IsSuccess)
                        {
                            successCount++;
                            var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 40
                                ? product.Name.Substring(0, 40) + "..."
                                : product.Name ?? "Unknown";
                            
                            await onProgress((int)currentProgress, $"✅ {displayName} ({product.SellerCount} sellers)", "success");
                            productScraped = true;
                        }
                        else
                        {
                            // Check if it's a Cloudflare block
                            if (product.ErrorMessage?.Contains("Cloudflare") == true)
                            {
                                retryCount++;
                                
                                if (retryCount <= MAX_PRODUCT_RETRIES)
                                {
                                    await onProgress((int)currentProgress, 
                                        $"🔄 Cloudflare block - retry {retryCount}/{MAX_PRODUCT_RETRIES} in 30s...", 
                                        "warning");
                                    await Task.Delay(CLOUDFLARE_COOLDOWN_MS);
                                }
                                else
                                {
                                    // Max retries reached - skip this product
                                    skippedCount++;
                                    await onProgress((int)currentProgress, 
                                        $"⏭️ Skipping product #{absoluteProductNumber} after {MAX_PRODUCT_RETRIES} Cloudflare blocks", 
                                        "warning");
                                    productScraped = true; // Exit retry loop
                                }
                            }
                            else
                            {
                                // Other error - count and skip
                                errorCount++;
                                await onProgress((int)currentProgress, $"❌ {product.ErrorMessage}", "error");
                                productScraped = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        
                        if (retryCount <= MAX_PRODUCT_RETRIES)
                        {
                            await onProgress((int)currentProgress, 
                                $"🔄 Error: {ex.Message} - retry {retryCount}/{MAX_PRODUCT_RETRIES}...", 
                                "warning");
                            await Task.Delay(5000); // Short delay before retry
                        }
                        else
                        {
                            // Max retries reached - create error product and skip
                            errorCount++;
                            skippedCount++;
                            product = new AkakceProductInfo
                            {
                                ProductUrl = url,
                                ErrorMessage = $"Skipped after {MAX_PRODUCT_RETRIES} retries: {ex.Message}",
                                ScrapedAt = DateTime.Now
                            };
                            await onProgress((int)currentProgress, 
                                $"⏭️ Skipping product #{absoluteProductNumber} after multiple errors", 
                                "warning");
                            productScraped = true;
                        }
                    }
                }
                
                // Add product to list (even if failed/skipped for reporting)
                if (product != null)
                {
                    products.Add(product);
                }
                
                currentProgress += progressPerProduct;
            }
            
            // Step 3: Export results
            if (products.Count > 0)
            {
                var stoppedText = cancellationToken.IsCancellationRequested ? " (stopped early)" : "";
                await onProgress(95, $"📊 Creating Excel report{stoppedText}...", "info");
                
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"Akakce_Category_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                
                try
                {
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(products, filePath);
                    
                    var totalSellers = products.Sum(p => p.SellerCount);
                    
                    var summary = $"✅ {successCount} products, {totalSellers} sellers scraped. " +
                                 $"{errorCount} errors. {skippedCount} skipped.";
                    
                    if (startFrom > 1)
                    {
                        summary += $" (Started from #{startFrom})";
                    }
                    
                    await onProgress(100, summary, "success");
                    await SendComplete(onProgress, fileName, successCount);
                }
                catch (Exception excelEx)
                {
                    await onProgress(100, $"❌ Excel error: {excelEx.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, "❌ No products scraped", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        catch (Exception ex)
        {
            // Try to save what we have
            if (products.Count > 0)
            {
                try
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var fileName = $"Akakce_Partial_{timestamp}.xlsx";
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(products, filePath);
                    
                    await onProgress(100, $"⚠️ Error but saved {products.Count} products", "warning");
                    await SendComplete(onProgress, fileName, products.Count);
                }
                catch
                {
                    await onProgress(100, $"❌ Error: {ex.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, $"❌ Error: {ex.Message}", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        finally
        {
            scraper?.Dispose();
            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessions.TryRemove(sessionId, out _);
            }
            cts.Dispose();
        }
    }
    
    /// <summary>
    /// Process an uploaded Excel file with Akakce URLs
    /// </summary>
    /// <param name="startFrom">Start scraping from this row number (1-based index). URLs before this will be skipped.</param>
    public async Task ProcessExcelFileAsync(
        Stream excelStream,
        ScrapeMethod scrapeMethod,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null,
        int startFrom = 1)
    {
        // Create cancellation token for this session
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessions[sessionId] = cts;
        }
        var cancellationToken = cts.Token;
        
        var products = new List<AkakceProductInfo>();
        AkakceScraper? scraper = null;
        
        try
        {
            // IMPORTANT: Akakce uses Cloudflare protection, so Scrape.do won't work!
            await onProgress(0, "⚠️ Akakce has Cloudflare protection. Using Selenium with delays.", "info");
            await onProgress(1, "Starting Akakce scraper (Selenium)...", "info");

            // Step 1: Read URLs from Excel
            await onProgress(2, "Reading URLs from Excel file...", "info");
            
            var reader = new AkakceExcelReader();
            excelStream.Position = 0;
            var urlColumn = reader.DetectUrlColumn(excelStream, hasHeader: true);
            excelStream.Position = 0;
            var urls = reader.ReadUrlsFromStream(excelStream, urlColumn, hasHeader: true);
            
            Console.WriteLine($"\n[AkakceService] ========== SCRAPING SESSION START ==========");
            Console.WriteLine($"[AkakceService] Total URLs found: {urls.Count}");
            
            if (urls.Count == 0)
            {
                await onProgress(100, "No valid Akakce URLs found in the Excel file", "error");
                await SendComplete(onProgress, null, null);
                return;
            }

            // Validate startFrom parameter
            if (startFrom > urls.Count)
            {
                await onProgress(100, $"❌ Start position ({startFrom}) exceeds available URLs ({urls.Count})", "error");
                await SendComplete(onProgress, null, null);
                return;
            }

            if (urls.Count > 500)
            {
                await onProgress(5, $"Limiting to first 500 URLs", "info");
                urls = urls.Take(500).ToList();
            }

            // Skip URLs before startFrom
            var urlsToScrape = urls.Skip(startFrom - 1).ToList();
            
            if (startFrom > 1)
            {
                await onProgress(5, $"Found {urls.Count} valid Akakce URLs. Will scrape {urlsToScrape.Count} starting from row #{startFrom}", "success");
            }
            else
            {
                await onProgress(5, $"Found {urls.Count} valid Akakce URLs", "success");
            }

            // Step 2: Scrape each URL with delays and skip logic
            var progressPerProduct = 85.0 / urlsToScrape.Count;
            var currentProgress = 10.0;

            scraper = new AkakceScraper();
            scraper.Method = ScrapeMethod.Selenium;

            int successCount = 0;
            int errorCount = 0;
            int skippedCount = 0;

            for (int i = 0; i < urlsToScrape.Count; i++)
            {
                // Check for cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, $"⏹️ Stopped at row #{startFrom + i}/{urls.Count}", "warning");
                    break;
                }
                
                var url = urlsToScrape[i];
                int absoluteRowNumber = startFrom + i;
                
                // Add delay between products (except first one)
                if (i > 0)
                {
                    var delayMs = _random.Next(MIN_DELAY_BETWEEN_PRODUCTS_MS, MAX_DELAY_BETWEEN_PRODUCTS_MS);
                    await onProgress((int)currentProgress, $"⏱️ Waiting {delayMs/1000}s to avoid Cloudflare...", "info");
                    await Task.Delay(delayMs);
                }
                
                await onProgress((int)currentProgress, $"📦 Scraping row #{absoluteRowNumber} ({i + 1}/{urlsToScrape.Count})...", "info");

                // Retry logic with automatic skip
                bool productScraped = false;
                AkakceProductInfo? product = null;
                int retryCount = 0;
                
                while (!productScraped && retryCount <= MAX_PRODUCT_RETRIES)
                {
                    try
                    {
                        product = await scraper.ScrapeProductAsync(url);
                        
                        if (product.IsSuccess)
                        {
                            successCount++;
                            var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 40
                                ? product.Name.Substring(0, 40) + "..."
                                : product.Name ?? "Unknown";
                            
                            await onProgress((int)currentProgress, $"✅ {displayName} ({product.SellerCount} sellers)", "success");
                            productScraped = true;
                        }
                        else
                        {
                            // Check if it's a Cloudflare block
                            if (product.ErrorMessage?.Contains("Cloudflare") == true)
                            {
                                retryCount++;
                                
                                if (retryCount <= MAX_PRODUCT_RETRIES)
                                {
                                    await onProgress((int)currentProgress, 
                                        $"🔄 Cloudflare block - retry {retryCount}/{MAX_PRODUCT_RETRIES} in 30s...", 
                                        "warning");
                                    await Task.Delay(CLOUDFLARE_COOLDOWN_MS);
                                }
                                else
                                {
                                    // Max retries reached - skip this product
                                    skippedCount++;
                                    await onProgress((int)currentProgress, 
                                        $"⏭️ Skipping row #{absoluteRowNumber} after {MAX_PRODUCT_RETRIES} Cloudflare blocks", 
                                        "warning");
                                    productScraped = true; // Exit retry loop
                                }
                            }
                            else
                            {
                                // Other error - count and skip
                                errorCount++;
                                await onProgress((int)currentProgress, $"❌ {product.ErrorMessage}", "error");
                                productScraped = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        
                        if (retryCount <= MAX_PRODUCT_RETRIES)
                        {
                            await onProgress((int)currentProgress, 
                                $"🔄 Error: {ex.Message} - retry {retryCount}/{MAX_PRODUCT_RETRIES}...", 
                                "warning");
                            await Task.Delay(5000); // Short delay before retry
                        }
                        else
                        {
                            // Max retries reached - create error product and skip
                            errorCount++;
                            skippedCount++;
                            product = new AkakceProductInfo
                            {
                                ProductUrl = url,
                                ErrorMessage = $"Skipped after {MAX_PRODUCT_RETRIES} retries: {ex.Message}",
                                ScrapedAt = DateTime.Now
                            };
                            await onProgress((int)currentProgress, 
                                $"⏭️ Skipping row #{absoluteRowNumber} after multiple errors", 
                                "warning");
                            productScraped = true;
                        }
                    }
                }
                
                // Add product to list (even if failed/skipped for reporting)
                if (product != null)
                {
                    products.Add(product);
                }

                currentProgress += progressPerProduct;
            }

            Console.WriteLine($"\n[AkakceService] ========== SCRAPING COMPLETE ==========");
            Console.WriteLine($"[AkakceService] Total products: {products.Count}");
            Console.WriteLine($"[AkakceService] Successful: {successCount}");
            Console.WriteLine($"[AkakceService] Skipped: {skippedCount}");

            // Step 3: Export results (even if stopped early)
            if (products.Count > 0)
            {
                var stoppedText = cancellationToken.IsCancellationRequested ? " (stopped early)" : "";
                await onProgress(95, $"📊 Creating Excel report{stoppedText}...", "info");

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"Akakce_Results_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                try
                {
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(products, filePath);

                    var totalSellers = products.Sum(p => p.SellerCount);
                    
                    var summary = $"✅ {successCount} products, {totalSellers} sellers. " +
                                 $"{errorCount} errors. {skippedCount} skipped.";
                    
                    if (startFrom > 1)
                    {
                        summary += $" (Started from row #{startFrom})";
                    }
                    
                    await onProgress(100, summary, "success");
                    await SendComplete(onProgress, fileName, successCount);
                }
                catch (Exception excelEx)
                {
                    await onProgress(100, $"❌ Excel error: {excelEx.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, "No products scraped", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        catch (Exception ex)
        {
            // Try to save what we have
            if (products.Count > 0)
            {
                try
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var fileName = $"Akakce_Partial_{timestamp}.xlsx";
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(products, filePath);
                    
                    await onProgress(100, $"⚠️ Error but saved {products.Count} products", "warning");
                    await SendComplete(onProgress, fileName, products.Count);
                }
                catch
                {
                    await onProgress(100, $"❌ Error: {ex.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, $"❌ Error: {ex.Message}", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        finally
        {
            // Cleanup
            scraper?.Dispose();
            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessions.TryRemove(sessionId, out _);
            }
            cts.Dispose();
        }
    }

    private async Task SendComplete(Func<int, string, string, Task> onProgress, string? fileName, int? productCount)
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
