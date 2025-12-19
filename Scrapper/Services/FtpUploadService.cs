using FluentFTP;
using Scrapper.Models;

namespace Scrapper.Services;

public class FtpUploadService
{
    private readonly CdnFtpConfig _config;

    public FtpUploadService(CdnFtpConfig config)
    {
        _config = config;
    }

    public async Task<string?> UploadImageAsync(byte[] imageData, string fileName, Action<int>? onProgress = null)
    {
        AsyncFtpClient? client = null;
        int maxRetries = 3;
        int retryDelay = 1000; // 1 second

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Create a new client for each upload to avoid connection issues
                client = new AsyncFtpClient(_config.Host, _config.Username, _config.Password, _config.Port);
                
                // Configure client for better reliability
                client.Config.ConnectTimeout = 30000; // 30 seconds
                client.Config.ReadTimeout = 60000; // 60 seconds - for reading responses
                client.Config.DataConnectionReadTimeout = 120000; // 120 seconds - for data transfer
                client.Config.DataConnectionConnectTimeout = 30000; // 30 seconds - for data connection
                client.Config.SocketKeepAlive = true; // Keep connection alive during transfers
                client.Config.DataConnectionType = FtpDataConnectionType.PASV; // Passive mode
                
                await client.Connect();
                
                // Create remote directory if it doesn't exist
                if (!await client.DirectoryExists(_config.RemotePath))
                {
                    await client.CreateDirectory(_config.RemotePath);
                }

                // Upload file
                var remotePath = $"{_config.RemotePath}/{fileName}";
                
                using var stream = new MemoryStream(imageData);
                var progress = new Progress<FtpProgress>(p =>
                {
                    var percentage = (int)p.Progress;
                    onProgress?.Invoke(percentage);
                });

                var result = await client.UploadStream(stream, remotePath, FtpRemoteExists.Overwrite, true, progress);
                
                await client.Disconnect();

                if (result == FtpStatus.Success)
                {
                    // Return CDN URL
                    return $"{_config.BaseUrl}{remotePath}";
                }
                
                Console.WriteLine($"FTP upload attempt {attempt} failed for {fileName} with status: {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FTP upload attempt {attempt}/{maxRetries} error for {fileName}: {ex.Message}");
                
                // Cleanup client
                if (client != null)
                {
                    try
                    {
                        if (client.IsConnected)
                        {
                            await client.Disconnect();
                        }
                        client.Dispose();
                    }
                    catch { }
                }

                if (attempt < maxRetries)
                {
                    await Task.Delay(retryDelay * attempt); // Exponential backoff
                }
                else
                {
                    Console.WriteLine($"FTP upload failed for {fileName} after {maxRetries} attempts");
                    return null;
                }
            }
        }

        return null;
    }

    public async Task<List<string>> UploadMultipleImagesAsync(
        List<byte[]> imagesData, 
        string productName,
        Func<int, int, Task>? onProgress = null)
    {
        var uploadedUrls = new List<string>();
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeProductName = SanitizeFileName(productName);

        for (int i = 0; i < imagesData.Count; i++)
        {
            var fileName = $"{safeProductName}_{timestamp}_{i + 1}.jpg";
            var url = await UploadImageAsync(imagesData[i], fileName);
            
            if (!string.IsNullOrEmpty(url))
            {
                uploadedUrls.Add(url);
            }

            if (onProgress != null)
            {
                await onProgress(i + 1, imagesData.Count);
            }
            
            // Small delay between uploads to avoid overwhelming the server
            await Task.Delay(200);
        }

        return uploadedUrls;
    }

    private string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        
        // Limit length
        if (sanitized.Length > 50)
            sanitized = sanitized.Substring(0, 50);
        
        // Replace spaces and special chars (Turkish characters)
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
