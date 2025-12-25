using Scrapper.Models;
using System.Text.RegularExpressions;

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
        try
        {
            // Build Scrape.do API URL
            var encodedUrl = System.Net.WebUtility.UrlEncode(url);
            var apiUrl = $"{_config.BaseUrl}?url={encodedUrl}&token={_config.ApiToken}";

            Console.WriteLine($"  Using Scrape.do API...");
            Console.WriteLine($"  API URL: {apiUrl}");
            
            // Make request to Scrape.do
            var response = await _httpClient.GetAsync(apiUrl);
            
            // Log response status
            Console.WriteLine($"  Response Status: {response.StatusCode}");
            
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();
            
            Console.WriteLine($"  ? Retrieved page (Length: {html.Length} chars)");
            
            return html;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"  ? Scrape.do HTTP error: {ex.StatusCode} - {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ? Scrape.do API error: {ex.Message}");
            throw;
        }
    }

    public async Task<List<string>> GetProductLinksAsync(string categoryUrl, int maxProducts = 50, bool isHepsiburada = false)
    {
        var platform = isHepsiburada ? "Hepsiburada" : "Trendyol";
        Console.WriteLine($"\n[{platform}] Starting product discovery (Scrape.do)...");
        Console.WriteLine($"[{platform}] Target: {maxProducts} products");
        Console.Out.Flush();
        
        var productLinks = new List<string>();
        
        // Parse URL to preserve existing query parameters (like sst=BEST_SELLER or q=laptop)
        var uri = new Uri(categoryUrl.StartsWith("http") ? categoryUrl : "https://" + categoryUrl);
        var basePath = uri.GetLeftPart(UriPartial.Path);
        var existingParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
        
        // Check if this is a search URL (/ara for Hepsiburada)
        bool isSearchUrl = isHepsiburada && basePath.Contains("/ara");
        
        // Remove pagination parameter if it exists
        existingParams.Remove(isHepsiburada ? "sayfa" : "pi");
        
        Console.WriteLine($"[{platform}] URL Type: {(isSearchUrl ? "Search" : "Category")}");
        
        // Calculate max pages needed
        int maxPages = Math.Max(30, (maxProducts / 20) + 5);
        int page = 1;
        int emptyPageCount = 0;
        
        while (page <= maxPages && productLinks.Count < maxProducts)
        {
            try
            {
                // Build paginated URL preserving existing parameters
                string paginatedUrl;
                var pageParams = System.Web.HttpUtility.ParseQueryString(existingParams.ToString());
                
                if (page > 1)
                {
                    pageParams[isHepsiburada ? "sayfa" : "pi"] = page.ToString();
                }
                
                // For search URLs, we MUST keep the query params (q=, filtreler=, etc.)
                paginatedUrl = pageParams.Count > 0 ? $"{basePath}?{pageParams}" : basePath;
                
                Console.WriteLine($"[{platform}] Page {page}: {paginatedUrl.Substring(0, Math.Min(80, paginatedUrl.Length))}...");
                Console.Out.Flush();
                
                var html = await GetPageHtmlAsync(paginatedUrl);
                var htmlDoc = new HtmlAgilityPack.HtmlDocument();
                htmlDoc.LoadHtml(html);
                
                int linksBeforePage = productLinks.Count;
                
                // Find all product links
                if (isHepsiburada)
                {
                    // Multiple selectors for Hepsiburada (both category and search pages)
                    var linkNodes = htmlDoc.DocumentNode.SelectNodes("//a[contains(@href, '-p-')]");
                    
                    if (linkNodes != null && linkNodes.Count > 0)
                    {
                        Console.WriteLine($"[{platform}] Raw links on page: {linkNodes.Count}");
                        
                        foreach (var node in linkNodes)
                        {
                            if (productLinks.Count >= maxProducts) break;
                            
                            var href = node.GetAttributeValue("href", "");
                            if (!string.IsNullOrEmpty(href) && href.Contains("-p-"))
                            {
                                // Match both -p- and -pm- patterns
                                if (Regex.IsMatch(href, @"-(pm?)-[A-Z0-9]+", RegexOptions.IgnoreCase))
                                {
                                    var fullUrl = href.StartsWith("http") ? href : "https://www.hepsiburada.com" + href;
                                    var cleanUrl = fullUrl.Split('?')[0];
                                    
                                    if (!productLinks.Contains(cleanUrl))
                                    {
                                        productLinks.Add(cleanUrl);
                                        
                                        // Show LIVE count
                                        Console.Write($"\r[{platform}] Found: {productLinks.Count}/{maxProducts} products   ");
                                        Console.Out.Flush();
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[{platform}] No links with -p- pattern found on page");
                    }
                }
                else
                {
                    // Trendyol pattern
                    var linkNodes = htmlDoc.DocumentNode.SelectNodes("//a[contains(@href, '-p-')]");
                    
                    if (linkNodes != null)
                    {
                        foreach (var node in linkNodes)
                        {
                            if (productLinks.Count >= maxProducts) break;
                            
                            var href = node.GetAttributeValue("href", "");
                            if (!string.IsNullOrEmpty(href) && href.Contains("-p-"))
                            {
                                var fullUrl = href.StartsWith("http") ? href : "https://www.trendyol.com" + href;
                                var cleanUrl = fullUrl.Split('?')[0];
                                
                                if (!productLinks.Contains(cleanUrl))
                                {
                                    productLinks.Add(cleanUrl);
                                    
                                    // Show LIVE count
                                    Console.Write($"\r[{platform}] Found: {productLinks.Count}/{maxProducts} products   ");
                                    Console.Out.Flush();
                                }
                            }
                        }
                    }
                }
                
                Console.WriteLine(); // New line after count
                Console.Out.Flush();
                
                int newLinks = productLinks.Count - linksBeforePage;
                Console.WriteLine($"[{platform}] Page {page} added {newLinks} new products");
                
                // Check if we reached target
                if (productLinks.Count >= maxProducts)
                {
                    Console.WriteLine($"[{platform}] ? Target reached!");
                    Console.Out.Flush();
                    break;
                }
                
                // Check for empty pages
                if (newLinks == 0)
                {
                    emptyPageCount++;
                    if (emptyPageCount >= 2)
                    {
                        Console.WriteLine($"[{platform}] ? End of available products");
                        Console.Out.Flush();
                        break;
                    }
                }
                else
                {
                    emptyPageCount = 0;
                }
                
                page++;
                
                // Delay between API calls to avoid rate limiting
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[{platform}] Error on page {page}: {ex.Message}");
                emptyPageCount++;
                if (emptyPageCount >= 2) break;
                page++;
            }
        }
        
        Console.WriteLine($"\n[{platform}] ? Total: {productLinks.Count} products from {page - 1} pages\n");
        Console.Out.Flush();
        
        // Debug: Print first few links
        if (productLinks.Count > 0)
        {
            Console.WriteLine($"  Sample links:");
            foreach (var link in productLinks.Take(3))
            {
                Console.WriteLine($"    - {link}");
            }
        }

        return productLinks;
    }
}
