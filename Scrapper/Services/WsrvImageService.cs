using Scrapper.Models;
using System.Web;

namespace Scrapper.Services;

/// <summary>
/// Service for generating wsrv.nl CDN URLs for product images
/// wsrv.nl is a free image CDN that proxies and optimizes images on-the-fly
/// No upload needed - just transform the original image URL
/// </summary>
public class WsrvImageService
{
    private const string WsrvBaseUrl = "https://wsrv.nl/";
    private const int TargetSize = 1000;

    /// <summary>
    /// Process product images by converting to wsrv.nl CDN URLs
    /// No actual upload or download needed - wsrv.nl proxies the original URLs
    /// </summary>
    public Task<(string? mainImage, List<string> additionalImages)> ProcessProductImagesAsync(
        ProductInfo product,
        Func<string, Task>? onProgressMessage = null)
    {
        var mainImageUrl = string.Empty;
        var additionalImageUrls = new List<string>();

        try
        {
            // Get all image URLs
            var allImageUrls = product.GetAllImages();
            
            if (allImageUrls.Count == 0)
            {
                onProgressMessage?.Invoke("?? No images found for product");
                return Task.FromResult<(string?, List<string>)>((null, new List<string>()));
            }

            // Convert up to 3 images (1 main + 2 additional)
            const int MaxImagesToProcess = 3;
            var imagesToProcess = allImageUrls.Take(MaxImagesToProcess).ToList();
            onProgressMessage?.Invoke($"??? Processing {imagesToProcess.Count} images via wsrv.nl...");

            // Convert main image
            if (imagesToProcess.Count > 0)
            {
                mainImageUrl = ConvertToWsrvUrl(imagesToProcess[0]);
            }

            // Convert additional images (up to 2 more)
            for (int i = 1; i < imagesToProcess.Count; i++)
            {
                var wsrvUrl = ConvertToWsrvUrl(imagesToProcess[i]);
                if (!string.IsNullOrEmpty(wsrvUrl))
                {
                    additionalImageUrls.Add(wsrvUrl);
                }
            }

            onProgressMessage?.Invoke($"? Converted {imagesToProcess.Count} image URLs");
            return Task.FromResult<(string?, List<string>)>((mainImageUrl, additionalImageUrls));
        }
        catch (Exception ex)
        {
            onProgressMessage?.Invoke($"? Error converting images: {ex.Message}");
            return Task.FromResult<(string?, List<string>)>((null, new List<string>()));
        }
    }

    /// <summary>
    /// Convert an image URL to wsrv.nl CDN URL with resizing
    /// Format: https://wsrv.nl/?url={encoded_url}&w={width}&h={height}&fit=cover&we
    /// </summary>
    private string ConvertToWsrvUrl(string originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl))
            return string.Empty;

        try
        {
            // Clean the URL (remove any existing query parameters that might interfere)
            var cleanUrl = originalUrl.Split('?')[0];
            
            // Encode the URL for use as a query parameter
            var encodedUrl = HttpUtility.UrlEncode(cleanUrl);
            
            // Build wsrv.nl URL with parameters:
            // - url: the source image URL
            // - w: target width (1000px)
            // - h: target height (1000px)  
            // - fit: cover (crop to fill dimensions)
            // - we: without enlargement (don't upscale if image is smaller)
            // - output: webp (modern format for better compression)
            var wsrvUrl = $"{WsrvBaseUrl}?url={encodedUrl}&w={TargetSize}&h={TargetSize}&fit=cover&we&output=webp";
            
            return wsrvUrl;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Initialize method for compatibility with existing code
    /// wsrv.nl doesn't need initialization since it's a stateless proxy
    /// </summary>
    public Task InitializeCacheAsync()
    {
        // No-op: wsrv.nl doesn't need cache initialization
        return Task.CompletedTask;
    }
}
