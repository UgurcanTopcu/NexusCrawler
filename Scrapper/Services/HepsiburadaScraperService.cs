using Scrapper.Models;

namespace Scrapper.Services;

public class HepsiburadaScraperService
{
    public async Task ScrapeWithProgressAsync(
        string categoryUrl,
        int maxProducts,
        bool excludePrice,
        ScrapeMethod scrapeMethod,
        bool processImages,
        Func<int, string, string, Task> onProgress)
    {
        try
        {
            var methodName = scrapeMethod == ScrapeMethod.ScrapeDo ? "Scrape.do API" : "Selenium";
            await onProgress(0, $"Initializing scraper ({methodName})...", "info");
            
            using var scraper = new HepsiburadaScraper();
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
            var progressPerProduct = (processImages ? 70.0 : 80.0) / linksToProcess.Count;
            var currentProgress = 10.0;
            
            for (int i = 0; i < linksToProcess.Count; i++)
            {
                var link = linksToProcess[i];
                await onProgress((int)currentProgress, $"Scraping product {i + 1} of {linksToProcess.Count}...", "info");
                
                var product = await scraper.GetProductDetailsAsync(link);
                if (product != null)
                {
                    products.Add(product);
                    var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 50 
                        ? product.Name.Substring(0, 50) + "..." 
                        : product.Name ?? "Unknown Product";
                    
                    var attrInfo = product.Attributes.Count > 0 ? $" ({product.Attributes.Count} attributes)" : "";
                    await onProgress(
                        (int)currentProgress,
                        $"? {displayName}{attrInfo}",
                        "success"
                    );
                }
                
                currentProgress += progressPerProduct;
                await Task.Delay(200);
            }
            
            // Image Processing Step
            if (processImages && products.Count > 0)
            {
                await onProgress((int)currentProgress, "??? Processing and uploading images to CDN...", "info");
                
                var ftpConfig = new CdnFtpConfig();
                var ftpService = new FtpUploadService(ftpConfig);
                var httpClient = new HttpClient();
                var imageService = new ImageProcessingService(httpClient, ftpService);
                
                var progressPerImage = 10.0 / products.Count;
                
                for (int i = 0; i < products.Count; i++)
                {
                    var product = products[i];
                    await onProgress((int)currentProgress, $"Processing images for product {i + 1}/{products.Count}...", "info");
                    
                    var (mainImage, additionalImages) = await imageService.ProcessProductImagesAsync(
                        product,
                        async (msg) => await onProgress((int)currentProgress, msg, "info")
                    );
                    
                    // Store CDN URLs
                    if (!string.IsNullOrEmpty(mainImage))
                    {
                        product.CdnImageUrl = mainImage;
                    }
                    
                    product.CdnAdditionalImages = additionalImages;
                    
                    currentProgress += progressPerImage;
                }
                
                await onProgress(90, $"? All images processed and uploaded to CDN!", "success");
            }
            
            var finalProgress = processImages ? 90 : 90;
            await onProgress(finalProgress, $"Scraped {products.Count} products. Creating Excel file...", "info");
            
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"HepsiburadaProducts_{timestamp}.xlsx";
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            
            try
            {
                var exporter = new ExcelExporter();
                exporter.ExportToExcel(products, filePath, excludePrice, processImages);
                
                await onProgress(100, $"? Successfully scraped {products.Count} products!", "success");
                await SendComplete(onProgress, fileName, products.Count);
            }
            catch (Exception excelEx)
            {
                await onProgress(100, $"? Error creating Excel file: {excelEx.Message}", "error");
                Console.WriteLine($"Excel export error details: {excelEx}");
                await SendComplete(onProgress, null, null);
            }
        }
        catch (Exception ex)
        {
            await onProgress(100, $"? Error: {ex.Message}", "error");
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
        await onProgress(100, json, "complete");
    }
}
