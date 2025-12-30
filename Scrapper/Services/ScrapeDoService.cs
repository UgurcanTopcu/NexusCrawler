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
        // Support large scrapes up to 2000 products
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
                
                Console.WriteLine($"[{platform}] Page {page}...");
                
                var html = await GetPageHtmlAsync(paginatedUrl);
                var htmlDoc = new HtmlAgilityPack.HtmlDocument();
                htmlDoc.LoadHtml(html);
                
                int linksBeforePage = productLinks.Count;
                
                var linkNodes = htmlDoc.DocumentNode.SelectNodes("//a[contains(@href, '-p-')]");
                
                // For Trendyol: Get ad product links from widget-container to exclude
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
                    
                    if (adLinks.Count > 0)
                    {
                        Console.WriteLine($"[{platform}] Excluding {adLinks.Count} ad products from widget-container");
                    }
                }
                
                if (linkNodes != null)
                {
                    foreach (var node in linkNodes)
                    {
                        if (productLinks.Count >= maxProducts) break;
                        
                        var href = node.GetAttributeValue("href", "");
                        if (string.IsNullOrEmpty(href) || !href.Contains("-p-")) continue;
                        
                        // Validate URL pattern
                        if (isHepsiburada && !Regex.IsMatch(href, @"-(pm?)-[A-Z0-9]+", RegexOptions.IgnoreCase))
                            continue;
                        
                        var baseUrl = isHepsiburada ? "https://www.hepsiburada.com" : "https://www.trendyol.com";
                        var fullUrl = href.StartsWith("http") ? href : baseUrl + href;
                        var cleanUrl = fullUrl.Split('?')[0];
                        
                        // Skip ad products for Trendyol
                        if (!isHepsiburada && adLinks.Contains(cleanUrl))
                        {
                            continue;
                        }
                        
                        if (!productLinks.Contains(cleanUrl))
                        {
                            productLinks.Add(cleanUrl);
                        }
                    }
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
