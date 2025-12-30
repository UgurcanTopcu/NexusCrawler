using Scrapper.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.Collections.Concurrent;

namespace Scrapper.Services;

/// <summary>
/// Service to process bulk image uploads from Excel
/// Downloads images, resizes to 1000x1000, uploads to CDN, returns new URLs
/// </summary>
public class BulkImageProcessingService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private const int TargetSize = 1000;
    
    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            Console.WriteLine($"[BulkImage] Session {sessionId} cancelled");
        }
    }

    public async Task ProcessExcelAsync(
        Stream excelStream,
        bool hasHeader,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessions[sessionId] = cts;
        }
        var cancellationToken = cts.Token;

        HttpClient? httpClient = null;
        
        try
        {
            await onProgress(1, "?? Reading Excel file...", "info");

            // Step 1: Read Excel
            var reader = new BulkImageExcelReader();
            var excelData = reader.ReadExcel(excelStream, hasHeader);

            if (excelData.ImageCells.Count == 0)
            {
                await onProgress(100, "?? No image URLs found in the Excel file", "warning");
                await SendComplete(onProgress, null, 0, 0);
                return;
            }

            await onProgress(5, $"? Found {excelData.ImageCells.Count} images across {excelData.ImageColumns.Count} columns", "success");
            
            // Log which columns will be processed
            var imageColList = string.Join(", ", excelData.ImageColumns.OrderBy(x => x));
            var dataColList = excelData.DataColumns.Except(excelData.ImageColumns).OrderBy(x => x).ToList();
            await onProgress(6, $"?? Image columns: {imageColList} | Data columns (preserved): {(dataColList.Any() ? string.Join(", ", dataColList) : "None")}", "info");

            // Step 2: Initialize services
            await onProgress(7, "?? Initializing CDN connection...", "info");
            
            var ftpConfig = new CdnFtpConfig();
            var ftpService = new FtpUploadService(ftpConfig);
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            // Step 3: Process each image
            var progressPerImage = 85.0 / excelData.ImageCells.Count;
            var currentProgress = 10.0;
            
            int successCount = 0;
            int failCount = 0;
            int imageIndex = 0;

            foreach (var imageCell in excelData.ImageCells)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, "?? Processing stopped by user", "warning");
                    break;
                }

                imageIndex++;
                await onProgress((int)currentProgress, $"??? Processing image {imageIndex}/{excelData.ImageCells.Count} (Row {imageCell.Row}, Col {imageCell.Column})...", "info");

                try
                {
                    // Download
                    Console.WriteLine($"[BulkImage] === Processing Image {imageIndex}/{excelData.ImageCells.Count} ===");
                    Console.WriteLine($"[BulkImage] Row: {imageCell.Row}, Column: {imageCell.Column}");
                    Console.WriteLine($"[BulkImage] URL: {imageCell.OriginalUrl}");
                    
                    var imageData = await DownloadImageAsync(httpClient, imageCell.OriginalUrl);
                    if (imageData == null)
                    {
                        imageCell.Error = "Download failed - Check console for details";
                        imageCell.IsProcessed = true;
                        failCount++;
                        await onProgress((int)currentProgress, $"? Download failed: Row {imageCell.Row}, Col {imageCell.Column} - Check URL", "error");
                        currentProgress += progressPerImage;
                        continue;
                    }

                    Console.WriteLine($"[BulkImage] ? Image downloaded, now resizing...");

                    // Resize
                    var resizedData = await ResizeImageAsync(imageData);
                    if (resizedData == null)
                    {
                        imageCell.Error = "Resize failed - Invalid image format";
                        imageCell.IsProcessed = true;
                        failCount++;
                        await onProgress((int)currentProgress, $"? Resize failed: Row {imageCell.Row}, Col {imageCell.Column}", "error");
                        currentProgress += progressPerImage;
                        continue;
                    }

                    Console.WriteLine($"[BulkImage] ? Image resized, now uploading to CDN...");

                    // Upload to CDN
                    // Use a unique folder: bulk_upload/timestamp/
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var fileName = $"image_{imageCell.Row}_{imageCell.Column}.jpg";
                    
                    var cdnUrl = await ftpService.UploadImageAsync(
                        resizedData,
                        fileName,
                        "bulk_upload",
                        timestamp
                    );

                    if (!string.IsNullOrEmpty(cdnUrl))
                    {
                        imageCell.CdnUrl = cdnUrl;
                        imageCell.IsProcessed = true;
                        successCount++;
                        
                        // Update the cell value in the data
                        if (imageCell.Row <= excelData.AllCells.Count && imageCell.Column <= excelData.AllCells[imageCell.Row - 1].Count)
                        {
                            excelData.AllCells[imageCell.Row - 1][imageCell.Column - 1] = cdnUrl;
                        }
                        
                        Console.WriteLine($"[BulkImage] ? Upload successful! CDN URL: {cdnUrl}");
                        await onProgress((int)currentProgress, $"? Row {imageCell.Row}, Col {imageCell.Column}: Uploaded successfully", "success");
                    }
                    else
                    {
                        imageCell.Error = "Upload failed - FTP error";
                        imageCell.IsProcessed = true;
                        failCount++;
                        Console.WriteLine($"[BulkImage] ? Upload failed to FTP");
                        await onProgress((int)currentProgress, $"? Upload failed: Row {imageCell.Row}, Col {imageCell.Column}", "error");
                    }
                }
                catch (Exception ex)
                {
                    imageCell.Error = ex.Message;
                    imageCell.IsProcessed = true;
                    failCount++;
                    Console.WriteLine($"[BulkImage] EXCEPTION at Row {imageCell.Row}, Col {imageCell.Column}");
                    Console.WriteLine($"[BulkImage] Exception: {ex.GetType().Name} - {ex.Message}");
                    Console.WriteLine($"[BulkImage] Stack: {ex.StackTrace}");
                    await onProgress((int)currentProgress, $"? Error at Row {imageCell.Row}, Col {imageCell.Column}: {ex.Message}", "error");
                }

                currentProgress += progressPerImage;
                
                // Small delay between uploads
                await Task.Delay(100);
            }

            // Step 4: Export results
            await onProgress(95, "?? Creating result Excel with all columns preserved...", "info");

            var resultFileName = $"BulkImages_Processed_{DateTime.Now:yyyyMMdd_HHmms}.xlsx";
            var resultFilePath = Path.Combine(Directory.GetCurrentDirectory(), resultFileName);

            try
            {
                var exporter = new BulkImageExcelExporter();
                exporter.Export(excelData, resultFilePath);

                await onProgress(100, $"? Done! {successCount} uploaded, {failCount} failed | All non-image columns preserved", "success");
                await SendComplete(onProgress, resultFileName, successCount, failCount);
            }
            catch (Exception ex)
            {
                await onProgress(100, $"? Error creating Excel: {ex.Message}", "error");
                await SendComplete(onProgress, null, successCount, failCount);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BulkImage] Error: {ex.Message}");
            await onProgress(100, $"? Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, 0, 0);
        }
        finally
        {
            httpClient?.Dispose();
            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessions.TryRemove(sessionId, out _);
            }
            cts.Dispose();
        }
    }

    private async Task<byte[]?> DownloadImageAsync(HttpClient httpClient, string imageUrl)
    {
        int maxRetries = 3;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Validate URL
                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    Console.WriteLine($"[BulkImage] ERROR: Image URL is empty or whitespace");
                    return null;
                }
                
                if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
                {
                    Console.WriteLine($"[BulkImage] ERROR: Invalid URL format: {imageUrl}");
                    return null;
                }

                Console.WriteLine($"[BulkImage] Downloading (attempt {attempt}/{maxRetries}): {imageUrl.Substring(0, Math.Min(80, imageUrl.Length))}...");

                var response = await httpClient.GetAsync(imageUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    var reasonPhrase = response.ReasonPhrase;
                    
                    Console.WriteLine($"[BulkImage] HTTP Error (attempt {attempt}/{maxRetries}): Status {statusCode} ({reasonPhrase})");
                    Console.WriteLine($"[BulkImage] URL: {imageUrl}");
                    
                    // Retry on server errors (5xx) or rate limiting (429)
                    if (attempt < maxRetries && (statusCode >= 500 || statusCode == 429))
                    {
                        var delayMs = 1000 * attempt; // Exponential backoff: 1s, 2s, 3s
                        Console.WriteLine($"[BulkImage] Retrying in {delayMs}ms...");
                        await Task.Delay(delayMs);
                        continue;
                    }
                    
                    return null;
                }

                var imageData = await response.Content.ReadAsByteArrayAsync();
                
                if (imageData == null || imageData.Length == 0)
                {
                    Console.WriteLine($"[BulkImage] ERROR: Downloaded empty data from: {imageUrl}");
                    return null;
                }

                if (imageData.Length < 1024)
                {
                    Console.WriteLine($"[BulkImage] WARNING: Image very small ({imageData.Length} bytes), might be invalid: {imageUrl}");
                    // Still try to process it, might be a valid tiny image
                }

                Console.WriteLine($"[BulkImage] ? Downloaded successfully: {imageData.Length} bytes");
                return imageData;
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine($"[BulkImage] TIMEOUT (attempt {attempt}/{maxRetries}): {imageUrl}");
                Console.WriteLine($"[BulkImage] Error: {ex.Message}");
                
                if (attempt < maxRetries)
                {
                    await Task.Delay(2000); // Wait 2s before retry on timeout
                    continue;
                }
                return null;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"[BulkImage] HTTP Exception (attempt {attempt}/{maxRetries}): {imageUrl}");
                Console.WriteLine($"[BulkImage] Error: {ex.Message}");
                Console.WriteLine($"[BulkImage] Inner Exception: {ex.InnerException?.Message ?? "None"}");
                
                if (attempt < maxRetries)
                {
                    await Task.Delay(1000 * attempt);
                    continue;
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BulkImage] UNEXPECTED ERROR downloading: {imageUrl}");
                Console.WriteLine($"[BulkImage] Exception Type: {ex.GetType().Name}");
                Console.WriteLine($"[BulkImage] Error: {ex.Message}");
                Console.WriteLine($"[BulkImage] Stack Trace: {ex.StackTrace}");
                return null;
            }
        }

        Console.WriteLine($"[BulkImage] FAILED after {maxRetries} attempts: {imageUrl}");
        return null;
    }

    private async Task<byte[]?> ResizeImageAsync(byte[] imageData)
    {
        try
        {
            if (imageData == null || imageData.Length == 0)
            {
                Console.WriteLine("[BulkImage] ERROR: Cannot resize - empty image data");
                return null;
            }

            Console.WriteLine($"[BulkImage] Resizing image ({imageData.Length} bytes)...");

            using var inputStream = new MemoryStream(imageData);
            Image? image = null;
            
            try
            {
                image = await Image.LoadAsync(inputStream);
                Console.WriteLine($"[BulkImage] Image loaded: {image.Width}x{image.Height} pixels");
            }
            catch (UnknownImageFormatException ex)
            {
                Console.WriteLine($"[BulkImage] ERROR: Unsupported image format - {ex.Message}");
                return null;
            }
            catch (InvalidImageContentException ex)
            {
                Console.WriteLine($"[BulkImage] ERROR: Invalid image content - {ex.Message}");
                return null;
            }

            using (image)
            {
                // Resize to fit within 1000x1000 while maintaining aspect ratio
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(TargetSize, TargetSize),
                    Mode = ResizeMode.Max
                }));

                Console.WriteLine($"[BulkImage] Resized to: {image.Width}x{image.Height}");

                // Pad to exactly 1000x1000 with white background
                if (image.Width < TargetSize || image.Height < TargetSize)
                {
                    image.Mutate(x => x.Pad(TargetSize, TargetSize, Color.White));
                    Console.WriteLine($"[BulkImage] Padded to: {TargetSize}x{TargetSize}");
                }

                using var outputStream = new MemoryStream();
                await image.SaveAsJpegAsync(outputStream, new JpegEncoder { Quality = 90 });
                
                var resizedData = outputStream.ToArray();
                Console.WriteLine($"[BulkImage] ? Resize complete: {resizedData.Length} bytes");
                
                return resizedData;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BulkImage] RESIZE ERROR: {ex.GetType().Name} - {ex.Message}");
            Console.WriteLine($"[BulkImage] Stack: {ex.StackTrace}");
            return null;
        }
    }

    private async Task SendComplete(Func<int, string, string, Task> onProgress, string? fileName, int successCount, int failCount)
    {
        var data = new
        {
            complete = true,
            downloadUrl = fileName != null ? $"/api/download/{fileName}" : null,
            fileName = fileName,
            successCount = successCount,
            failCount = failCount
        };

        var json = System.Text.Json.JsonSerializer.Serialize(data);
        await onProgress(100, json, "complete");
    }
}
