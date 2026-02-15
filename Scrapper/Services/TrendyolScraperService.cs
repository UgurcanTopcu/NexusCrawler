using Scrapper.Models;
using System.Collections.Concurrent;

namespace Scrapper.Services;

public class TrendyolScraperService
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
        string? templateName,
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

            using var scraper = new TrendyolScraper();
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

            // Initialize image service if needed (wsrv.nl - no upload, just URL transformation)
            WsrvImageService? imageService = null;

            if (processImages)
            {
                imageService = new WsrvImageService();
                await onProgress(9, "Image CDN ready (wsrv.nl)", "info");
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

                    // Process images with wsrv.nl
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
                            await onProgress((int)currentProgress, $"Converted {imageCount} images to wsrv.nl URLs", "success");
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

            // No cleanup needed for wsrv.nl (it's stateless)

            // Always create Excel if we have products (even if stopped early)
            if (products.Count > 0)
            {
                var finalProgress = 90;
                var stoppedText = cts.Token.IsCancellationRequested ? " (stopped early)" : "";
                var skippedText = skippedNoBarcodeCount > 0 ? $" ({skippedNoBarcodeCount} skipped - no barcode)" : "";
                await onProgress(finalProgress, $"Scraped {products.Count} products{stoppedText}{skippedText}. Creating Excel...", "info");

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var urlSlug = ExtractUrlSlug(categoryUrl);
                var fileName = $"Trendyol_{urlSlug}_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                try
                {
                    if (!string.IsNullOrEmpty(templateName))
                    {
                        var templateService = new TemplateService();
                        var template = templateService.GetTemplate(templateName);

                        if (template != null)
                        {
                            var templateExporter = new TemplateExcelExporter();
                            templateExporter.ExportWithTemplate(products, filePath, template, processImages);
                        }
                        else
                        {
                            var exporter = new ExcelExporter();
                            exporter.ExportToExcel(products, filePath, excludePrice, processImages);
                        }
                    }
                    else
                    {
                        var exporter = new ExcelExporter();
                        exporter.ExportToExcel(products, filePath, excludePrice, processImages);
                    }

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
                    var urlSlug = ExtractUrlSlug(categoryUrl);
                    var fileName = $"Trendyol_{urlSlug}_Partial_{timestamp}.xlsx";
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

    private static string ExtractUrlSlug(string url)
    {
        try
        {
            var uri = new Uri(url.StartsWith("http") ? url : "https://" + url);
            var path = uri.AbsolutePath.Trim('/');
            
            if (string.IsNullOrEmpty(path))
                return "products";
            
            var slug = path.Replace("/", "_");
            
            if (slug.Length > 60)
                slug = slug[..60];
            
            foreach (var c in Path.GetInvalidFileNameChars())
                slug = slug.Replace(c, '_');
            
            return slug;
        }
        catch
        {
            return "products";
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