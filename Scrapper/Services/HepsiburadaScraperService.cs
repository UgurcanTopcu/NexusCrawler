using Scrapper.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Scrapper.Services;

public class HepsiburadaScraperService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    
    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
            cts.Cancel();
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
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;
        
        var products = new List<ProductInfo>();
        int skippedNoBarcodeCount = 0;
        
        try
        {
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
                
                await onProgress(8, "Loading CDN cache...", "info");
                await imageService.InitializeCacheAsync();
                
                var (siteCount, productCount) = cdnCache.GetCacheStats();
                await onProgress(9, $"CDN cache ready: {productCount} products", "info");
            }
            
            // Process each product
            for (int i = 0; i < linksToProcess.Count; i++)
            {
                // Check for cancellation
                if (cts.Token.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, $"Stopped at product {i}/{linksToProcess.Count}", "warning");
                    break;
                }
                
                var link = linksToProcess[i];
                await onProgress((int)currentProgress, $"Scraping product {i + 1} of {linksToProcess.Count}...", "info");
                
                var product = await scraper.GetProductDetailsAsync(link);
                if (product != null)
                {
                    // Check if product has barcode - skip if not
                    if (string.IsNullOrWhiteSpace(product.Barcode))
                    {
                        skippedNoBarcodeCount++;
                        var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 40 
                            ? product.Name.Substring(0, 40) + "..." 
                            : product.Name ?? "Unknown";
                        await onProgress((int)currentProgress, $"Skipped (no barcode): {displayName}", "warning");
                        currentProgress += progressPerProduct;
                        await Task.Delay(100);
                        continue;
                    }
                    
                    var displayNameSuccess = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 50 
                        ? product.Name.Substring(0, 50) + "..." 
                        : product.Name ?? "Unknown Product";
                    
                    await onProgress((int)currentProgress, $"Scraped: {displayNameSuccess}", "success");
                    
                    // Process images
                    if (processImages && imageService != null && !cts.Token.IsCancellationRequested)
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
                            await onProgress((int)currentProgress, $"Uploaded {imageCount} images", "success");
                        }
                        catch (Exception imgEx)
                        {
                            await onProgress((int)currentProgress, $"Image error: {imgEx.Message}", "error");
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
                var stoppedText = cts.Token.IsCancellationRequested ? " (stopped early)" : "";
                var skippedText = skippedNoBarcodeCount > 0 ? $" ({skippedNoBarcodeCount} skipped - no barcode)" : "";
                await onProgress(finalProgress, $"Scraped {products.Count} products{stoppedText}{skippedText}. Creating Excel...", "info");
                
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var urlName = ExtractNameFromUrl(categoryUrl);
                var fileName = $"Hepsiburada_{urlName}_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                
                try
                {
                    var exporter = new ExcelExporter();
                    exporter.ExportToExcel(products, filePath, excludePrice, processImages);
                    
                    var successMsg = $"Exported {products.Count} products!";
                    if (skippedNoBarcodeCount > 0)
                        successMsg += $" ({skippedNoBarcodeCount} skipped - no barcode)";
                    await onProgress(100, successMsg, "success");
                    
                    await SendComplete(onProgress, fileName, products.Count);
                }
                catch (Exception excelEx)
                {
                    await onProgress(100, $"Excel error: {excelEx.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                var noProductsMsg = skippedNoBarcodeCount > 0 
                    ? $"No products with barcode found ({skippedNoBarcodeCount} products skipped - no barcode)"
                    : "No products scraped";
                await onProgress(100, noProductsMsg, "error");
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
                    var urlName = ExtractNameFromUrl(categoryUrl);
                    var fileName = $"Hepsiburada_{urlName}_Partial_{timestamp}.xlsx";
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    var exporter = new ExcelExporter();
                    exporter.ExportToExcel(products, filePath, excludePrice, processImages);
                    
                    await onProgress(100, $"Error occurred but saved {products.Count} products", "warning");
                    await SendComplete(onProgress, fileName, products.Count);
                }
                catch
                {
                    await onProgress(100, $"Error: {ex.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, $"Error: {ex.Message}", "error");
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
    
    /// <summary>
    /// Extracts a meaningful name from Hepsiburada URLs for file naming.
    /// Shop URL: https://www.hepsiburada.com/magaza/avfoni?tab=allproducts -> "avfoni"
    /// Category URL: https://www.hepsiburada.com/elektrikli-ev-aletleri-ankastre-setler-c-234329 -> "ankastre-setler"
    /// Search URL: https://www.hepsiburada.com/ara?q=laptop -> "laptop"
    /// </summary>
    private string ExtractNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url.StartsWith("http") ? url : "https://" + url);
            var path = uri.AbsolutePath.Trim('/');
            var query = uri.Query;
            
            // Shop URL: /magaza/shopname
            if (path.StartsWith("magaza/", StringComparison.OrdinalIgnoreCase))
            {
                var shopName = path.Substring(7).Split('?')[0].Split('/')[0];
                return SanitizeFileName(shopName);
            }
            
            // Search URL: /ara?q=searchterm
            if (path.StartsWith("ara", StringComparison.OrdinalIgnoreCase) && query.Contains("q="))
            {
                var queryParams = System.Web.HttpUtility.ParseQueryString(query);
                var searchTerm = queryParams.Get("q");
                if (!string.IsNullOrEmpty(searchTerm))
                {
                    return SanitizeFileName(searchTerm);
                }
            }
            
            // Category URL: /some-category-name-c-123456 or /some-category-name
            // Extract the meaningful part before "-c-" or just the last segment
            var segments = path.Split('/');
            var lastSegment = segments[segments.Length - 1];
            
            // Remove category ID suffix if present (e.g., "-c-234329")
            var categoryMatch = Regex.Match(lastSegment, @"^(.+?)-c-\d+$");
            if (categoryMatch.Success)
            {
                return SanitizeFileName(categoryMatch.Groups[1].Value);
            }
            
            // Return the last segment as-is (cleaned)
            return SanitizeFileName(lastSegment);
        }
        catch
        {
            return "Products";
        }
    }
    
    /// <summary>
    /// Sanitizes a string to be safe for use as a filename.
    /// Removes invalid characters, limits length, and ensures readable format.
    /// </summary>
    private string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Products";
        
        // Replace URL encoding and special chars with readable text
        name = System.Web.HttpUtility.UrlDecode(name);
        
        // Replace Turkish characters with ASCII equivalents for compatibility
        name = name.Replace('ý', 'i')
                   .Replace('ð', 'g')
                   .Replace('ü', 'u')
                   .Replace('þ', 's')
                   .Replace('ö', 'o')
                   .Replace('ç', 'c')
                   .Replace('Ý', 'I')
                   .Replace('Ð', 'G')
                   .Replace('Ü', 'U')
                   .Replace('Þ', 'S')
                   .Replace('Ö', 'O')
                   .Replace('Ç', 'C');
        
        // Keep only alphanumeric, dash, and underscore
        name = Regex.Replace(name, @"[^a-zA-Z0-9-_]", "-");
        
        // Remove consecutive dashes
        name = Regex.Replace(name, @"-+", "-");
        
        // Trim dashes from start and end
        name = name.Trim('-');
        
        // Limit length to 50 characters
        if (name.Length > 50)
            name = name.Substring(0, 50).TrimEnd('-');
        
        // Ensure not empty after sanitization
        return string.IsNullOrWhiteSpace(name) ? "Products" : name;
    }
}
