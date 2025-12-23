using Scrapper.Models;
using System.Net.Http;

namespace Scrapper.Services;

public class TrendyolScraperService
{
    public async Task ScrapeWithProgressAsync(
        string categoryUrl,
        int maxProducts,
        bool excludePrice,
        ScrapeMethod scrapeMethod,
        bool processImages,
        string? templateName, // NEW: Template name
        Func<int, string, string, Task> onProgress)
    {
        try
        {
            var methodName = scrapeMethod == ScrapeMethod.ScrapeDo ? "Scrape.do API" : "Selenium";
            await onProgress(0, $"Initializing scraper ({methodName})...", "info");
            
            using var scraper = new TrendyolScraper();
            scraper.Method = scrapeMethod;
            
            await onProgress(5, "Fetching product links...", "info");
            var productLinks = await scraper.GetProductLinksAsync(categoryUrl);
            
            if (productLinks.Count == 0)
            {
                await onProgress(100, "No products found at the given URL", "error");
                await SendComplete(onProgress, null, null);
                return;
            }
            
            var linksToProcess = productLinks.Take(maxProducts).ToList();
            await onProgress(10, $"Found {productLinks.Count} products, will scrape {linksToProcess.Count}", "info");
            
            var products = new List<ProductInfo>();
            var progressPerProduct = 80.0 / linksToProcess.Count;
            var currentProgress = 10.0;
            
            // Initialize image services ONCE if needed
            FtpUploadService? ftpService = null;
            HttpClient? httpClient = null;
            ImageProcessingService? imageService = null;
            
            if (processImages)
            {
                var ftpConfig = new CdnFtpConfig();
                ftpService = new FtpUploadService(ftpConfig);
                httpClient = new HttpClient();
                imageService = new ImageProcessingService(httpClient, ftpService);
            }
            
            // Process each product: Scrape -> Upload Images -> Add to list
            for (int i = 0; i < linksToProcess.Count; i++)
            {
                var link = linksToProcess[i];
                await onProgress((int)currentProgress, $"Scraping product {i + 1} of {linksToProcess.Count}...", "info");
                
                var product = await scraper.GetProductDetailsAsync(link);
                if (product != null)
                {
                    var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 50 
                        ? product.Name.Substring(0, 50) + "..." 
                        : product.Name ?? "Unknown Product";
                    
                    var attrInfo = product.Attributes.Count > 0 ? $" ({product.Attributes.Count} attributes)" : "";
                    await onProgress(
                        (int)currentProgress,
                        $"✓ {displayName}{attrInfo}",
                        "success"
                    );
                    
                    // ✨ NEW: Process images IMMEDIATELY after scraping
                    if (processImages && imageService != null)
                    {
                        try
                        {
                            await onProgress((int)currentProgress, $"🖼️ Processing images for product {i + 1}...", "info");
                            
                            Console.WriteLine($"\n[Service] ==================== CALLING IMAGE PROCESSOR ====================");
                            Console.WriteLine($"[Service] Product: {product.Name}");
                            Console.WriteLine($"[Service] Source: {product.Source}");
                            Console.WriteLine($"[Service] ProductId: {product.ProductId}");
                            Console.WriteLine($"[Service] Has Images: {product.GetAllImages().Count}");
                            
                            var (mainImage, additionalImages) = await imageService.ProcessProductImagesAsync(
                                product,
                                async (msg) => await onProgress((int)currentProgress, msg, "info")
                            );
                            
                            Console.WriteLine($"[Service] Image processing completed");
                            Console.WriteLine($"[Service] Main image: {(string.IsNullOrEmpty(mainImage) ? "NULL" : mainImage)}");
                            Console.WriteLine($"[Service] Additional images: {additionalImages.Count}");
                            Console.WriteLine($"[Service] ================================================================\n");
                            
                            // Store CDN URLs
                            if (!string.IsNullOrEmpty(mainImage))
                            {
                                product.CdnImageUrl = mainImage;
                                Console.WriteLine($"[Service] ✅ SET product.CdnImageUrl = {mainImage}");
                            }
                            else
                            {
                                Console.WriteLine($"[Service] ❌ WARNING: mainImage was NULL or empty! NOT setting CdnImageUrl");
                            }
                            product.CdnAdditionalImages = additionalImages;
                            
                            Console.WriteLine($"[Service] VERIFICATION:");
                            Console.WriteLine($"[Service]   product.CdnImageUrl = {(string.IsNullOrEmpty(product.CdnImageUrl) ? "NULL/EMPTY" : product.CdnImageUrl)}");
                            Console.WriteLine($"[Service]   product.CdnAdditionalImages.Count = {product.CdnAdditionalImages.Count}");
                            if (product.CdnAdditionalImages.Count > 0)
                            {
                                for (int j = 0; j < product.CdnAdditionalImages.Count; j++)
                                {
                                    Console.WriteLine($"[Service]   CdnAdditionalImages[{j}] = {product.CdnAdditionalImages[j]}");
                                }
                            }
                            
                            var imageCount = (string.IsNullOrEmpty(mainImage) ? 0 : 1) + additionalImages.Count;
                            await onProgress((int)currentProgress, $"✓ Uploaded {imageCount} images for product {i + 1}", "success");
                        }
                        catch (Exception imgEx)
                        {
                            Console.WriteLine($"\n[Service] ❌❌❌ IMAGE PROCESSING EXCEPTION ❌❌❌");
                            Console.WriteLine($"[Service] Product: {product.Name}");
                            Console.WriteLine($"[Service] Exception: {imgEx.GetType().Name}");
                            Console.WriteLine($"[Service] Message: {imgEx.Message}");
                            Console.WriteLine($"[Service] Stack: {imgEx.StackTrace}");
                            Console.WriteLine($"[Service] ================================================\n");
                            
                            await onProgress((int)currentProgress, $"❌ Image upload failed for product {i + 1}: {imgEx.Message}", "error");
                        }
                    }
                    
                    products.Add(product);
                }
                
                currentProgress += progressPerProduct;
                // Reduced delay for faster scraping
                await Task.Delay(200);
            }
            
            // Cleanup
            if (httpClient != null)
            {
                httpClient.Dispose();
            }
            
            var finalProgress = 90;
            await onProgress(finalProgress, $"Scraped {products.Count} products. Creating Excel file...", "info");
            
            // Create Excel file
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"TrendyolProducts_{timestamp}.xlsx";
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            
            try
            {
                // Check if template is specified
                if (!string.IsNullOrEmpty(templateName))
                {
                    var templateService = new TemplateService();
                    var template = templateService.GetTemplate(templateName);
                    
                    if (template != null)
                    {
                        await onProgress(finalProgress, $"✅ Using template: {template.Name}", "info");
                        var templateExporter = new TemplateExcelExporter();
                        templateExporter.ExportWithTemplate(products, filePath, template, processImages);
                    }
                    else
                    {
                        await onProgress(finalProgress, $"⚠️ Template '{templateName}' not found, using default export", "info");
                        var exporter = new ExcelExporter();
                        exporter.ExportToExcel(products, filePath, excludePrice, processImages);
                    }
                }
                else
                {
                    // Use default exporter
                    var exporter = new ExcelExporter();
                    exporter.ExportToExcel(products, filePath, excludePrice, processImages);
                }
                
                await onProgress(100, $"✓ Successfully scraped {products.Count} products!", "success");
                await SendComplete(onProgress, fileName, products.Count);
            }
            catch (Exception excelEx)
            {
                await onProgress(100, $"✗ Error creating Excel file: {excelEx.Message}", "error");
                Console.WriteLine($"Excel export error details: {excelEx}");
                await SendComplete(onProgress, null, null);
            }
        }
        catch (Exception ex)
        {
            await onProgress(100, $"✗ Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, null);
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
        // Send as a message so it gets picked up by the progress handler
        await onProgress(100, json, "complete");
    }
}
