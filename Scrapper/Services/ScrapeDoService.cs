using Scrapper.Models;
using System.Text.RegularExpressions;
using System.Web;

namespace Scrapper.Services;

public class ScrapeDoService
{
    private readonly HttpClient _httpClient;
    private readonly ScrapeDoConfig _config;

    public ScrapeDoService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _config = new ScrapeDoConfig();
    }

    public async Task<string> GetPageHtmlAsync(string url)
    {
        var encodedUrl = System.Net.WebUtility.UrlEncode(url);
        var apiUrl = $"{_config.BaseUrl}?url={encodedUrl}&token={_config.ApiToken}";
        
        var response = await _httpClient.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<List<string>> GetProductLinksAsync(string categoryUrl, int maxProducts = 50, bool isHepsiburada = false)
    {
        var platform = isHepsiburada ? "Hepsiburada" : "Trendyol";
        Console.WriteLine($"\n[{platform}] Starting product discovery (Scrape.do)...");
        Console.WriteLine($"[{platform}] Target: {maxProducts} products");
        
        var productLinks = new List<string>();
        
        var uri = new Uri(categoryUrl.StartsWith("http") ? categoryUrl : "https://" + categoryUrl);
        var basePath = uri.GetLeftPart(UriPartial.Path);
        var originalQuery = uri.Query;
        
        string pageParam = isHepsiburada ? "sayfa" : "pi";
        int maxPages = Math.Max(150, (maxProducts / 24) + 15);
        int page = 1;
        int emptyPageCount = 0;
        
        while (page <= maxPages && productLinks.Count < maxProducts)
        {
            try
            {
                string paginatedUrl;
                
                if (page == 1)
                {
                    paginatedUrl = categoryUrl.StartsWith("http") ? categoryUrl : "https://" + categoryUrl;
                }
                else
                {
                    if (string.IsNullOrEmpty(originalQuery))
                    {
                        paginatedUrl = $"{basePath}?{pageParam}={page}";
                    }
                    else
                    {
                        var queryParts = originalQuery.TrimStart('?').Split('&')
                            .Where(p => !p.StartsWith($"{pageParam}=", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        queryParts.Add($"{pageParam}={page}");
                        paginatedUrl = $"{basePath}?{string.Join("&", queryParts)}";
                    }
                }
                
                Console.WriteLine($"[{platform}] Page {page}: {paginatedUrl}");
                
                var html = await GetPageHtmlAsync(paginatedUrl);
                var htmlDoc = new HtmlAgilityPack.HtmlDocument();
                htmlDoc.LoadHtml(html);
                
                Console.WriteLine($"[{platform}] HTML length: {html.Length} chars");
                
                int linksBeforePage = productLinks.Count;
                
                // For Trendyol: Get ad product links to exclude
                var adLinks = new HashSet<string>();
                if (!isHepsiburada)
                {
                    var adNodes = htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'widget-container')]//a[contains(@href, '-p-')]");
                    if (adNodes != null)
                    {
                        foreach (var adNode in adNodes)
                        {
                            var adHref = adNode.GetAttributeValue("href", "");
                            if (!string.IsNullOrEmpty(adHref))
                            {
                                var adCleanUrl = adHref.Split('?')[0];
                                if (adCleanUrl.StartsWith("/")) adCleanUrl = "https://www.trendyol.com" + adCleanUrl;
                                adLinks.Add(adCleanUrl);
                            }
                        }
                    }
                }
                
                // Try multiple patterns for Hepsiburada
                HtmlAgilityPack.HtmlNodeCollection? linkNodes = null;
                
                if (isHepsiburada)
                {
                    // Get ALL links, we'll filter them properly
                    linkNodes = htmlDoc.DocumentNode.SelectNodes("//a[@href]");
                    Console.WriteLine($"[{platform}] Total <a> tags: {linkNodes?.Count ?? 0}");
                }
                else
                {
                    // Trendyol pattern
                    linkNodes = htmlDoc.DocumentNode.SelectNodes("//a[contains(@href, '-p-')]");
                }
                
                if (linkNodes != null && linkNodes.Count > 0)
                {
                    Console.WriteLine($"[{platform}] Processing {linkNodes.Count} links...");
                    
                    int candidateCount = 0;
                    int rejectedCategory = 0;
                    int rejectedNoPattern = 0;
                    int fromAdRedirects = 0;
                    
                    foreach (var node in linkNodes)
                    {
                        if (productLinks.Count >= maxProducts) break;
                        
                        var href = node.GetAttributeValue("href", "");
                        if (string.IsNullOrEmpty(href)) continue;
                        
                        var baseUrl = isHepsiburada ? "https://www.hepsiburada.com" : "https://www.trendyol.com";
                        var fullUrl = href.StartsWith("http") ? href : baseUrl + href;
                        var cleanUrl = fullUrl;
                        
                        // SPECIAL: Handle adservice redirect links (sponsored products)
                        if (isHepsiburada && fullUrl.Contains("adservice.hepsiburada.com") && fullUrl.Contains("redirect="))
                        {
                            try
                            {
                                // IMPORTANT: HTML attributes may have &amp; instead of &
                                // Decode HTML entities first
                                var decodedHref = System.Net.WebUtility.HtmlDecode(fullUrl);
                                
                                Console.WriteLine($"[{platform}] Found adservice link, decoding...");
                                
                                // Extract redirect parameter using Uri and query parsing
                                var adUri = new Uri(decodedHref);
                                var queryParams = System.Web.HttpUtility.ParseQueryString(adUri.Query);
                                var redirectParam = queryParams.Get("redirect");
                                
                                if (!string.IsNullOrEmpty(redirectParam))
                                {
                                    // The redirect param is already URL-decoded by ParseQueryString
                                    // But we need to clean it - remove query params from the product URL itself
                                    cleanUrl = redirectParam.Split('?')[0].Split('#')[0];
                                    
                                    Console.WriteLine($"[{platform}] Ad redirect extracted: {cleanUrl}");
                                    
                                    // Check if it's a valid product URL
                                    if (cleanUrl.Contains("-p-") && !cleanUrl.Contains("-c-") && !productLinks.Contains(cleanUrl))
                                    {
                                        productLinks.Add(cleanUrl);
                                        candidateCount++;
                                        fromAdRedirects++;
                                        
                                        if (productLinks.Count <= 10)
                                        {
                                            Console.WriteLine($"[{platform}] #{productLinks.Count}: {cleanUrl} (from ad)");
                                        }
                                    }
                                    continue;
                                }
                                else
                                {
                                    Console.WriteLine($"[{platform}] No redirect param found in: {decodedHref.Substring(0, Math.Min(100, decodedHref.Length))}...");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[{platform}] Error extracting ad redirect: {ex.Message}");
                            }
                        }
                        
                        // Skip other adservice/tracking links
                        if (cleanUrl.Contains("adservice") || 
                            cleanUrl.Contains("/track") || 
                            cleanUrl.Contains("/event") ||
                            cleanUrl.Contains("banner") ||
                            cleanUrl.Contains("campaign") ||
                            cleanUrl.Contains("widget") ||
                            cleanUrl.Contains("/hesabim") ||
                            cleanUrl.Contains("/siparislerim") ||
                            cleanUrl.Contains("/favorilerim") ||
                            cleanUrl.Contains("/magaza/") ||
                            cleanUrl.EndsWith("hepsiburada.com/") ||
                            cleanUrl == "https://www.hepsiburada.com")
                        {
                            continue;
                        }
                        
                        // Clean URL for regular links
                        cleanUrl = fullUrl.Split('?')[0].Split('#')[0];
                        
                        // Validate product URL
                        bool isValidProduct = false;
                        
                        if (isHepsiburada)
                        {
                            // Must be from hepsiburada.com
                            if (!cleanUrl.Contains("hepsiburada.com")) continue;
                            
                            var pathPart = cleanUrl.Split(new[] { "hepsiburada.com" }, StringSplitOptions.None);
                            if (pathPart.Length < 2) continue;
                            var path = pathPart[1];
                            
                            // CRITICAL: Skip category pages (contain -c- in path)
                            if (path.Contains("-c-"))
                            {
                                rejectedCategory++;
                                continue; // This is a category, not a product
                            }
                            
                            // Pattern 1: Contains -p- (most common)
                            if (path.Contains("-p-"))
                            {
                                isValidProduct = true;
                            }
                            // Pattern 2: Contains /p-
                            else if (path.Contains("/p-"))
                            {
                                isValidProduct = true;
                            }
                            // Pattern 3: Ends with HB product code
                            else if (Regex.IsMatch(path, @"/[a-z0-9-]+-HB[A-Z0-9]{10,}$", RegexOptions.IgnoreCase))
                            {
                                isValidProduct = true;
                            }
                            // Pattern 4: Long product-looking path with alphanumeric code at end
                            // BUT MUST NOT contain -c- (category indicator)
                            else if (!path.Contains("filtreler") &&
                                     !path.EndsWith("/") &&
                                     path.Split('/').Length == 2 && 
                                     path.Length > 20 &&
                                     Regex.IsMatch(path, @"[A-Z0-9]{8,}$", RegexOptions.IgnoreCase))
                            {
                                isValidProduct = true;
                            }
                            else
                            {
                                rejectedNoPattern++;
                                // Log first few rejections
                                if (rejectedNoPattern <= 5)
                                {
                                    Console.WriteLine($"[{platform}] REJECTED (no pattern): {cleanUrl}");
                                }
                            }
                        }
                        else
                        {
                            // Trendyol validation
                            isValidProduct = cleanUrl.Contains("-p-") && !adLinks.Contains(cleanUrl);
                        }
                        
                        if (isValidProduct && !productLinks.Contains(cleanUrl))
                        {
                            productLinks.Add(cleanUrl);
                            candidateCount++;
                            
                            if (productLinks.Count <= 10)
                            {
                                Console.WriteLine($"[{platform}] #{productLinks.Count}: {cleanUrl}");
                            }
                        }
                    }
                    
                    var adInfo = fromAdRedirects > 0 ? $" (including {fromAdRedirects} from ads)" : "";
                    Console.WriteLine($"[{platform}] Found {candidateCount} new product links{adInfo} from {linkNodes.Count} total links");
                    Console.WriteLine($"[{platform}] Rejected: Category={rejectedCategory}, NoPattern={rejectedNoPattern}");
                }
                else
                {
                    Console.WriteLine($"[{platform}] ? No links found in HTML!");
                }
                
                int newLinks = productLinks.Count - linksBeforePage;
                Console.WriteLine($"[{platform}] Page {page}: +{newLinks} new | Total: {productLinks.Count}/{maxProducts}");
                
                if (productLinks.Count >= maxProducts)
                {
                    Console.WriteLine($"[{platform}] ? Target reached!");
                    break;
                }
                
                if (newLinks == 0)
                {
                    emptyPageCount++;
                    if (emptyPageCount >= 2)
                    {
                        Console.WriteLine($"[{platform}] ? End of available products");
                        break;
                    }
                }
                else
                {
                    emptyPageCount = 0;
                }
                
                page++;
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{platform}] Error on page {page}: {ex.Message}");
                emptyPageCount++;
                if (emptyPageCount >= 2) break;
                page++;
            }
        }
        
        Console.WriteLine($"\n[{platform}] ? Total: {productLinks.Count} products from {page - 1} pages\n");

        return productLinks;
    }
}
