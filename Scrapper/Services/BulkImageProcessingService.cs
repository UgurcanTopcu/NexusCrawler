using Scrapper.Models;
using System.Collections.Concurrent;
using System.Web;

namespace Scrapper.Services;

public class BulkImageProcessingService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private const string WsrvBaseUrl = "https://wsrv.nl/";
    private const int TargetSize = 1000;

    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
            cts.Cancel();
    }

    public async Task ProcessExcelAsync(
        Stream excelStream,
        bool hasHeader,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;

        try
        {
            await onProgress(1, "📖 Reading Excel file...", "info");

            var reader = new BulkImageExcelReader();
            var excelData = reader.ReadExcel(excelStream, hasHeader);

            if (excelData.ImageCells.Count == 0)
            {
                await onProgress(100, "⚠️ No image URLs found in the Excel file", "warning");
                await SendComplete(onProgress, null, 0, 0);
                return;
            }

            await onProgress(5, $"✅ Found {excelData.ImageCells.Count} images across {excelData.ImageColumns.Count} columns", "success");

            // Log which columns will be processed
            var imageColList = string.Join(", ", excelData.ImageColumns.OrderBy(x => x));
            var dataColList = excelData.DataColumns.Except(excelData.ImageColumns).OrderBy(x => x).ToList();
            await onProgress(6, $"📊 Image columns: {imageColList} | Data columns (preserved): {(dataColList.Any() ? string.Join(", ", dataColList) : "None")}", "info");

            // Step 2: Initialize wsrv.nl service
            await onProgress(7, "🌐 Initializing wsrv.nl CDN (no upload needed)...", "info");
            await onProgress(8, "✅ wsrv.nl ready - converting URLs...", "success");

            // Step 3: Process each image (convert to wsrv.nl URL)
            var progressPerImage = 85.0 / excelData.ImageCells.Count;
            var currentProgress = 10.0;

            int successCount = 0;
            int failCount = 0;
            int imageIndex = 0;

            foreach (var imageCell in excelData.ImageCells)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, "⏹️ Processing stopped by user", "warning");
                    break;
                }

                imageIndex++;
                await onProgress((int)currentProgress, $"⚙️ Converting image {imageIndex}/{excelData.ImageCells.Count} (Row {imageCell.Row}, Col {imageCell.Column})...", "info");

                try
                {
                    Console.WriteLine($"[BulkImage] === Converting Image {imageIndex}/{excelData.ImageCells.Count} ===");
                    Console.WriteLine($"[BulkImage] Row: {imageCell.Row}, Column: {imageCell.Column}");
                    Console.WriteLine($"[BulkImage] Original URL: {imageCell.OriginalUrl}");

                    // Validate the URL
                    if (string.IsNullOrWhiteSpace(imageCell.OriginalUrl))
                    {
                        imageCell.Error = "Empty URL";
                        imageCell.IsProcessed = true;
                        failCount++;
                        await onProgress((int)currentProgress, $"❌ Empty URL: Row {imageCell.Row}, Col {imageCell.Column}", "error");
                        currentProgress += progressPerImage;
                        continue;
                    }

                    // Convert to wsrv.nl URL
                    var wsrvUrl = ConvertToWsrvUrl(imageCell.OriginalUrl);
                    
                    if (!string.IsNullOrEmpty(wsrvUrl))
                    {
                        imageCell.CdnUrl = wsrvUrl;
                        imageCell.IsProcessed = true;
                        successCount++;

                        // Update the cell value in the data
                        if (imageCell.Row <= excelData.AllCells.Count && imageCell.Column <= excelData.AllCells[imageCell.Row - 1].Count)
                        {
                            excelData.AllCells[imageCell.Row - 1][imageCell.Column - 1] = wsrvUrl;
                        }

                        Console.WriteLine($"[BulkImage] ✅ Converted to wsrv.nl URL: {wsrvUrl}");
                        await onProgress((int)currentProgress, $"✅ Row {imageCell.Row}, Col {imageCell.Column}: Converted", "success");
                    }
                    else
                    {
                        imageCell.Error = "Invalid URL format";
                        imageCell.IsProcessed = true;
                        failCount++;
                        Console.WriteLine($"[BulkImage] ❌ URL conversion failed");
                        await onProgress((int)currentProgress, $"❌ Conversion failed: Row {imageCell.Row}, Col {imageCell.Column}", "error");
                    }
                }
                catch (Exception ex)
                {
                    imageCell.Error = $"{ex.GetType().Name}: {ex.Message}";
                    imageCell.IsProcessed = true;
                    failCount++;
                    Console.WriteLine($"[BulkImage] EXCEPTION at Row {imageCell.Row}, Col {imageCell.Column}");
                    Console.WriteLine($"[BulkImage] Exception: {ex.GetType().Name} - {ex.Message}");
                    await onProgress((int)currentProgress, $"❌ Error at Row {imageCell.Row}, Col {imageCell.Column}: {ex.Message}", "error");
                }

                currentProgress += progressPerImage;

                // Small delay for UI responsiveness
                await Task.Delay(50);
            }

            // Step 4: Export results
            await onProgress(95, "📊 Creating result Excel with converted URLs...", "info");

            var resultFileName = $"BulkImages_wsrvnl_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            var resultFilePath = Path.Combine(Directory.GetCurrentDirectory(), resultFileName);

            try
            {
                var exporter = new BulkImageExcelExporter();
                exporter.Export(excelData, resultFilePath);

                var summary = $"✅ Done! {successCount} converted to wsrv.nl, {failCount} failed";
                if (dataColList.Any())
                {
                    summary += $" | {dataColList.Count} data column(s) preserved";
                }

                await onProgress(100, summary, "success");
                await SendComplete(onProgress, resultFileName, successCount, failCount);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BulkImage] Excel export error: {ex.Message}");
                await onProgress(100, $"❌ Error creating Excel: {ex.Message}", "error");
                await SendComplete(onProgress, null, successCount, failCount);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BulkImage] Fatal error: {ex.Message}");
            Console.WriteLine($"[BulkImage] Stack: {ex.StackTrace}");
            await onProgress(100, $"❌ Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, 0, 0);
        }
        finally
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessions.TryRemove(sessionId, out _);
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Process a list of URLs directly (without Excel file)
    /// </summary>
    public async Task ProcessUrlListAsync(
        List<string> urls,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;

        try
        {
            await onProgress(1, "📋 Processing URL list...", "info");

            // Filter out empty URLs
            var validUrls = urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();

            if (validUrls.Count == 0)
            {
                await onProgress(100, "⚠️ No valid URLs provided", "warning");
                await SendComplete(onProgress, null, 0, 0);
                return;
            }

            await onProgress(5, $"✅ Found {validUrls.Count} URLs to process", "success");
            await onProgress(7, "🌐 Initializing wsrv.nl CDN (no upload needed)...", "info");
            await onProgress(8, "✅ wsrv.nl ready - converting URLs...", "success");

            // Process each URL
            var progressPerUrl = 85.0 / validUrls.Count;
            var currentProgress = 10.0;

            int successCount = 0;
            int failCount = 0;

            var results = new List<(string originalUrl, string? convertedUrl, bool success, string? error)>();

            for (int i = 0; i < validUrls.Count; i++)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, "⏹️ Processing stopped by user", "warning");
                    break;
                }

                var url = validUrls[i];
                await onProgress((int)currentProgress, $"⚙️ Converting URL {i + 1}/{validUrls.Count}...", "info");

                try
                {
                    Console.WriteLine($"[BulkImage] Converting URL {i + 1}/{validUrls.Count}: {url}");

                    // Validate the URL
                    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
                    {
                        Console.WriteLine($"[BulkImage] Invalid URL format");
                        failCount++;
                        results.Add((url, null, false, "Invalid URL format"));
                        currentProgress += progressPerUrl;
                        continue;
                    }

                    // Convert to wsrv.nl URL
                    var wsrvUrl = ConvertToWsrvUrl(url);

                    if (!string.IsNullOrEmpty(wsrvUrl))
                    {
                        Console.WriteLine($"[BulkImage] ✓ Converted to: {wsrvUrl}");
                        successCount++;
                        results.Add((url, wsrvUrl, true, null));
                    }
                    else
                    {
                        Console.WriteLine($"[BulkImage] ✗ Conversion failed");
                        failCount++;
                        results.Add((url, null, false, "Conversion failed"));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BulkImage] Error converting URL: {ex.Message}");
                    failCount++;
                    results.Add((url, null, false, ex.Message));
                }

                currentProgress += progressPerUrl;
            }

            // Export results to Excel
            await onProgress(95, "📊 Creating Excel report...", "info");

            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var resultFileName = $"BulkImage_URLs_{timestamp}.xlsx";
                var resultFilePath = Path.Combine(Directory.GetCurrentDirectory(), resultFileName);

                var exporter = new BulkImageExcelExporter();
                exporter.ExportUrlResults(results, resultFilePath);

                var summary = $"✅ Done! {successCount} succeeded, {failCount} failed";
                await onProgress(100, summary, "success");
                await SendComplete(onProgress, resultFileName, successCount, failCount);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BulkImage] Excel export error: {ex.Message}");
                await onProgress(100, $"❌ Error creating Excel: {ex.Message}", "error");
                await SendComplete(onProgress, null, successCount, failCount);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BulkImage] Fatal error: {ex.Message}");
            await onProgress(100, $"❌ Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, 0, 0);
        }
        finally
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessions.TryRemove(sessionId, out _);
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Convert an image URL to wsrv.nl CDN URL with resizing to 1000x1000 PNG
    /// Format: https://wsrv.nl/?url={encoded_url}&w={width}&h={height}&fit=cover&output=png
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
            // - fit: cover (crop to fill exact 1000x1000 dimensions)
            // - output: png
            var wsrvUrl = $"{WsrvBaseUrl}?url={encodedUrl}&w={TargetSize}&h={TargetSize}&fit=cover&output=png";
            
            return wsrvUrl;
        }
        catch
        {
            return string.Empty;
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