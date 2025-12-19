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

    public async Task<List<string>> GetProductLinksAsync(string categoryUrl, bool isHepsiburada = false)
    {
        Console.WriteLine($"Fetching product links using Scrape.do API...");
        
        var html = await GetPageHtmlAsync(categoryUrl);
        var productLinks = new List<string>();

        try
        {
            var htmlDoc = new HtmlAgilityPack.HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Find all product links - different patterns for Trendyol and Hepsiburada
            if (isHepsiburada)
            {
                // Hepsiburada uses -p- pattern (e.g., /apple-ipad-...-p-HBCV0000870EF8)
                var patterns = new[]
                {
                    "//a[contains(@href, '-p-')]"
                };

                foreach (var pattern in patterns)
                {
                    var linkNodes = htmlDoc.DocumentNode.SelectNodes(pattern);
                    
                    if (linkNodes != null && linkNodes.Count > 0)
                    {
                        Console.WriteLine($"  Using pattern: {pattern} (found {linkNodes.Count} nodes)");
                        
                        foreach (var node in linkNodes)
                        {
                            var href = node.GetAttributeValue("href", "");
                            if (!string.IsNullOrEmpty(href) && href.Contains("-p-"))
                            {
                                // Check if it ends with a product code (e.g., -p-HBCV0000870EF8)
                                if (Regex.IsMatch(href, @"-p-[A-Z0-9]+"))
                                {
                                    var fullUrl = href.StartsWith("http") ? href : "https://www.hepsiburada.com" + href;
                                    var cleanUrl = fullUrl.Split('?')[0];
                                    
                                    if (!productLinks.Contains(cleanUrl))
                                    {
                                        productLinks.Add(cleanUrl);
                                    }
                                }
                            }
                        }
                        
                        if (productLinks.Count > 0)
                            break;
                    }
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
                        var href = node.GetAttributeValue("href", "");
                        if (!string.IsNullOrEmpty(href) && href.Contains("-p-"))
                        {
                            var fullUrl = href.StartsWith("http") ? href : "https://www.trendyol.com" + href;
                            var cleanUrl = fullUrl.Split('?')[0];
                            
                            if (!productLinks.Contains(cleanUrl))
                            {
                                productLinks.Add(cleanUrl);
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"Found {productLinks.Count} product links");
            
            // Debug: Print first few links
            if (productLinks.Count > 0)
            {
                Console.WriteLine($"  Sample links:");
                foreach (var link in productLinks.Take(3))
                {
                    Console.WriteLine($"    - {link}");
                }
            }
            else
            {
                Console.WriteLine("  ?? No product links found in HTML!");
                
                // Save HTML for debugging
                var debugFile = $"debug_scrapedo_{(isHepsiburada ? "hepsiburada" : "trendyol")}_{DateTime.Now:yyyyMMdd_HHmmss}.html";
                await File.WriteAllTextAsync(debugFile, html);
                Console.WriteLine($"  ?? Saved HTML to: {debugFile}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing product links: {ex.Message}");
        }

        return productLinks;
    }
}
