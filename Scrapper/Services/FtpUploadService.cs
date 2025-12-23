using Scrapper.Models;

namespace Scrapper.Services;

public class FtpUploadService
{
    private readonly CdnFtpConfig _config;

    public FtpUploadService(CdnFtpConfig config)
    {
        _config = config;
    }

    public async Task<string?> UploadImageAsync(byte[] imageData, string fileName, string site = "", string productId = "", Action<int>? onProgress = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                Console.WriteLine($"\n[FTP] ========== UPLOAD START ==========");
                Console.WriteLine($"[FTP] File: {fileName}");
                Console.WriteLine($"[FTP] Size: {imageData.Length} bytes");
                Console.WriteLine($"[FTP] Site: {site}");
                Console.WriteLine($"[FTP] Product ID: {productId}");
                Console.WriteLine($"[FTP] Host: {_config.Host}:{_config.Port}");
                Console.WriteLine($"[FTP] User: {_config.Username}");
                
                // Setup session options - EXACTLY like Filestash
                WinSCP.SessionOptions sessionOptions = new WinSCP.SessionOptions
                {
                    Protocol = WinSCP.Protocol.Ftp,  // Plain FTP
                    HostName = _config.Host,
                    PortNumber = _config.Port,
                    UserName = _config.Username,
                    Password = _config.Password,
                    FtpMode = WinSCP.FtpMode.Passive,  // PASV mode
                    FtpSecure = WinSCP.FtpSecure.None,  // No encryption
                    Timeout = TimeSpan.FromSeconds(30)
                };
                
                using (WinSCP.Session session = new WinSCP.Session())
                {
                    // Enable logging for debugging - use unique log file per upload
                    session.SessionLogPath = Path.Combine(Path.GetTempPath(), $"winscp_log_{Guid.NewGuid()}.txt");
                    
                    Console.WriteLine($"[FTP] Connecting...");
                    session.Open(sessionOptions);
                    Console.WriteLine($"[FTP] ? Connected successfully!");
                    
                    // Build remote path with site/productId structure
                    string remotePath = _config.RemotePath ?? "/";
                    
                    // Add site folder if provided
                    if (!string.IsNullOrEmpty(site))
                    {
                        remotePath = $"{remotePath.TrimEnd('/')}/{site}";
                        
                        // Create site directory if it doesn't exist
                        try
                        {
                            if (!session.FileExists(remotePath))
                            {
                                Console.WriteLine($"[FTP] Creating site directory: {remotePath}");
                                session.CreateDirectory(remotePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[FTP] Warning: Could not create site directory: {ex.Message}");
                        }
                    }
                    
                    // Add productId folder if provided
                    if (!string.IsNullOrEmpty(productId))
                    {
                        remotePath = $"{remotePath}/{productId}";
                        
                        // Create product directory if it doesn't exist
                        try
                        {
                            if (!session.FileExists(remotePath))
                            {
                                Console.WriteLine($"[FTP] Creating product directory: {remotePath}");
                                session.CreateDirectory(remotePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[FTP] Warning: Could not create product directory: {ex.Message}");
                        }
                    }
                    
                    // Save byte array to temporary file - USE UNIQUE FILENAME TO PREVENT CONFLICTS
                    var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");
                    File.WriteAllBytes(tempFile, imageData);
                    
                    try
                    {
                        // Upload
                        var remoteFilePath = $"{remotePath}/{fileName}";
                        
                        Console.WriteLine($"[FTP] Uploading to: {remoteFilePath}");
                        
                        WinSCP.TransferOptions transferOptions = new WinSCP.TransferOptions();
                        transferOptions.TransferMode = WinSCP.TransferMode.Binary;
                        
                        WinSCP.TransferOperationResult transferResult = session.PutFiles(tempFile, remoteFilePath, false, transferOptions);
                        
                        // Check for errors
                        transferResult.Check();
                        
                        Console.WriteLine($"[FTP] ? Upload successful!");
                        
                        var cdnUrl = $"{_config.BaseUrl}{remoteFilePath}";
                        Console.WriteLine($"[FTP] ? CDN URL: {cdnUrl}");
                        Console.WriteLine($"[FTP] ========== UPLOAD COMPLETE ==========\n");
                        
                        return cdnUrl;
                    }
                    finally
                    {
                        // Clean up temp file
                        if (File.Exists(tempFile))
                        {
                            try
                            {
                                File.Delete(tempFile);
                            }
                            catch
                            {
                                // Ignore cleanup errors
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[FTP] ========== ERROR ==========");
                Console.WriteLine($"[FTP] Exception: {ex.GetType().Name}");
                Console.WriteLine($"[FTP] Message: {ex.Message}");
                
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[FTP] Inner: {ex.InnerException.Message}");
                }
                
                Console.WriteLine($"[FTP] See detailed log in temp folder (winscp_log_*.txt)");
                Console.WriteLine($"[FTP] ============================\n");
                
                return null;
            }
        });
    }

    public async Task<List<string>> UploadMultipleImagesAsync(
        List<byte[]> imagesData, 
        string productName,
        string site = "",
        string productId = "",
        Func<int, int, Task>? onProgress = null)
    {
        var uploadedUrls = new List<string>();

        for (int i = 0; i < imagesData.Count; i++)
        {
            var fileName = $"image_{i + 1}.jpg";
            Console.WriteLine($"\n[FTP] ========== Image {i + 1} of {imagesData.Count} ==========");
            
            var url = await UploadImageAsync(imagesData[i], fileName, site, productId);
            
            if (!string.IsNullOrEmpty(url))
            {
                uploadedUrls.Add(url);
                Console.WriteLine($"[FTP] Success count: {uploadedUrls.Count}/{i + 1}");
            }

            if (onProgress != null)
            {
                await onProgress(i + 1, imagesData.Count);
            }
            
            await Task.Delay(100);
        }

        Console.WriteLine($"\n[FTP] ========== BATCH COMPLETE ==========");
        Console.WriteLine($"[FTP] Total: {uploadedUrls.Count}/{imagesData.Count} uploaded");
        Console.WriteLine($"[FTP] ======================================\n");
        
        return uploadedUrls;
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