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

            var linkScraper = new HepsiburadaScraper();
            linkScraper.Method = scrapeMethod;

            await onProgress(5, "Fetching product links...", "info");
            var productLinks = await linkScraper.GetProductLinksAsync(categoryUrl, maxProducts, onProgress);
            linkScraper.Dispose();

            if (productLinks.Count == 0)
            {
                await onProgress(100, "No products found at the given URL", "error");
                await SendComplete(onProgress, null, null);
                return;
            }

            var linksToProcess = productLinks.Take(maxProducts).ToList();
            await onProgress(10, $"Found {productLinks.Count} products, will scrape {linksToProcess.Count}", "info");

            // Initialize image services ONCE if needed
            FtpUploadService? ftpService = null;
            HttpClient? httpClient = null;
            CdnCacheService? cdnCache = null;
            var progressPerProduct = 80.0 / linksToProcess.Count;
            var currentProgress = 10.0;

            // Initialize image service if needed (wsrv.nl - no upload, just URL transformation)
            WsrvImageService? wsrvImageService = null;

            if (processImages)
            {
                wsrvImageService = new WsrvImageService();
                await onProgress(9, "Image CDN ready (wsrv.nl)", "info");
            }

            // Parallel product detail scraping with rate limiting
            const int maxConcurrency = 4;
            const int staggerDelayMs = 1500;

            var scraperPool = new ConcurrentBag<HepsiburadaScraper>();
            var poolScrapers = new List<HepsiburadaScraper>();
            var poolLock = new object();
            for (int j = 0; j < maxConcurrency; j++)
            {
                var s = new HepsiburadaScraper { Method = scrapeMethod };
                scraperPool.Add(s);
                poolScrapers.Add(s);
            }

            using var semaphore = new SemaphoreSlim(maxConcurrency);
            using var throttle = new SemaphoreSlim(1, 1);
            var productResults = new ConcurrentDictionary<int, ProductInfo>();
            int completedCount = 0;
            var progressLock = new object();

            try
            {
                var tasks = linksToProcess.Select(async (link, index) =>
                {
                    try
                    {
                        await semaphore.WaitAsync(cts.Token);
                    }
                    catch (OperationCanceledException) { return; }

                    try
                    {
                        if (cts.Token.IsCancellationRequested) return;

                        // Stagger requests to avoid rate limiting
                        await throttle.WaitAsync(cts.Token);
                        try { await Task.Delay(staggerDelayMs, cts.Token); }
                        finally { throttle.Release(); }

                        if (cts.Token.IsCancellationRequested) return;

                        // Get scraper from pool
                        scraperPool.TryTake(out var poolScraper);
                        ProductInfo? product = null;
                        bool scraperFailed = false;
                        try
                        {
                            product = await poolScraper!.GetProductDetailsAsync(link);
                            if (product == null) scraperFailed = true;
                        }
                        catch
                        {
                            scraperFailed = true;
                        }

                        if (scraperFailed)
                        {
                            // Scraper may be in a broken state (crashed ChromeDriver, blocked session).
                            // Dispose it and create a fresh replacement to prevent cascading failures.
                            try { poolScraper!.Dispose(); } catch { }
                            var replacement = new HepsiburadaScraper { Method = scrapeMethod };
                            scraperPool.Add(replacement);
                            lock (poolLock) { poolScrapers.Add(replacement); }
                        }
                        else
                        {
                            scraperPool.Add(poolScraper!);
                        }

                        int current;
                        lock (progressLock) { current = ++completedCount; }
                        var progressPercent = 10 + (int)((current / (double)linksToProcess.Count) * 80);

                        if (product == null)
                        {
                            await onProgress(progressPercent, $"Failed to scrape product {current}/{linksToProcess.Count}", "warning");
                            return;
                        }

                        // Check if product has barcode - skip if not
                        if (string.IsNullOrWhiteSpace(product.Barcode))
                        {
                            lock (progressLock) { skippedNoBarcodeCount++; }
                            var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 40
                                ? product.Name.Substring(0, 40) + "..."
                                : product.Name ?? "Unknown";
                            await onProgress(progressPercent, $"Skipped (no barcode): {displayName}", "warning");
                            return;
                        }

                        ApplySourceCollectionMetadata(product, categoryUrl);

                        var displayNameSuccess = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 50
                            ? product.Name.Substring(0, 50) + "..."
                            : product.Name ?? "Unknown Product";
                        await onProgress(progressPercent, $"Scraped: {displayNameSuccess}", "success");

                        // Process images
                        if (processImages && wsrvImageService != null && !cts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                var (mainImage, additionalImages) = await wsrvImageService.ProcessProductImagesAsync(
                                    product,
                                    async (msg) => await onProgress(progressPercent, msg, "info")
                                );

                                if (!string.IsNullOrEmpty(mainImage))
                                    product.CdnImageUrl = mainImage;
                                product.CdnAdditionalImages = additionalImages;

                                var imageCount = (string.IsNullOrEmpty(mainImage) ? 0 : 1) + additionalImages.Count;
                                await onProgress(progressPercent, $"Uploaded {imageCount} images", "success");
                            }
                            catch (Exception imgEx)
                            {
                                await onProgress(progressPercent, $"Image error: {imgEx.Message}", "error");
                            }
                        }

                        productResults[index] = product;
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks);
            }
            finally
            {
                foreach (var s in poolScrapers)
                    s.Dispose();
            }

            // Collect results preserving original order
            products = productResults.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList();

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
                var fileName = $"{urlName}_{timestamp}.xlsx";
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
                    var fileName = $"{urlName}_Partial_{timestamp}.xlsx";
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

    /// <summary>
    /// Scrapes multiple category URLs and combines all products into a single Excel file.
    /// </summary>
    public async Task ScrapeMultipleWithProgressAsync(
        string[] categoryUrls,
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

        // Scrape.do plan limit: max 10 concurrent requests total.
        // 10 parallel URLs × 1 product at a time = exactly 10 concurrent.
        int maxUrlParallel     = scrapeMethod == ScrapeMethod.ScrapeDo ? 10 : 1;
        int productConcurrency = 1; // always 1 — URL-level parallelism already saturates the 10-request limit
        var resultsBag         = new ConcurrentBag<(int Index, List<ProductInfo> Products)>();
        var completedUrls      = 0;
        using var progressLock = new SemaphoreSlim(1, 1);

        try
        {
            await onProgress(2,
                $"?? Batch scrape — {categoryUrls.Length} URL(s), {maxUrlParallel} parallel",
                "info");

            await Parallel.ForEachAsync(
                categoryUrls.Select((url, i) => (url, i)),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxUrlParallel,
                    CancellationToken      = cts.Token
                },
                async (item, _) =>
                {
                    var (url, urlIndex) = item;

                    Func<int, string, string, Task> inner = async (_, msg, type) =>
                    {
                        if (type == "complete") return;
                        var pct = 5 + Volatile.Read(ref completedUrls) * 85 / categoryUrls.Length;
                        await progressLock.WaitAsync(CancellationToken.None);
                        try   { await onProgress(pct, msg, type); }
                        finally { progressLock.Release(); }
                    };

                    var urlProducts = await ScrapeUrlToProductsAsync(
                        url, maxProducts, scrapeMethod, processImages, inner, cts, productConcurrency);

                    resultsBag.Add((urlIndex, urlProducts));

                    var current  = Interlocked.Increment(ref completedUrls);
                    var progress = 5 + current * 85 / categoryUrls.Length;
                    var shortUrl = url.Length > 70 ? url[..70] + "…" : url;

                    await progressLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        await onProgress(progress,
                            $"? [{current}/{categoryUrls.Length}] {shortUrl}: {urlProducts.Count} products",
                            "success");
                    }
                    finally { progressLock.Release(); }
                });

            var allProducts = resultsBag
                .OrderBy(x => x.Index)
                .SelectMany(x => x.Products)
                .ToList();

            if (allProducts.Count > 0)
            {
                await onProgress(92, $"?? Creating combined Excel for {allProducts.Count} products...", "info");

                var ts       = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"Hepsiburada_Combined_{categoryUrls.Length}URLs_{ts}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                try
                {
                    new ExcelExporter().ExportToExcel(allProducts, filePath, excludePrice, processImages);
                    await onProgress(100,
                        $"? Done! {allProducts.Count} products combined from {categoryUrls.Length} URL(s)", "success");
                    await SendComplete(onProgress, fileName, allProducts.Count);
                }
                catch (Exception excelEx)
                {
                    await onProgress(100, $"Excel error: {excelEx.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, "No products found across all URLs", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        catch (OperationCanceledException)
        {
            var partial = resultsBag.OrderBy(x => x.Index).SelectMany(x => x.Products).ToList();
            if (partial.Count > 0)
            {
                var ts          = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var partialName = $"Hepsiburada_Combined_Partial_{ts}.xlsx";
                new ExcelExporter().ExportToExcel(partial,
                    Path.Combine(Directory.GetCurrentDirectory(), partialName), excludePrice, processImages);
                await onProgress(100, $"? Stopped — saved {partial.Count} products collected so far", "warning");
                await SendComplete(onProgress, partialName, partial.Count);
            }
            else
            {
                await onProgress(100, "? Stopped by user — no products collected", "warning");
                await SendComplete(onProgress, null, null);
            }
        }
        catch (Exception ex)
        {
            var partial = resultsBag.OrderBy(x => x.Index).SelectMany(x => x.Products).ToList();
            if (partial.Count > 0)
            {
                var ts          = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var partialName = $"Hepsiburada_Combined_Partial_{ts}.xlsx";
                new ExcelExporter().ExportToExcel(partial,
                    Path.Combine(Directory.GetCurrentDirectory(), partialName), excludePrice, processImages);
                await onProgress(100, $"?? Error — saved {partial.Count} products collected so far", "warning");
                await SendComplete(onProgress, partialName, partial.Count);
            }
            else
            {
                await onProgress(100, $"? Error: {ex.Message}", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(sessionId))
                _sessions.TryRemove(sessionId, out _);
            cts.Dispose();
        }
    }

    /// <summary>
    /// Scrapes a single category URL and returns the collected products without exporting.
    /// Uses the same parallel scraper-pool approach as the single-URL method.
    /// </summary>
    private static async Task<List<ProductInfo>> ScrapeUrlToProductsAsync(
        string categoryUrl,
        int maxProducts,
        ScrapeMethod scrapeMethod,
        bool processImages,
        Func<int, string, string, Task> onProgress,
        CancellationTokenSource cts,
        int maxProductConcurrency = 4)
    {
        var productResults = new ConcurrentDictionary<int, ProductInfo>();

        try
        {
            var linkScraper = new HepsiburadaScraper { Method = scrapeMethod };
            var productLinks = await linkScraper.GetProductLinksAsync(categoryUrl, maxProducts, onProgress);
            linkScraper.Dispose();

            if (productLinks.Count == 0) return [];

            var toProcess = productLinks.Take(maxProducts).ToList();
            await onProgress(10, $"Found {toProcess.Count} products", "info");

            const int staggerDelayMs = 1500;
            var maxConcurrency = maxProductConcurrency;

            var scraperPool  = new ConcurrentBag<HepsiburadaScraper>();
            var poolScrapers = new List<HepsiburadaScraper>();
            var poolLock     = new object();
            for (int j = 0; j < maxConcurrency; j++)
            {
                var s = new HepsiburadaScraper { Method = scrapeMethod };
                scraperPool.Add(s);
                poolScrapers.Add(s);
            }

            WsrvImageService? imageService = processImages ? new WsrvImageService() : null;

            using var semaphore = new SemaphoreSlim(maxConcurrency);
            using var throttle  = new SemaphoreSlim(1, 1);
            int completedCount = 0;

            try
            {
                var tasks = toProcess.Select(async (link, index) =>
                {
                    try { await semaphore.WaitAsync(cts.Token); }
                    catch (OperationCanceledException) { return; }

                    try
                    {
                        if (cts.Token.IsCancellationRequested) return;

                        await throttle.WaitAsync(cts.Token);
                        try { await Task.Delay(staggerDelayMs, cts.Token); }
                        finally { throttle.Release(); }

                        if (cts.Token.IsCancellationRequested) return;

                        scraperPool.TryTake(out var poolScraper);
                        ProductInfo? product = null;
                        bool failed = false;
                        try { product = await poolScraper!.GetProductDetailsAsync(link); if (product == null) failed = true; }
                        catch { failed = true; }

                        if (failed)
                        {
                            try { poolScraper!.Dispose(); } catch { }
                            var replacement = new HepsiburadaScraper { Method = scrapeMethod };
                            scraperPool.Add(replacement);
                            lock (poolLock) { poolScrapers.Add(replacement); }
                        }
                        else
                        {
                            scraperPool.Add(poolScraper!);
                        }

                        int current;
                        lock (poolLock) { current = ++completedCount; }
                        var pct = 10 + (int)(current / (double)toProcess.Count * 80);

                        if (product == null || string.IsNullOrWhiteSpace(product.Barcode)) return;

                        ApplySourceCollectionMetadata(product, categoryUrl);

                        if (processImages && imageService != null)
                        {
                            try
                            {
                                var (main, extra) = await imageService.ProcessProductImagesAsync(
                                    product, async (msg) => await onProgress(pct, msg, "info"));
                                if (!string.IsNullOrEmpty(main)) product.CdnImageUrl = main;
                                product.CdnAdditionalImages = extra;
                            }
                            catch { }
                        }

                        var name = product.Name?.Length > 50 ? product.Name[..50] + "…" : product.Name ?? "Unknown";
                        await onProgress(pct, $"Scraped: {name}", "success");
                        productResults[index] = product;
                    }
                    catch (OperationCanceledException) { }
                    finally { semaphore.Release(); }
                }).ToList();

                await Task.WhenAll(tasks);
            }
            finally
            {
                foreach (var s in poolScrapers) s.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await onProgress(0, $"Error scraping {categoryUrl}: {ex.Message}", "error");
        }

        return productResults.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    /// <summary>
    /// Extracts a meaningful name from Hepsiburada URLs for file naming.
    /// Shop URL: https://www.hepsiburada.com/magaza/avfoni?tab=allproducts -> "avfoni"
    /// Category URL: https://www.hepsiburada.com/elektrikli-ev-aletleri-ankastre-setler-c-234329 -> "ankastre-setler"
    /// Search URL: https://www.hepsiburada.com/ara?q=laptop -> "laptop"
    /// </summary>
    private static string ExtractNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url.StartsWith("http") ? url : "https://" + url);
            var path = uri.AbsolutePath.Trim('/');
            var query = uri.Query;
            
            // Shop URL: /magaza/shopname (without query parameters like ?tab=allproducts)
            if (path.StartsWith("magaza/", StringComparison.OrdinalIgnoreCase))
            {
                // Take everything after "magaza/" and before any / or query params
                var shopName = path.Substring(7).Split('/')[0];
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

    private static void ApplySourceCollectionMetadata(ProductInfo product, string categoryUrl)
    {
        ArgumentNullException.ThrowIfNull(product);

        if (string.IsNullOrWhiteSpace(categoryUrl))
            return;

        product.SourceCollectionUrl = categoryUrl;
        product.SourceCollectionKey = ExtractSourceCollectionKey(categoryUrl);

        if (string.IsNullOrWhiteSpace(product.Seller) && !string.IsNullOrWhiteSpace(product.SourceCollectionKey))
            product.Seller = product.SourceCollectionKey;
    }

    private static string ExtractSourceCollectionKey(string url)
    {
        try
        {
            var uri = new Uri(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url);
            var path = uri.AbsolutePath.Trim('/');

            if (path.StartsWith("magaza/", StringComparison.OrdinalIgnoreCase))
                return path.Substring("magaza/".Length).Split('/')[0];

            return ExtractNameFromUrl(url);
        }
        catch
        {
            return string.Empty;
        }
    }
    
    /// <summary>
    /// Sanitizes a string to be safe for use as a filename.
    /// Removes invalid characters, limits length, and ensures readable format.
    /// </summary>
    private static string SanitizeFileName(string name)
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
