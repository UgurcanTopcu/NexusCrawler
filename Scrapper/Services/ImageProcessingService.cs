using Scrapper.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Scrapper.Services;

public class ImageProcessingService
{
    private readonly HttpClient _httpClient;
    private readonly FtpUploadService _ftpService;
    private readonly CdnCacheService _cdnCache;
    private const int TargetSize = 1000;

    public ImageProcessingService(HttpClient httpClient, FtpUploadService ftpService, CdnCacheService cdnCache)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _ftpService = ftpService;
        _cdnCache = cdnCache;
    }

    /// <summary>
    /// Initialize the CDN cache - call this once before processing any products
    /// </summary>
    public async Task InitializeCacheAsync()
    {
        await _cdnCache.InitializeCacheAsync();
    }

    public async Task<string?> ProcessAndUploadImageAsync(string imageUrl, ProductInfo product, int imageIndex = 0)
    {
        try
        {
            // 1. Download Layer: Fetch the image with validation
            var imageData = await DownloadImageAsync(imageUrl);
            if (imageData == null || imageData.Length == 0)
            {
                return null;
            }

            // 2. Processing Layer: Resize to 1000x1000
            var resizedData = await ResizeImageAsync(imageData, imageUrl);
            if (resizedData == null || resizedData.Length == 0)
            {
                return null;
            }

            // 3. Storage Layer: Upload to FTP with site/productId structure
            var fileName = $"image_{imageIndex + 1}.jpg";
            
            var cdnUrl = await _ftpService.UploadImageAsync(
                resizedData, 
                fileName, 
                product.Source, 
                product.ProductId
            );
            
            return cdnUrl;
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    public async Task<(string? mainImage, List<string> additionalImages)> ProcessProductImagesAsync(
        ProductInfo product,
        Func<string, Task>? onProgressMessage = null)
    {
        var mainImageUrl = string.Empty;
        var additionalImageUrls = new List<string>();

        try
        {
            // DIAGNOSTIC: Log product identification





            
            // Validate product identification BEFORE processing
            if (string.IsNullOrEmpty(product.Source))
            {
                await (onProgressMessage?.Invoke("? Product source missing - skipping upload") ?? Task.CompletedTask);
                return (null, new List<string>());
            }
            
            if (string.IsNullOrEmpty(product.ProductId))
            {
                await (onProgressMessage?.Invoke("? Product ID missing - skipping upload") ?? Task.CompletedTask);
                return (null, new List<string>());
            }
            
            // **FAST LOOKUP: Check CDN cache using pre-fetched folder list**
            if (_cdnCache.ProductExistsInCache(product.Source, product.ProductId))
            {
                
                // Generate URLs from cache (no HTTP requests needed)
                var cachedMain = _cdnCache.GenerateCdnUrl(product.Source, product.ProductId, 0);
                var cachedAdditional = new List<string>();
                for (int i = 1; i < 3; i++)
                {
                    cachedAdditional.Add(_cdnCache.GenerateCdnUrl(product.Source, product.ProductId, i));
                }
                
                await (onProgressMessage?.Invoke($"? Found cached images on CDN, skipping upload") ?? Task.CompletedTask);
                return (cachedMain, cachedAdditional);
            }

            // Get all image URLs
            var allImageUrls = product.GetAllImages();
            
            if (allImageUrls.Count == 0)
            {
                await (onProgressMessage?.Invoke("?? No images found for product") ?? Task.CompletedTask);
                return (null, new List<string>());
            }

            // ? LIMIT: Only process first 3 images (1 main + 2 additional)
            const int MaxImagesToProcess = 3;
            var imagesToProcess = allImageUrls.Take(MaxImagesToProcess).ToList();
            await (onProgressMessage?.Invoke($"??? Processing {imagesToProcess.Count} images...") ?? Task.CompletedTask);

            int successCount = 0;
            int failCount = 0;

            // Process main image
            if (imagesToProcess.Count > 0)
            {
                var cdnUrl = await ProcessAndUploadImageAsync(imagesToProcess[0], product, 0);
                if (!string.IsNullOrEmpty(cdnUrl))
                {
                    mainImageUrl = cdnUrl;
                    successCount++;
                }
                else
                {
                    failCount++;
                }
            }

            // Process additional images (up to 2 more)
            for (int i = 1; i < imagesToProcess.Count; i++)
            {
                var cdnUrl = await ProcessAndUploadImageAsync(imagesToProcess[i], product, i);
                if (!string.IsNullOrEmpty(cdnUrl))
                {
                    additionalImageUrls.Add(cdnUrl);
                    successCount++;
                }
                else
                {
                    failCount++;
                }
            }

            // Add to cache after successful upload
            if (successCount > 0)
            {
                _cdnCache.AddToCache(product.Source, product.ProductId);
            }

            var statusMsg = $"? Uploaded {successCount}/{imagesToProcess.Count} images to CDN";
            if (failCount > 0)
            {
                statusMsg += $" ({failCount} failed)";
            }
            await (onProgressMessage?.Invoke(statusMsg) ?? Task.CompletedTask);
            
            return (mainImageUrl, additionalImageUrls);
        }
        catch (Exception ex)
        {

            return (null, new List<string>());
        }
    }

    // Download Layer: Fetch image from URL with retry logic
    private async Task<byte[]?> DownloadImageAsync(string imageUrl)
    {
        int maxRetries = 3;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Validate URL
                if (string.IsNullOrWhiteSpace(imageUrl) || !Uri.TryCreate(imageUrl, UriKind.Absolute, out _))
                {
                    return null;
                }

                var response = await _httpClient.GetAsync(imageUrl);
                if (!response.IsSuccessStatusCode)
                {
                    
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(1000 * attempt);
                        continue;
                    }
                    return null;
                }

                var imageData = await response.Content.ReadAsByteArrayAsync();
                
                if (imageData == null || imageData.Length == 0)
                {
                    return null;
                }

                if (imageData.Length < 1024)
                {
                    return null;
                }

                return imageData;
            }
            catch (TaskCanceledException)
            {
                if (attempt < maxRetries)
                {
                    await Task.Delay(2000);
                    continue;
                }
                return null;
            }
            catch (HttpRequestException ex)
            {
                if (attempt < maxRetries)
                {
                    await Task.Delay(1000 * attempt);
                    continue;
                }
                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        return null;
    }

    // Processing Layer: Resize image to 1000x1000
    private async Task<byte[]?> ResizeImageAsync(byte[] imageData, string sourceUrl = "")
    {
        try
        {
            if (imageData == null || imageData.Length == 0)
            {
                return null;
            }

            Image? image = null;
            try
            {
                using var inputStream = new MemoryStream(imageData);
                image = await Image.LoadAsync(inputStream);
            }
            catch (UnknownImageFormatException ex)
            {
                return null;
            }
            catch (InvalidImageContentException ex)
            {
                return null;
            }

            using (image)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(TargetSize, TargetSize),
                    Mode = ResizeMode.Max
                }));

                if (image.Width < TargetSize || image.Height < TargetSize)
                {
                    image.Mutate(x => x.Pad(TargetSize, TargetSize, Color.White));
                }

                using var outputStream = new MemoryStream();
                await image.SaveAsJpegAsync(outputStream, new JpegEncoder
                {
                    Quality = 90
                });

                return outputStream.ToArray();
            }
        }
        catch (Exception ex)
        {
            return null;
        }
    }
}
