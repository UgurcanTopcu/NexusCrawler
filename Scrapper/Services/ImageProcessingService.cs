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

    public async Task<string?> ProcessAndUploadImageAsync(string imageUrl, string productName, int imageIndex = 0)
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

            // 3. Storage Layer: Upload to FTP
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeProductName = SanitizeFileName(productName);
            var fileName = $"{safeProductName}_{timestamp}_{imageIndex + 1}.jpg";
            
            var cdnUrl = await _ftpService.UploadImageAsync(resizedData, fileName);
            
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
            // Get all image URLs
            var allImageUrls = product.GetAllImages();
            
            if (allImageUrls.Count == 0)
            {
                await (onProgressMessage?.Invoke("?? No images found for product") ?? Task.CompletedTask);
                return (null, new List<string>());
            }

            await (onProgressMessage?.Invoke($"??? Processing {allImageUrls.Count} images...") ?? Task.CompletedTask);

            int successCount = 0;
            int failCount = 0;

            // Process main image
            if (allImageUrls.Count > 0)
            {
                var cdnUrl = await ProcessAndUploadImageAsync(allImageUrls[0], product.Name, 0);
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

            // Process additional images
            for (int i = 1; i < allImageUrls.Count; i++)
            {
                var cdnUrl = await ProcessAndUploadImageAsync(allImageUrls[i], product.Name, i);
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

            var statusMsg = $"? Uploaded {successCount}/{allImageUrls.Count} images to CDN";
            if (failCount > 0)
            {
                statusMsg += $" ({failCount} failed)";
            }
            await (onProgressMessage?.Invoke(statusMsg) ?? Task.CompletedTask);
            
            return (mainImageUrl, additionalImageUrls);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing product images: {ex.Message}");
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
