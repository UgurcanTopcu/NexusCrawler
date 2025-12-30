using Scrapper.Models;
using System.Collections.Concurrent;

namespace Scrapper.Services;

public class HepsiburadaScraperService
{
    // Store cancellation tokens by session ID
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    
    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            Console.WriteLine($"[HepsiburadaService] Session {sessionId} cancelled");
        }
    }
    
    public async Task ScrapeWithProgressAsync(
        string categoryUrl,
        int maxProducts,
        bool excludePrice,
        ScrapeMethod scrapeMethod,
        bool processImages,
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
        
        var products = new List<ProductInfo>();
        
        try
        {
            // Validate maxProducts to prevent excessive scraping
            if (maxProducts > 2000)
            {
                await onProgress(0, "?? Maximum 2000 products allowed per session. Limiting to 2000.", "warning");
                maxProducts = 2000;
            }
            
            var methodName = scrapeMethod == ScrapeMethod.ScrapeDo ? "Scrape.do API" : "Selenium";
            await onProgress(0, $"Initializing scraper ({methodName})...", "info");
            
            using var scraper = new HepsiburadaScraper();
            scraper.Method = scrapeMethod;
            
            await onProgress(5, "Fetching product links...", "info");
            var productLinks = await scraper.GetProductLinksAsync(categoryUrl, maxProducts, onProgress);
            
            if (productLinks.Count == 0)
            {
                await onProgress(100, "No products found at the given URL", "error");
                await SendComplete(onProgress, null, null);
                return;
            }
            
            var linksToProcess = productLinks.Take(maxProducts).ToList();
            await onProgress(10, $"Found {productLinks.Count} products, will scrape {linksToProcess.Count}", "info");
            
            var progressPerProduct = 80.0 / linksToProcess.Count;
            var currentProgress = 10.0;
            
            // Initialize image services ONCE if needed
            FtpUploadService? ftpService = null;
            HttpClient? httpClient = null;
            ImageProcessingService? imageService = null;
            CdnCacheService? cdnCache = null;
            
            if (processImages)
            {
                var ftpConfig = new CdnFtpConfig();
                ftpService = new FtpUploadService(ftpConfig);
                httpClient = new HttpClient();
                cdnCache = new CdnCacheService(ftpConfig);
                imageService = new ImageProcessingService(httpClient, ftpService, cdnCache);
                
                await onProgress(8, "?? Loading CDN cache...", "info");
                await imageService.InitializeCacheAsync();
                
                var (siteCount, productCount) = cdnCache.GetCacheStats();
                await onProgress(9, $"? CDN cache ready: {productCount} products", "info");
            }
            
            // Process each product
            for (int i = 0; i < linksToProcess.Count; i++)
            {
                // Check for cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, $"?? Stopped at product {i}/{linksToProcess.Count}", "warning");
                    break;
                }
                
                var link = linksToProcess[i];
                await onProgress((int)currentProgress, $"Scraping product {i + 1} of {linksToProcess.Count}...", "info");
                
                var product = await scraper.GetProductDetailsAsync(link);
                if (product != null)
                {
                    var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 50 
                        ? product.Name.Substring(0, 50) + "..." 
                        : product.Name ?? "Unknown Product";
                    
                    await onProgress((int)currentProgress, $"? {displayName}", "success");
                    
                    // Process images
                    if (processImages && imageService != null && !cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            var (mainImage, additionalImages) = await imageService.ProcessProductImagesAsync(
                                product,
                                async (msg) => await onProgress((int)currentProgress, msg, "info")
                            );
                            
                            if (!string.IsNullOrEmpty(mainImage))
                                product.CdnImageUrl = mainImage;
                            product.CdnAdditionalImages = additionalImages;
                            
                            var imageCount = (string.IsNullOrEmpty(mainImage) ? 0 : 1) + additionalImages.Count;
                            await onProgress((int)currentProgress, $"? Uploaded {imageCount} images", "success");
                        }
                        catch (Exception imgEx)
                        {
                            await onProgress((int)currentProgress, $"? Image error: {imgEx.Message}", "error");
                        }
                    }
                    
                    products.Add(product);
                }
                
                currentProgress += progressPerProduct;
                await Task.Delay(200);
            }
            
            // Cleanup
            httpClient?.Dispose();
            
            // Always create Excel if we have products (even if stopped early)
            if (products.Count > 0)
            {
                var finalProgress = 90;
                var stoppedText = cancellationToken.IsCancellationRequested ? " (stopped early)" : "";
                await onProgress(finalProgress, $"Scraped {products.Count} products{stoppedText}. Creating Excel...", "info");
                
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"HepsiburadaProducts_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                
                try
                {
                    var exporter = new ExcelExporter();
                    exporter.ExportToExcel(products, filePath, excludePrice, processImages);
                    
                    await onProgress(100, $"? Exported {products.Count} products!", "success");
                    await SendComplete(onProgress, fileName, products.Count);
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
                    var fileName = $"HepsiburadaProducts_Partial_{timestamp}.xlsx";
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    var exporter = new ExcelExporter();
                    exporter.ExportToExcel(products, filePath, excludePrice, processImages);
                    
                    await onProgress(100, $"?? Error occurred but saved {products.Count} products", "warning");
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
            // Cleanup session
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
