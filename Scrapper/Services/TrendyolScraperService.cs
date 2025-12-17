using Scrapper.Models;

namespace Scrapper.Services;

public class TrendyolScraperService
{
    public async Task ScrapeWithProgressAsync(
        string categoryUrl,
        int maxProducts,
        bool excludePrice,
        Func<int, string, string, Task> onProgress)
    {
        try
        {
            await onProgress(0, "Initializing scraper...", "info");
            
            using var scraper = new TrendyolScraper();
            // Disable HTML debugging for speed (only enable if needed for troubleshooting)
            scraper.SaveHtmlForDebug = false;
            
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
                    
                    // Show attribute count in progress
                    var attrInfo = product.Attributes.Count > 0 ? $" ({product.Attributes.Count} attributes)" : "";
                    await onProgress(
                        (int)currentProgress,
                        $"✓ {displayName}{attrInfo}",
                        "success"
                    );
                }
                
                currentProgress += progressPerProduct;
                // Reduced delay for faster scraping
                await Task.Delay(300);
            }
            
            await onProgress(90, $"Scraped {products.Count} products. Creating Excel file...", "info");
            
            // Create Excel file
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"TrendyolProducts_{timestamp}.xlsx";
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            
            var exporter = new ExcelExporter();
            exporter.ExportToExcel(products, filePath, excludePrice);
            
            await onProgress(100, $"✓ Successfully scraped {products.Count} products!", "success");
            await SendComplete(onProgress, fileName, products.Count);
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
