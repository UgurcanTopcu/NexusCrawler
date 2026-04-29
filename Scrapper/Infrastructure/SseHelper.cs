using System.Text.Json;

namespace Scrapper.Infrastructure;

/// <summary>
/// Helper class for Server-Sent Events (SSE) streaming responses
/// Eliminates code duplication across API endpoints
/// </summary>
public static class SseHelper
{
    /// <summary>
    /// Creates a progress callback that writes SSE events to the stream
    /// </summary>
    /// <param name="writer">The StreamWriter to write to</param>
    /// <returns>A callback function that can be passed to services</returns>
    public static Func<int, string, string, Task> CreateProgressCallback(StreamWriter writer)
    {
        return async (progress, message, type) =>
        {
            var data = JsonSerializer.Serialize(new
            {
                progress,
                message,
                type
            });
            // Use WriteAsync with explicit \n\n to guarantee proper SSE event separation
            // on all platforms (WriteLineAsync adds \r\n on Windows, breaking \n\n splits)
            await writer.WriteAsync($"data: {data}\n\n");
            await writer.FlushAsync();
        };
    }

    /// <summary>
    /// Sends an error message via SSE
    /// </summary>
    public static async Task SendErrorAsync(StreamWriter writer, string message)
    {
        var errorData = JsonSerializer.Serialize(new
        {
            progress = 100,
            message = $"Error: {message}",
            type = "error",
            complete = true
        });
        await writer.WriteAsync($"data: {errorData}\n\n");
        await writer.FlushAsync();
    }

    /// <summary>
    /// Sends a "no file uploaded" error via SSE
    /// </summary>
    public static async Task SendNoFileErrorAsync(StreamWriter writer)
    {
        var errorData = JsonSerializer.Serialize(new
        {
            progress = 100,
            message = "No file uploaded",
            type = "error",
            complete = true
        });
        await writer.WriteAsync($"data: {errorData}\n\n");
        await writer.FlushAsync();
    }

    /// <summary>
    /// Reads an uploaded file into a MemoryStream
    /// </summary>
    public static async Task<MemoryStream> ReadFileToMemoryStreamAsync(IFormFile file)
    {
        var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        memoryStream.Position = 0;
        return memoryStream;
    }
}
