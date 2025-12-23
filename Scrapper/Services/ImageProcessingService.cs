using Scrapper.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Scrapper.Services;

public class ImageProcessingService
{
    private readonly HttpClient _httpClient;
    private readonly FtpUploadService _ftpService;
    private const int TargetSize = 1000;

    public ImageProcessingService(HttpClient httpClient, FtpUploadService ftpService)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30); // 30 second timeout
        _ftpService = ftpService;
    }

    public async Task<string?> ProcessAndUploadImageAsync(string imageUrl, ProductInfo product, int imageIndex = 0)
    {
        try
        {
            // 1. Download Layer: Fetch the image with validation
            var imageData = await DownloadImageAsync(imageUrl);
            if (imageData == null || imageData.Length == 0)
            {
                Console.WriteLine($"Skipping image {imageIndex + 1}: Download failed or empty data");
                return null;
            }

            // 2. Processing Layer: Resize to 1000x1000
            var resizedData = await ResizeImageAsync(imageData, imageUrl);
            if (resizedData == null || resizedData.Length == 0)
            {
                Console.WriteLine($"Skipping image {imageIndex + 1}: Resize failed");
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
            Console.WriteLine($"Image processing error for {imageUrl}: {ex.Message}");
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
            Console.WriteLine($"\n[Image Processing] ========================================");
            Console.WriteLine($"[Image Processing] Product: {product.Name}");
            Console.WriteLine($"[Image Processing] Source: {product.Source}");
            Console.WriteLine($"[Image Processing] ProductId: {product.ProductId}");
            Console.WriteLine($"[Image Processing] ProductUrl: {product.ProductUrl}");
            Console.WriteLine($"[Image Processing] ========================================");
            
            // Validate product identification BEFORE processing
            if (string.IsNullOrEmpty(product.Source))
            {
                Console.WriteLine($"[Image Processing] ? ERROR: Product.Source is empty! Skipping upload.");
                await (onProgressMessage?.Invoke("? Product source missing - skipping upload") ?? Task.CompletedTask);
                return (null, new List<string>());
            }
            
            if (string.IsNullOrEmpty(product.ProductId))
            {
                Console.WriteLine($"[Image Processing] ? ERROR: Product.ProductId is empty! Skipping upload.");
                await (onProgressMessage?.Invoke("? Product ID missing - skipping upload") ?? Task.CompletedTask);
                return (null, new List<string>());
            }
            
            // **NEW: Check CDN cache first**
            var cdnConfig = new CdnFtpConfig();
            var cdnCache = new CdnCacheService(cdnConfig);  // Uses its own HttpClient now
            
            await (onProgressMessage?.Invoke($"?? Checking CDN cache for existing images...") ?? Task.CompletedTask);
            
            var (existingMain, existingAdditional) = await cdnCache.FindExistingImagesAsync(
                product.Source, 
                product.ProductId, 
                maxImagesToCheck: 3  // Only check for 3 images
            );
            
            if (existingMain != null)
            {
                await (onProgressMessage?.Invoke($"? Found {existingAdditional.Count + 1} existing images on CDN, skipping upload") ?? Task.CompletedTask);
                Console.WriteLine($"[Image Processing] ? Using cached images for {product.Source}/{product.ProductId}");
                return (existingMain, existingAdditional);
            }
            else
            {
                Console.WriteLine($"[Image Processing] No cached images found, proceeding with upload...");
            }

            // Get all image URLs
            var allImageUrls = product.GetAllImages();
            
            if (allImageUrls.Count == 0)
            {
                await (onProgressMessage?.Invoke("?? No images found for product") ?? Task.CompletedTask);
                Console.WriteLine($"[Image Processing] ?? No images to process for product");
                return (null, new List<string>());
            }

            // ? LIMIT: Only process first 3 images (1 main + 2 additional)
            const int MaxImagesToProcess = 3;
            var imagesToProcess = allImageUrls.Take(MaxImagesToProcess).ToList();
            
            Console.WriteLine($"[Image Processing] Processing {imagesToProcess.Count} of {allImageUrls.Count} images for {product.Source}/{product.ProductId} (limited to {MaxImagesToProcess})");
            await (onProgressMessage?.Invoke($"??? Processing {imagesToProcess.Count} images...") ?? Task.CompletedTask);

            int successCount = 0;
            int failCount = 0;

            // Process main image
            if (imagesToProcess.Count > 0)
            {
                Console.WriteLine($"[Image Processing] Uploading main image (1/{imagesToProcess.Count})...");
                var cdnUrl = await ProcessAndUploadImageAsync(imagesToProcess[0], product, 0);
                if (!string.IsNullOrEmpty(cdnUrl))
                {
                    mainImageUrl = cdnUrl;
                    successCount++;
                    Console.WriteLine($"[Image Processing] ? Main image uploaded: {cdnUrl}");
                }
                else
                {
                    failCount++;
                    Console.WriteLine($"[Image Processing] ? Main image upload failed");
                }
            }

            // Process additional images (up to 2 more)
            for (int i = 1; i < imagesToProcess.Count; i++)
            {
                Console.WriteLine($"[Image Processing] Uploading additional image ({i + 1}/{imagesToProcess.Count})...");
                var cdnUrl = await ProcessAndUploadImageAsync(imagesToProcess[i], product, i);
                if (!string.IsNullOrEmpty(cdnUrl))
                {
                    additionalImageUrls.Add(cdnUrl);
                    successCount++;
                    Console.WriteLine($"[Image Processing] ? Additional image {i + 1} uploaded: {cdnUrl}");
                }
                else
                {
                    failCount++;
                    Console.WriteLine($"[Image Processing] ? Additional image {i + 1} upload failed");
                }
            }

            var statusMsg = $"? Uploaded {successCount}/{imagesToProcess.Count} images to CDN";
            if (failCount > 0)
            {
                statusMsg += $" ({failCount} failed)";
            }
            Console.WriteLine($"[Image Processing] Summary: {statusMsg}");
            await (onProgressMessage?.Invoke(statusMsg) ?? Task.CompletedTask);
            
            return (mainImageUrl, additionalImageUrls);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Image Processing] ? EXCEPTION: {ex.Message}");
            Console.WriteLine($"[Image Processing] Stack Trace: {ex.StackTrace}");
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
                    Console.WriteLine($"Invalid image URL: {imageUrl}");
                    return null;
                }

                var response = await _httpClient.GetAsync(imageUrl);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Failed to download image (attempt {attempt}/{maxRetries}): {imageUrl} - Status: {response.StatusCode}");
                    
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(1000 * attempt); // Exponential backoff
                        continue;
                    }
                    return null;
                }

                var imageData = await response.Content.ReadAsByteArrayAsync();
                
                // Validate image data
                if (imageData == null || imageData.Length == 0)
                {
                    Console.WriteLine($"Downloaded empty image data from: {imageUrl}");
                    return null;
                }

                // Check minimum size (at least 1KB)
                if (imageData.Length < 1024)
                {
                    Console.WriteLine($"Downloaded image too small ({imageData.Length} bytes): {imageUrl}");
                    return null;
                }

                return imageData;
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine($"Download timeout (attempt {attempt}/{maxRetries}) for {imageUrl}");
                if (attempt < maxRetries)
                {
                    await Task.Delay(2000);
                    continue;
                }
                return null;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Download error (attempt {attempt}/{maxRetries}) for {imageUrl}: {ex.Message}");
                if (attempt < maxRetries)
                {
                    await Task.Delay(1000 * attempt);
                    continue;
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected download error for {imageUrl}: {ex.Message}");
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
            // Validate input
            if (imageData == null || imageData.Length == 0)
            {
                Console.WriteLine("Cannot resize: Empty image data");
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
                Console.WriteLine($"Unsupported image format from {sourceUrl}: {ex.Message}");
                return null;
            }
            catch (InvalidImageContentException ex)
            {
                Console.WriteLine($"Invalid image content from {sourceUrl}: {ex.Message}");
                return null;
            }

            using (image)
            {
                // Resize to 1000x1000 maintaining aspect ratio
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(TargetSize, TargetSize),
                    Mode = ResizeMode.Max // Maintains aspect ratio, fits within 1000x1000
                }));

                // If image is smaller than 1000x1000, pad it
                if (image.Width < TargetSize || image.Height < TargetSize)
                {
                    image.Mutate(x => x.Pad(TargetSize, TargetSize, Color.White));
                }

                // Save to memory stream with JPEG encoding
                using var outputStream = new MemoryStream();
                await image.SaveAsJpegAsync(outputStream, new JpegEncoder
                {
                    Quality = 90 // High quality
                });

                return outputStream.ToArray();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Resize error: {ex.Message}");
            return null;
        }
    }

    private string SanitizeFileName(string fileName)
    {
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
