using Scrapper.Models;
using System.Text.RegularExpressions;

namespace Scrapper.Services;

/// <summary>
/// Service to check if images are already uploaded to CDN and retrieve their URLs
/// </summary>
public class CdnCacheService
{
    private readonly HttpClient _httpClient;
    private readonly CdnFtpConfig _config;
    
    public CdnCacheService(CdnFtpConfig config)
    {
        // Create our own HttpClient for cache checks - don't share with image downloader
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _config = config;
    }

    /// <summary>
    /// Generate expected CDN URL for a product and image index with site/productId structure
    /// </summary>
    public string GenerateCdnUrl(string site, string productId, int imageIndex)
    {
        // New pattern: https://mmstr.sm.mncdn.com/images/products/{site}/{productId}/image_{index}.jpg
        var fileName = $"image_{imageIndex + 1}.jpg";
        
        var remotePath = _config.RemotePath ?? "/";
        return $"{_config.BaseUrl}{remotePath.TrimEnd('/')}/{site}/{productId}/{fileName}";
    }

    /// <summary>
    /// Check if an image exists on CDN by trying to HEAD request it
    /// </summary>
    public async Task<bool> ImageExistsAsync(string cdnUrl)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, cdnUrl);
            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Find existing CDN images for a product using site/productId folder structure
    /// </summary>
    public async Task<(string? mainImage, List<string> additionalImages)> FindExistingImagesAsync(
        string site,
        string productId, 
        int maxImagesToCheck = 3)
    {
        Console.WriteLine($"\n[CDN Cache] Checking for existing images: {site}/{productId}");
        
        string? mainImageUrl = null;
        var additionalImages = new List<string>();

        // Skip cache check if site or productId is missing
        if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(productId))
        {
            Console.WriteLine($"[CDN Cache] Skipping cache check - missing site or productId");
            return (null, new List<string>());
        }

        // Check main image (image_1.jpg)
        var mainUrl = GenerateCdnUrl(site, productId, 0);
        if (await ImageExistsAsync(mainUrl))
        {
            mainImageUrl = mainUrl;
            Console.WriteLine($"[CDN Cache] ? Found existing main image: {mainUrl}");
        }
        else
        {
            Console.WriteLine($"[CDN Cache] Main image not found: {mainUrl}");
        }

        // Check additional images (image_2.jpg, image_3.jpg, etc.) - up to maxImagesToCheck
        for (int i = 1; i < maxImagesToCheck; i++)
        {
            var url = GenerateCdnUrl(site, productId, i);
            if (await ImageExistsAsync(url))
            {
                additionalImages.Add(url);
                Console.WriteLine($"[CDN Cache] ? Found existing additional image {i + 1}: {url}");
            }
            else
            {
                // Stop checking once we hit a missing image
                break;
            }
        }

        if (mainImageUrl != null || additionalImages.Count > 0)
        {
            Console.WriteLine($"[CDN Cache] ? Found {(mainImageUrl != null ? 1 : 0) + additionalImages.Count} existing images on CDN");
        }
        else
        {
            Console.WriteLine($"[CDN Cache] No existing images found on CDN for {site}/{productId}");
        }

        return (mainImageUrl, additionalImages);
    }

    private string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "product";

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        
        if (sanitized.Length > 50)
            sanitized = sanitized.Substring(0, 50);
        
        sanitized = sanitized.Replace(" ", "_")
                             .Replace("þ", "s").Replace("Þ", "S")
                             .Replace("ý", "i").Replace("Ý", "I")
                             .Replace("ð", "g").Replace("Ð", "G")
                             .Replace("ü", "u").Replace("Ü", "U")
                             .Replace("ö", "o").Replace("Ö", "O")
                             .Replace("ç", "c").Replace("Ç", "C");

        return sanitized.ToLower();
    }
}
