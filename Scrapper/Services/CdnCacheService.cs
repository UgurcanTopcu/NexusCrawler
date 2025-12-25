using Scrapper.Models;
using System.Text.RegularExpressions;

namespace Scrapper.Services;

/// <summary>
/// Service to check if images are already uploaded to CDN by fetching folder list via FTP
/// </summary>
public class CdnCacheService
{
    private readonly CdnFtpConfig _config;
    
    // Cache of existing product folders per site
    // Key: site name (e.g., "Gunes", "Orange")
    // Value: HashSet of product IDs that have folders
    private Dictionary<string, HashSet<string>>? _folderCache;
    private bool _cacheInitialized = false;
    
    public CdnCacheService(CdnFtpConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Initialize the cache by fetching all folder names from FTP
    /// Call this once before processing products
    /// </summary>
    public async Task InitializeCacheAsync()
    {
        if (_cacheInitialized)
        {
            Console.WriteLine("[CDN Cache] Cache already initialized, skipping...");
            return;
        }
        
        Console.WriteLine("\n[CDN Cache] ========== INITIALIZING FOLDER CACHE ==========");
        _folderCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        
        await Task.Run(() =>
        {
            try
            {
                WinSCP.SessionOptions sessionOptions = new WinSCP.SessionOptions
                {
                    Protocol = WinSCP.Protocol.Ftp,
                    HostName = _config.Host,
                    PortNumber = _config.Port,
                    UserName = _config.Username,
                    Password = _config.Password,
                    FtpMode = WinSCP.FtpMode.Passive,
                    FtpSecure = WinSCP.FtpSecure.None,
                    Timeout = TimeSpan.FromSeconds(30)
                };
                
                using (WinSCP.Session session = new WinSCP.Session())
                {
                    Console.WriteLine($"[CDN Cache] Connecting to FTP: {_config.Host}:{_config.Port}");
                    session.Open(sessionOptions);
                    Console.WriteLine($"[CDN Cache] ? Connected!");
                    
                    string basePath = _config.RemotePath ?? "/";
                    Console.WriteLine($"[CDN Cache] Base path: {basePath}");
                    
                    // List all site folders (Gunes, Orange, etc.)
                    try
                    {
                        var siteDirectories = session.ListDirectory(basePath);
                        
                        foreach (WinSCP.RemoteFileInfo siteFolder in siteDirectories.Files)
                        {
                            // Skip . and .. and non-directories
                            if (siteFolder.Name == "." || siteFolder.Name == ".." || !siteFolder.IsDirectory)
                                continue;
                            
                            string siteName = siteFolder.Name;
                            string sitePath = $"{basePath.TrimEnd('/')}/{siteName}";
                            
                            Console.WriteLine($"[CDN Cache] Found site folder: {siteName}");
                            
                            // Initialize HashSet for this site
                            _folderCache[siteName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            
                            // List all product folders under this site
                            try
                            {
                                var productDirectories = session.ListDirectory(sitePath);
                                int productCount = 0;
                                
                                foreach (WinSCP.RemoteFileInfo productFolder in productDirectories.Files)
                                {
                                    // Skip . and .. and non-directories
                                    if (productFolder.Name == "." || productFolder.Name == ".." || !productFolder.IsDirectory)
                                        continue;
                            
                                    _folderCache[siteName].Add(productFolder.Name);
                                    productCount++;
                                }
                                
                                Console.WriteLine($"[CDN Cache] ? {siteName}: {productCount} product folders cached");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[CDN Cache] Warning: Could not list products in {siteName}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CDN Cache] Warning: Could not list site directories: {ex.Message}");
                    }
                }
                
                _cacheInitialized = true;
                
                // Summary
                int totalProducts = _folderCache.Values.Sum(h => h.Count);
                Console.WriteLine($"\n[CDN Cache] ========== CACHE READY ==========");
                Console.WriteLine($"[CDN Cache] Sites: {_folderCache.Count}");
                Console.WriteLine($"[CDN Cache] Total cached products: {totalProducts}");
                foreach (var site in _folderCache)
                {
                    Console.WriteLine($"[CDN Cache]   - {site.Key}: {site.Value.Count} products");
                }
                Console.WriteLine($"[CDN Cache] =====================================\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CDN Cache] ? Failed to initialize cache: {ex.Message}");
                Console.WriteLine($"[CDN Cache] Will check images individually as fallback");
                _folderCache = new Dictionary<string, HashSet<string>>();
                _cacheInitialized = true;
            }
        });
    }

    /// <summary>
    /// Check if a product folder exists in the cache (fast lookup)
    /// </summary>
    public bool ProductExistsInCache(string site, string productId)
    {
        if (!_cacheInitialized || _folderCache == null)
        {
            Console.WriteLine("[CDN Cache] Warning: Cache not initialized, returning false");
            return false;
        }
        
        if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(productId))
        {
            return false;
        }
        
        if (_folderCache.TryGetValue(site, out var productIds))
        {
            return productIds.Contains(productId);
        }
        
        return false;
    }

    /// <summary>
    /// Generate expected CDN URL for a product and image index with site/productId structure
    /// </summary>
    public string GenerateCdnUrl(string site, string productId, int imageIndex)
    {
        var fileName = $"image_{imageIndex + 1}.jpg";
        var remotePath = _config.RemotePath ?? "/";
        return $"{_config.BaseUrl}{remotePath.TrimEnd('/')}/{site}/{productId}/{fileName}";
    }

    /// <summary>
    /// Find existing CDN images for a product using cached folder list
    /// Much faster than HTTP HEAD requests for each image
    /// </summary>
    public async Task<(string? mainImage, List<string> additionalImages)> FindExistingImagesAsync(
        string site,
        string productId, 
        int maxImagesToCheck = 3)
    {
        // Ensure cache is initialized
        if (!_cacheInitialized)
        {
            await InitializeCacheAsync();
        }
        
        string? mainImageUrl = null;
        var additionalImages = new List<string>();

        // Skip if site or productId is missing
        if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(productId))
        {
            Console.WriteLine($"[CDN Cache] Skipping - missing site or productId");
            return (null, new List<string>());
        }

        // Fast lookup using cached folder list
        if (ProductExistsInCache(site, productId))
        {
            Console.WriteLine($"[CDN Cache] ? FOUND in cache: {site}/{productId}");
            
            // Product folder exists, generate URLs
            mainImageUrl = GenerateCdnUrl(site, productId, 0);
            
            for (int i = 1; i < maxImagesToCheck; i++)
            {
                additionalImages.Add(GenerateCdnUrl(site, productId, i));
            }
            
            Console.WriteLine($"[CDN Cache] ? Returning {1 + additionalImages.Count} cached image URLs");
        }
        else
        {
            Console.WriteLine($"[CDN Cache] ? Not in cache: {site}/{productId} - will upload");
        }

        return (mainImageUrl, additionalImages);
    }

    /// <summary>
    /// Add a product to the cache after successful upload
    /// </summary>
    public void AddToCache(string site, string productId)
    {
        if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(productId))
            return;
            
        if (_folderCache == null)
            _folderCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        
        if (!_folderCache.ContainsKey(site))
            _folderCache[site] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        _folderCache[site].Add(productId);
        Console.WriteLine($"[CDN Cache] Added to cache: {site}/{productId}");
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    public (int siteCount, int totalProducts) GetCacheStats()
    {
        if (_folderCache == null)
            return (0, 0);
            
        return (_folderCache.Count, _folderCache.Values.Sum(h => h.Count));
    }
}
