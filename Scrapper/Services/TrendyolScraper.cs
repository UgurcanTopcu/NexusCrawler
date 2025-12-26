using HtmlAgilityPack;
using Scrapper.Models;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace Scrapper.Services;

public class TrendyolScraper : IDisposable
{
    private readonly HttpClient _httpClient;
    private IWebDriver? _driver;
    private readonly ScrapeDoService? _scrapeDoService;
    private const string BaseUrl = "https://www.trendyol.com";
    public ScrapeMethod Method { get; set; } = ScrapeMethod.Selenium;

    public TrendyolScraper()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _scrapeDoService = new ScrapeDoService(_httpClient);
    }

    private void InitializeDriver()
    {
        if (_driver == null)
        {
            var options = new ChromeOptions();
            options.AddArgument("--headless=new");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--window-size=1920,1080");
            options.AddArgument("--disable-blink-features=AutomationControlled");
            options.AddArgument("user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            options.AddExcludedArgument("enable-automation");
            options.AddAdditionalOption("useAutomationExtension", false);
            
            _driver = new ChromeDriver(options);
            ((IJavaScriptExecutor)_driver).ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");
        }
    }

    public async Task<List<string>> GetProductLinksAsync(string categoryUrl, int maxProducts = 50, Func<int, string, string, Task>? onProgress = null)
    {
        if (Method == ScrapeMethod.ScrapeDo)
        {
            return await _scrapeDoService!.GetProductLinksAsync(categoryUrl, maxProducts, isHepsiburada: false);
        }

        var productLinks = new List<string>();

        try
        {
            InitializeDriver();
            
            // Parse URL to preserve existing query parameters (like sst=BEST_SELLER)
            var uri = new Uri(categoryUrl.StartsWith("http") ? categoryUrl : "https://" + categoryUrl);
            var basePath = uri.GetLeftPart(UriPartial.Path);
            var existingParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
            
            // Remove pi parameter if it exists in the original URL
            existingParams.Remove("pi");
            
            Console.WriteLine("\n[Trendyol] Starting product discovery...");
            Console.WriteLine($"[Trendyol] Target: {maxProducts} products");
            Console.Out.Flush();
            
            if (onProgress != null)
            {
                await onProgress(5, $"🔍 Finding products (target: {maxProducts})...", "info");
            }
            
            int page = 1;
            // Calculate max pages needed: ~24 products per page, add buffer for 500 products
            int maxPages = Math.Max(30, (maxProducts / 20) + 5);
            int previousCount = 0;
            int emptyPageCount = 0;
            
            while (page <= maxPages && productLinks.Count < maxProducts)
            {
                // Build paginated URL preserving existing parameters
                string paginatedUrl;
                if (page == 1 && existingParams.Count == 0)
                {
                    paginatedUrl = basePath;
                }
                else
                {
                    // Clone params and add/update pi
                    var pageParams = System.Web.HttpUtility.ParseQueryString(existingParams.ToString());
                    if (page > 1)
                    {
                        pageParams["pi"] = page.ToString();
                    }
                    paginatedUrl = pageParams.Count > 0 ? $"{basePath}?{pageParams}" : basePath;
                }
                
                Console.WriteLine($"[Trendyol] Page {page}...");
                Console.Out.Flush();
                
                _driver!.Navigate().GoToUrl(paginatedUrl);
                
                // Wait for products to load
                var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));
                try
                {
                    wait.Until(d => d.FindElements(By.CssSelector("a[href*='-p-']")).Count > 0);
                }
                catch
                {
                    emptyPageCount++;
                    if (emptyPageCount >= 2)
                    {
                        Console.WriteLine($"[Trendyol] No more products available");
                        Console.Out.Flush();
                        break;
                    }
                    page++;
                    continue;
                }
                
                await Task.Delay(500); // Let page stabilize
                
                // Scroll down on this page to load all lazy-loaded products
                var jsExecutor = (IJavaScriptExecutor)_driver;
                for (int scroll = 0; scroll < 3; scroll++)
                {
                    jsExecutor.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                    await Task.Delay(300);
                }
                
                // Extract links from current page
                var linkElements = _driver.FindElements(By.CssSelector("a[href*='-p-']"));
                int newLinksOnPage = 0;
                int lastReportedCount = productLinks.Count;
                
                // Get all widget-container elements (ad products) to exclude
                var adContainers = _driver.FindElements(By.CssSelector(".widget-container"));
                var adLinks = new HashSet<string>();
                foreach (var container in adContainers)
                {
                    try
                    {
                        var containerLinks = container.FindElements(By.CssSelector("a[href*='-p-']"));
                        foreach (var adLink in containerLinks)
                        {
                            var href = adLink.GetAttribute("href");
                            if (!string.IsNullOrEmpty(href))
                            {
                                var cleanUrl = href.Split('?')[0];
                                adLinks.Add(cleanUrl);
                            }
                        }
                    }
                    catch { }
                }
                
                if (adLinks.Count > 0)
                {
                    Console.WriteLine($"[Trendyol] Excluding {adLinks.Count} ad products from widget-container");
                }
                
                foreach (var element in linkElements)
                {
                    // Stop if we already have enough products
                    if (productLinks.Count >= maxProducts)
                    {
                        break;
                    }
                    
                    try
                    {
                        var href = element.GetAttribute("href");
                        if (!string.IsNullOrEmpty(href) && href.Contains("-p-"))
                        {
                            var fullUrl = href.StartsWith("http") ? href : BaseUrl + href;
                            var cleanUrl = fullUrl.Split('?')[0];
                            
                            // Skip if this is an ad product
                            if (adLinks.Contains(cleanUrl))
                            {
                                continue;
                            }
                            
                            if (!productLinks.Contains(cleanUrl))
                            {
                                productLinks.Add(cleanUrl);
                                newLinksOnPage++;
                                
                                // Show LIVE count on console
                                Console.Write($"\r[Trendyol] Found: {productLinks.Count}/{maxProducts} products   ");
                                Console.Out.Flush();
                                
                                // Send to UI every 5 products or at target
                                if (onProgress != null && (productLinks.Count % 5 == 0 || productLinks.Count == maxProducts))
                                {
                                    var progressPercent = Math.Min(5 + (int)((productLinks.Count / (double)maxProducts) * 5), 10);
                                    await onProgress(progressPercent, $"📦 Found {productLinks.Count}/{maxProducts} products", "info");
                                }
                            }
                        }
                    }
                    catch { }
                }
                
                Console.WriteLine(); // New line after the count
                Console.Out.Flush();
                
                // Check if we reached the target
                if (productLinks.Count >= maxProducts)
                {
                    Console.WriteLine($"[Trendyol] ✓ Target reached!");
                    Console.Out.Flush();
                    if (onProgress != null)
                    {
                        await onProgress(10, $"✅ Found all {productLinks.Count} product URLs!", "success");
                    }
                    break;
                }
                
                // Check if we got new products
                if (productLinks.Count == previousCount)
                {
                    emptyPageCount++;
                    if (emptyPageCount >= 2)
                    {
                        Console.WriteLine($"[Trendyol] ✓ End of available products");
                        Console.Out.Flush();
                        if (onProgress != null && productLinks.Count > 0)
                        {
                            await onProgress(10, $"✅ Found {productLinks.Count} products (all available)", "success");
                        }
                        break;
                    }
                }
                else
                {
                    emptyPageCount = 0; // Reset counter if we found products
                    previousCount = productLinks.Count;
                }
                
                page++;
                
                // Small delay between page loads
                await Task.Delay(300);
            }
            
            Console.WriteLine($"\n[Trendyol] ✓ Total: {productLinks.Count} products from {page - 1} pages\n");
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching product links: {ex.Message}");
            if (onProgress != null)
            {
                await onProgress(10, $"❌ Error finding products: {ex.Message}", "error");
            }
        }

        return productLinks;
    }

    public async Task<ProductInfo?> GetProductDetailsAsync(string productUrl)
    {
        try
        {
            string html;
            HtmlDocument htmlDoc;

            if (Method == ScrapeMethod.ScrapeDo)
            {
                html = await _scrapeDoService!.GetPageHtmlAsync(productUrl);
                htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);
                await Task.Delay(300);
            }
            else
            {
                InitializeDriver();
                _driver!.Navigate().GoToUrl(productUrl);
                
                var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(8));
                
                try
                {
                    wait.Until(d => d.FindElements(By.CssSelector(".attribute-item")).Count > 0);
                }
                catch { }
                
                await Task.Delay(700);

                html = _driver.PageSource;
                htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);
            }

            var product = new ProductInfo 
            { 
                ProductUrl = productUrl,
                Source = "Gunes"
            };
            
            // EXTRACT PRODUCT ID from URL
            try
            {
                var match = Regex.Match(productUrl, @"-p-(\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    product.ProductId = match.Groups[1].Value;
                }
            }
            catch { }
            
            // EXTRACT PRODUCT NAME & BRAND
            try
            {
                var h1Node = htmlDoc.DocumentNode.SelectSingleNode("//h1[contains(@class, 'pr-new-br') or contains(@class, 'product-title')]");

                if (h1Node != null)
                {
                    product.Name = h1Node.InnerText.Trim();
                    
                    var brandStrong = h1Node.SelectSingleNode(".//strong");
                    if (brandStrong != null)
                    {
                        product.Brand = brandStrong.InnerText.Trim();
                    }
                }
                
                if (string.IsNullOrEmpty(product.Name))
                {
                    var node = htmlDoc.DocumentNode.SelectSingleNode("//h1");
                    if (node != null && !string.IsNullOrWhiteSpace(node.InnerText))
                    {
                        product.Name = node.InnerText.Trim();
                    }
                }
            }
            catch { }

            // EXTRACT PRICES
            try
            {
                if (Method == ScrapeMethod.Selenium && _driver != null)
                {
                    try
                    {
                        var jsExecutor = (IJavaScriptExecutor)_driver!;
                        var cartPrice = jsExecutor.ExecuteScript(@"
                            var priceBox = document.querySelector('[class*=""prc-box""]');
                            if (priceBox) {
                                var discounted = priceBox.querySelector('[class*=""prc-dsc""]');
                                var single = priceBox.querySelector('[class*=""prc-slg""]');
                                if (discounted) return discounted.textContent.trim();
                                if (single) return single.textContent.trim();
                            }
                            return '';
                        ");
                        
                        if (cartPrice != null && !string.IsNullOrWhiteSpace(cartPrice.ToString()))
                        {
                            product.DiscountedPrice = CleanPrice(cartPrice.ToString()!);
                        }
                    }
                    catch { }
                }
                
                if (string.IsNullOrEmpty(product.DiscountedPrice))
                {
                    var priceBox = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'prc-box')]");
                    if (priceBox != null)
                    {
                        var originalPrice = priceBox.SelectSingleNode(".//span[contains(@class, 'prc-org')]");
                        if (originalPrice != null)
                        {
                            product.Price = CleanPrice(originalPrice.InnerText);
                        }
                        
                        var discountedPrice = priceBox.SelectSingleNode(".//span[contains(@class, 'prc-dsc')]") 
                            ?? priceBox.SelectSingleNode(".//span[contains(@class, 'prc-slg')");
                        if (discountedPrice != null)
                        {
                            product.DiscountedPrice = CleanPrice(discountedPrice.InnerText);
                        }
                    }
                }
            }
            catch { }

            // EXTRACT SELLER
            try
            {
                var sellerNode = htmlDoc.DocumentNode.SelectSingleNode("//a[contains(@class, 'merchant')]");
                if (sellerNode != null && !string.IsNullOrWhiteSpace(sellerNode.InnerText))
                {
                    product.Seller = sellerNode.InnerText.Trim();
                }
            }
            catch { }

            // EXTRACT IMAGE
            try
            {
                var allImages = new List<string>();
                
                if (Method == ScrapeMethod.Selenium && _driver != null)
                {
                    try
                    {
                        var jsExecutor = (IJavaScriptExecutor)_driver!;
                        var imageUrls = jsExecutor.ExecuteScript(@"
                            var images = [];
                            document.querySelectorAll('[class*=""gallery""] img, img[src*=""productimages""]').forEach(img => {
                                var alt = img.alt || '';
                                if (alt.toLowerCase().includes('product stamp')) return;
                                var src = img.src || img.getAttribute('data-src') || '';
                                if (src && !images.includes(src)) images.push(src);
                            });
                            return images.join('|||');
                        ");
                        
                        if (imageUrls != null && !string.IsNullOrWhiteSpace(imageUrls.ToString()))
                        {
                            foreach (var url in imageUrls.ToString()!.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var imgUrl = url.Trim();
                                if (imgUrl.StartsWith("//")) imgUrl = "https:" + imgUrl;
                                else if (!imgUrl.StartsWith("http")) imgUrl = BaseUrl + imgUrl;
                                
                                if (imgUrl.Contains("automation", StringComparison.OrdinalIgnoreCase) || 
                                    imgUrl.Contains("marketing", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                
                                if (!allImages.Contains(imgUrl))
                                    allImages.Add(imgUrl);
                            }
                        }
                    }
                    catch { }
                }
                
                if (allImages.Count == 0)
                {
                    var imgNodes = htmlDoc.DocumentNode.SelectNodes("//img[contains(@src, 'productimages')]");
                    if (imgNodes != null)
                    {
                        foreach (var node in imgNodes)
                        {
                            var altText = node.GetAttributeValue("alt", "");
                            if (altText.Contains("Product stamp", StringComparison.OrdinalIgnoreCase))
                                continue;
                            
                            var imgUrl = node.GetAttributeValue("src", "") ?? node.GetAttributeValue("data-src", "");
                            if (!string.IsNullOrEmpty(imgUrl))
                            {
                                if (imgUrl.StartsWith("//")) imgUrl = "https:" + imgUrl;
                                else if (!imgUrl.StartsWith("http")) imgUrl = BaseUrl + imgUrl;
                                
                                if (!imgUrl.Contains("automation") && !imgUrl.Contains("marketing") && !allImages.Contains(imgUrl))
                                    allImages.Add(imgUrl);
                            }
                        }
                    }
                }
                
                if (allImages.Count > 0)
                {
                    product.ImageUrl = allImages[0];
                    for (int i = 1; i < allImages.Count; i++)
                    {
                        product.AdditionalImages.Add(allImages[i]);
                    }
                }
            }
            catch { }

            // EXTRACT CATEGORY
            try
            {
                var breadcrumbNodes = htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'breadcrumb')]//a");
                if (breadcrumbNodes != null && breadcrumbNodes.Count > 0)
                {
                    var categories = breadcrumbNodes
                        .Select(n => n.InnerText.Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    
                    if (categories.Count > 0)
                    {
                        product.Category = string.Join(" > ", categories);
                    }
                }
            }
            catch { }

            // EXTRACT DESCRIPTION
            try
            {
                string detailedDescription = "";
                
                if (Method == ScrapeMethod.Selenium && _driver != null)
                {
                    try
                    {
                        var jsExecutor = (IJavaScriptExecutor)_driver!;
                        
                        // Try to click the description tab
                        try
                        {
                            var descriptionTab = _driver.FindElement(By.XPath("//a[contains(text(), 'ÜRÜN BİLGİLERİ')]"));
                            descriptionTab.Click();
                            await Task.Delay(500);
                        }
                        catch { }
                        
                        var descriptionText = jsExecutor.ExecuteScript(@"
                            var container = document.querySelector('.content-description-container');
                            var textParts = [];
                            if (container) {
                                container.querySelectorAll('p.product-description-content').forEach(function(p) {
                                    var text = p.textContent.trim();
                                    if (text && text.length > 5 && !text.startsWith('...')) {
                                        textParts.push('- ' + text);
                                    }
                                });
                            }
                            return textParts.join('\n');
                        ");
                        
                        if (descriptionText != null && !string.IsNullOrWhiteSpace(descriptionText.ToString()))
                        {
                            detailedDescription = descriptionText.ToString()!.Trim();
                        }
                    }
                    catch { }
                }
                
                // Fallback: HTML parsing
                if (string.IsNullOrEmpty(detailedDescription))
                {
                    var descParagraphs = htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'content-description-container')]//p[contains(@class, 'product-description-content')]" );
                    
                    if (descParagraphs != null && descParagraphs.Count > 0)
                    {
                        var descLines = descParagraphs
                            .Select(p => p.InnerText.Trim())
                            .Where(text => !string.IsNullOrWhiteSpace(text) && text.Length > 5 && !text.StartsWith("..."))
                            .Select(text => "- " + text)
                            .ToList();
                        
                        if (descLines.Count > 0)
                        {
                            detailedDescription = string.Join("\n", descLines);
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(detailedDescription))
                {
                    if (detailedDescription.Length > 5000)
                        detailedDescription = detailedDescription.Substring(0, 5000) + "...";
                    product.Description = detailedDescription;
                }
            }
            catch { }

            // EXTRACT BARCODE
            try
            {
                var scriptNodes = htmlDoc.DocumentNode.SelectNodes("//script[contains(text(), 'barcode')]");
                if (scriptNodes != null)
                {
                    foreach (var scriptNode in scriptNodes)
                    {
                        var jsonMatch = Regex.Match(scriptNode.InnerText, @"""(?:barcode|barkod)"":\s*""(\d{8,})""", RegexOptions.IgnoreCase);
                        if (jsonMatch.Success)
                        {
                            product.Barcode = jsonMatch.Groups[1].Value;
                            break;
                        }
                    }
                }
            }
            catch { }

            // EXTRACT PRODUCT ATTRIBUTES
            await ExtractProductAttributes(htmlDoc, product);

            return product;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error scraping product: {ex.Message}");
        }

        return null;
    }

    private async Task ExtractProductAttributes(HtmlDocument htmlDoc, ProductInfo product)
    {
        var attributeItems = htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'attribute-item')]");
        
        if (attributeItems != null && attributeItems.Count > 0)
        {
            foreach (var item in attributeItems)
            {
                try
                {
                    var nameDiv = item.SelectSingleNode(".//div[contains(@class, 'name')]");
                    var valueDiv = item.SelectSingleNode(".//div[contains(@class, 'value')]");
                    
                    if (nameDiv != null && valueDiv != null)
                    {
                        var key = nameDiv.InnerText.Trim();
                        var value = valueDiv.InnerText.Trim();
                        
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                        {
                            product.Attributes[key] = value;
                        }
                    }
                }
                catch { }
            }
        }
        else if (Method == ScrapeMethod.Selenium && _driver != null)
        {
            // JS fallback only if HTML parsing fails
            try
            {
                var jsExecutor = (IJavaScriptExecutor)_driver;
                var jsData = jsExecutor.ExecuteScript(@"
                    var attrs = [];
                    document.querySelectorAll('.attribute-item').forEach(item => {
                        var name = item.querySelector('.name');
                        var value = item.querySelector('.value');
                        if (name && value) {
                            attrs.push({ key: name.textContent.trim(), value: value.textContent.trim() });
                        }
                    });
                    return JSON.stringify(attrs);
                ");
                
                if (jsData != null)
                {
                    using var doc = JsonDocument.Parse(jsData.ToString()!);
                    foreach (var attr in doc.RootElement.EnumerateArray())
                    {
                        if (attr.TryGetProperty("key", out var keyElem) && 
                            attr.TryGetProperty("value", out var valueElem))
                        {
                            var key = keyElem.GetString();
                            var value = valueElem.GetString();
                            
                            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                            {
                                product.Attributes[key] = value;
                            }
                        }
                    }
                }
            }
            catch { }
        }
        
        await Task.CompletedTask;
    }

    private string CleanPrice(string priceText)
    {
        if (string.IsNullOrWhiteSpace(priceText))
            return "";
        
        var match = Regex.Match(priceText, @"([\d.,]+)");
        if (match.Success)
        {
            var numericValue = match.Value;
            if (priceText.Contains("TL") || priceText.Contains("₺"))
                return numericValue + " TL";
            return numericValue + " TL";
        }
        
        return priceText.Trim();
    }

    public async Task<List<ProductInfo>> ScrapeAllProductsAsync(string categoryUrl, int maxProducts = 50)
    {
        var products = new List<ProductInfo>();
        
        var productLinks = await GetProductLinksAsync(categoryUrl);
        
        var linksToProcess = productLinks.Take(maxProducts).ToList();
        Console.WriteLine($"\nProcessing {linksToProcess.Count} products...\n");

        foreach (var link in linksToProcess)
        {
            var product = await GetProductDetailsAsync(link);
            if (product != null)
            {
                products.Add(product);
            }
            
            await Task.Delay(300); // Reduced from 500ms
        }

        return products;
    }

    public void Dispose()
    {
        _driver?.Quit();
        _driver?.Dispose();
    }
}
