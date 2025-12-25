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
    public async Task ProcessCategoryUrlAsync(
        string categoryUrl,
        int maxProducts,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null)
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
            await onProgress(1, "?? Starting Akakce category scraper...", "info");
            await onProgress(2, $"?? URL: {categoryUrl}", "info");
            await onProgress(3, $"?? Target: {maxProducts} products", "info");
            await onProgress(4, "?? Note: Akakce has Cloudflare protection. Delays added between products.", "info");
            
            scraper = new AkakceScraper();
            scraper.Method = ScrapeMethod.Selenium;
            
            // Step 1: Extract product URLs from category page
            var productUrls = await scraper.GetProductUrlsFromCategoryAsync(categoryUrl, maxProducts, onProgress);
            
            if (productUrls.Count == 0)
            {
                await onProgress(100, "? No product URLs found on the category page", "error");
                await SendComplete(onProgress, null, null);
                return;
            }
            
            await onProgress(15, $"? Found {productUrls.Count} products to scrape", "success");
            
            // Step 2: Scrape each product with significant delays
            var progressPerProduct = 75.0 / productUrls.Count;
            var currentProgress = 20.0;
            
            int successCount = 0;
            int errorCount = 0;
            
            for (int i = 0; i < productUrls.Count; i++)
            {
                // Check for cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, $"?? Stopped at product {i}/{productUrls.Count}", "warning");
                    break;
                }
                
                var url = productUrls[i];
                
                // Add delay between products (except first one)
                if (i > 0)
                {
                    var delayMs = _random.Next(MIN_DELAY_BETWEEN_PRODUCTS_MS, MAX_DELAY_BETWEEN_PRODUCTS_MS);
                    await onProgress((int)currentProgress, $"?? Waiting {delayMs/1000}s to avoid Cloudflare...", "info");
                    await Task.Delay(delayMs);
                }
                
                await onProgress((int)currentProgress, $"Scraping product {i + 1}/{productUrls.Count}...", "info");
                
                try
                {
                    var product = await scraper.ScrapeProductAsync(url);
                    products.Add(product);
                    
                    if (product.IsSuccess)
                    {
                        successCount++;
                        var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 40
                            ? product.Name.Substring(0, 40) + "..."
                            : product.Name ?? "Unknown";
                        
                        await onProgress((int)currentProgress, $"? {displayName} ({product.SellerCount} sellers)", "success");
                    }
                    else
                    {
                        errorCount++;
                        await onProgress((int)currentProgress, $"? {product.ErrorMessage}", "error");
                        
                        // If we got a Cloudflare block, add extra delay
                        if (product.ErrorMessage?.Contains("Cloudflare") == true)
                        {
                            await onProgress((int)currentProgress, "? Cloudflare detected - adding 30s cooldown...", "warning");
                            await Task.Delay(30000); // 30 second cooldown
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                    products.Add(new AkakceProductInfo
                    {
                        ProductUrl = url,
                        ErrorMessage = ex.Message,
                        ScrapedAt = DateTime.Now
                    });
                    await onProgress((int)currentProgress, $"? {ex.Message}", "error");
                }
                
                currentProgress += progressPerProduct;
            }
            
            // Step 3: Export results
            if (products.Count > 0)
            {
                var stoppedText = cancellationToken.IsCancellationRequested ? " (stopped early)" : "";
                await onProgress(95, $"?? Creating Excel report{stoppedText}...", "info");
                
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"Akakce_Category_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                
                try
                {
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(products, filePath);
                    
                    var totalSellers = products.Sum(p => p.SellerCount);
                    
                    await onProgress(100, $"? {successCount} products, {totalSellers} sellers scraped. {errorCount} errors.", "success");
                    await SendComplete(onProgress, fileName, successCount);
                }
                catch (Exception excelEx)
                {
                    await onProgress(100, $"? Excel error: {excelEx.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, "? No products scraped", "error");
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
                    
                    await onProgress(100, $"?? Error but saved {products.Count} products", "warning");
                    await SendComplete(onProgress, fileName, products.Count);
                }
                catch
                {
                    await onProgress(100, $"? Error: {ex.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, $"? Error: {ex.Message}", "error");
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
    public async Task ProcessExcelFileAsync(
        Stream excelStream,
        ScrapeMethod scrapeMethod,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null)
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
            await onProgress(0, "?? Akakce has Cloudflare protection. Using Selenium with delays.", "info");
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

            await onProgress(5, $"Found {urls.Count} valid Akakce URLs", "success");

            if (urls.Count > 500)
            {
                await onProgress(5, $"Limiting to first 500 URLs", "info");
                urls = urls.Take(500).ToList();
            }

            // Step 2: Scrape each URL with delays
            var progressPerProduct = 85.0 / urls.Count;
            var currentProgress = 10.0;

            scraper = new AkakceScraper();
            scraper.Method = ScrapeMethod.Selenium;

            int successCount = 0;
            int errorCount = 0;

            for (int i = 0; i < urls.Count; i++)
            {
                // Check for cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, $"?? Stopped at product {i}/{urls.Count}", "warning");
                    break;
                }
                
                var url = urls[i];
                
                // Add delay between products (except first one)
                if (i > 0)
                {
                    var delayMs = _random.Next(MIN_DELAY_BETWEEN_PRODUCTS_MS, MAX_DELAY_BETWEEN_PRODUCTS_MS);
                    await onProgress((int)currentProgress, $"?? Waiting {delayMs/1000}s to avoid Cloudflare...", "info");
                    await Task.Delay(delayMs);
                }
                
                await onProgress((int)currentProgress, $"Scraping product {i + 1} of {urls.Count}...", "info");

                try
                {
                    var product = await scraper.ScrapeProductAsync(url);
                    products.Add(product);

                    if (product.IsSuccess)
                    {
                        successCount++;
                        var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 40
                            ? product.Name.Substring(0, 40) + "..."
                            : product.Name ?? "Unknown";
                        
                        await onProgress((int)currentProgress, $"? {displayName} ({product.SellerCount} sellers)", "success");
                    }
                    else
                    {
                        errorCount++;
                        await onProgress((int)currentProgress, $"? {product.ErrorMessage}", "error");
                        
                        // If we got a Cloudflare block, add extra delay
                        if (product.ErrorMessage?.Contains("Cloudflare") == true)
                        {
                            await onProgress((int)currentProgress, "? Cloudflare detected - adding 30s cooldown...", "warning");
                            await Task.Delay(30000);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                    products.Add(new AkakceProductInfo
                    {
                        ProductUrl = url,
                        ErrorMessage = ex.Message,
                        ScrapedAt = DateTime.Now
                    });
                    
                    await onProgress((int)currentProgress, $"? {ex.Message}", "error");
                }

                currentProgress += progressPerProduct;
            }

            Console.WriteLine($"\n[AkakceService] ========== SCRAPING COMPLETE ==========");
            Console.WriteLine($"[AkakceService] Total products: {products.Count}");
            Console.WriteLine($"[AkakceService] Successful: {successCount}");

            // Step 3: Export results (even if stopped early)
            if (products.Count > 0)
            {
                var stoppedText = cancellationToken.IsCancellationRequested ? " (stopped early)" : "";
                await onProgress(95, $"Creating Excel report{stoppedText}...", "info");

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"Akakce_Results_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                try
                {
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(products, filePath);

                    var totalSellers = products.Sum(p => p.SellerCount);
                    
                    await onProgress(100, $"? {successCount} products, {totalSellers} sellers. {errorCount} errors.", "success");
                    await SendComplete(onProgress, fileName, successCount);
                }
                catch (Exception excelEx)
                {
                    await onProgress(100, $"? Excel error: {excelEx.Message}", "error");
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
                    
                    await onProgress(100, $"?? Error but saved {products.Count} products", "warning");
                    await SendComplete(onProgress, fileName, products.Count);
                }
                catch
                {
                    await onProgress(100, $"? Error: {ex.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, $"? Error: {ex.Message}", "error");
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
