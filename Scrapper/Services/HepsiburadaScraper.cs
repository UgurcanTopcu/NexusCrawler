using HtmlAgilityPack;
using Scrapper.Models;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace Scrapper.Services;

public class HepsiburadaScraper : IDisposable
{
    private readonly HttpClient _httpClient;
    private IWebDriver? _driver;
    private readonly ScrapeDoService? _scrapeDoService;
    private const string BaseUrl = "https://www.hepsiburada.com";
    public ScrapeMethod Method { get; set; } = ScrapeMethod.Selenium;

    public HepsiburadaScraper()
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

    public async Task<List<string>> GetProductLinksAsync(string categoryUrl)
    {
        if (Method == ScrapeMethod.ScrapeDo)
        {
            return await _scrapeDoService!.GetProductLinksAsync(categoryUrl, isHepsiburada: true);
        }

        var productLinks = new List<string>();

        try
        {
            InitializeDriver();
            _driver!.Navigate().GoToUrl(categoryUrl);
            
            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
            
            try
            {
                wait.Until(d => d.FindElements(By.CssSelector("a[href*='-p-']")).Count > 0);
            }
            catch { }
            
            // Optimized scrolling with reduced delays
            for (int i = 0; i < 5; i++)
            {
                ((IJavaScriptExecutor)_driver).ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                await Task.Delay(500);
            }
            
            await Task.Delay(1000);

            var allLinks = _driver.FindElements(By.TagName("a"));
            
            foreach (var link in allLinks)
            {
                try
                {
                    var href = link.GetAttribute("href");
                    if (!string.IsNullOrEmpty(href) && href.Contains("-p-") && href.Contains("hepsiburada.com"))
                    {
                        // Skip "Sana özel seçimler" (personalized recommendations)
                        // These are in a banner section, not real search results
                        try
                        {
                            var jsExecutor = (IJavaScriptExecutor)_driver;
                            var isInBanner = jsExecutor.ExecuteScript(@"
                                var el = arguments[0];
                                while (el) {
                                    var className = el.className || '';
                                    var id = el.id || '';
                                    // Check if in ProductsBanner or recommendation section
                                    if (className.includes('ProductsBanner') || 
                                        className.includes('productsBanner') ||
                                        id.includes('ProductsBanner') ||
                                        className.includes('seçimler') ||
                                        className.includes('özel')) {
                                        return true;
                                    }
                                    el = el.parentElement;
                                }
                                return false;
                            ", link);
                            
                            if (isInBanner != null && (bool)isInBanner)
                            {
                                continue; // Skip this link
                            }
                        }
                        catch { }
                        
                        var cleanUrl = href.Split('?')[0];
                        
                        if (Regex.IsMatch(cleanUrl, @"-p-[A-Z0-9]+$"))
                        {
                            if (!productLinks.Contains(cleanUrl))
                            {
                                productLinks.Add(cleanUrl);
                                
                                // Performance optimization: Stop if we have enough links
                                // Add buffer for potential failed scrapes
                                // This avoids processing hundreds of links when only 5 are needed
                                if (productLinks.Count >= 100)
                                {
                                    Console.WriteLine($"Collected sufficient product links ({productLinks.Count}), stopping early...");
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            
            Console.WriteLine($"Found {productLinks.Count} product links");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching product links: {ex.Message}");
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
                    wait.Until(d => d.FindElements(By.CssSelector("h1, .product-name")).Count > 0);
                }
                catch { }
                
                await Task.Delay(700);

                html = _driver.PageSource;
                htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);
            }

            var product = new ProductInfo { ProductUrl = productUrl };
            
            // EXTRACT PRODUCT NAME
            try
            {
                var nameSelectors = new[] { "//h1[@id='product-name']", "//h1[contains(@class, 'product-name')]", "//h1" };
                foreach (var selector in nameSelectors)
                {
                    var node = htmlDoc.DocumentNode.SelectSingleNode(selector);
                    if (node != null && !string.IsNullOrWhiteSpace(node.InnerText))
                    {
                        product.Name = node.InnerText.Trim();
                        break;
                    }
                }
            }
            catch { }

            // EXTRACT BRAND
            try
            {
                var brandSelectors = new[] { "//span[contains(@class, 'brand')]", "//a[contains(@class, 'brand')]" };
                foreach (var selector in brandSelectors)
                {
                    var node = htmlDoc.DocumentNode.SelectSingleNode(selector);
                    if (node != null && !string.IsNullOrWhiteSpace(node.InnerText))
                    {
                        product.Brand = node.InnerText.Trim();
                        break;
                    }
                }
                
                if (string.IsNullOrEmpty(product.Brand))
                {
                    var scriptNodes = htmlDoc.DocumentNode.SelectNodes("//script[contains(text(), 'brand')]");
                    if (scriptNodes != null)
                    {
                        foreach (var scriptNode in scriptNodes)
                        {
                            var scriptText = scriptNode.InnerText;
                            var brandMatch = Regex.Match(scriptText, @"""brand"":\s*""([^""]+)""", RegexOptions.IgnoreCase);
                            if (brandMatch.Success)
                            {
                                product.Brand = brandMatch.Groups[1].Value;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // EXTRACT PRICE
            try
            {
                if (Method == ScrapeMethod.Selenium && _driver != null)
                {
                    try
                    {
                        var jsExecutor = (IJavaScriptExecutor)_driver!;
                        var priceData = jsExecutor.ExecuteScript(@"
                            var priceElem = document.querySelector('[data-bind=""markupText:'currentPriceBeforePoint'""]') || 
                                           document.querySelector('.price-value') ||
                                           document.querySelector('[itemprop=""price""]');
                            return priceElem ? priceElem.textContent.trim() : '';
                        ");
                        
                        if (priceData != null && !string.IsNullOrWhiteSpace(priceData.ToString()))
                        {
                            product.DiscountedPrice = CleanPrice(priceData.ToString()!);
                        }
                    }
                    catch { }
                }
                
                if (string.IsNullOrEmpty(product.DiscountedPrice))
                {
                    var priceSelectors = new[] {
                        "//span[@data-bind=\"markupText:'currentPriceBeforePoint'\"]",
                        "//span[@itemprop='price']",
                        "//*[contains(@class, 'price-value')]"
                    };

                    foreach (var selector in priceSelectors)
                    {
                        var node = htmlDoc.DocumentNode.SelectSingleNode(selector);
                        if (node != null && !string.IsNullOrWhiteSpace(node.InnerText))
                        {
                            product.DiscountedPrice = CleanPrice(node.InnerText);
                            break;
                        }
                    }
                }
            }
            catch { }

            // EXTRACT SELLER
            try
            {
                var sellerSelectors = new[] { "//a[contains(@class, 'merchant')]", "//span[contains(@class, 'seller-name')]" };
                foreach (var selector in sellerSelectors)
                {
                    var node = htmlDoc.DocumentNode.SelectSingleNode(selector);
                    if (node != null && !string.IsNullOrWhiteSpace(node.InnerText))
                    {
                        product.Seller = node.InnerText.Trim();
                        break;
                    }
                }
            }
            catch { }

            // EXTRACT IMAGES
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
                            document.querySelectorAll('[class*=""gallery""] img, [class*=""product-image""] img').forEach(img => {
                                var src = img.src || img.getAttribute('data-src') || '';
                                if (src && !images.includes(src)) {
                                    images.push(src);
                                }
                            });
                            return images.join('|||');
                        ");
                        
                        if (imageUrls != null && !string.IsNullOrWhiteSpace(imageUrls.ToString()))
                        {
                            var urls = imageUrls.ToString()!.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var url in urls)
                            {
                                var imgUrl = url.Trim();
                                if (imgUrl.StartsWith("//"))
                                    imgUrl = "https:" + imgUrl;
                                else if (!imgUrl.StartsWith("http"))
                                    imgUrl = BaseUrl + imgUrl;
                                
                                if (!allImages.Contains(imgUrl))
                                    allImages.Add(imgUrl);
                            }
                        }
                    }
                    catch { }
                }
                
                if (allImages.Count == 0)
                {
                    var imageSelectors = new[] { "//img[contains(@class, 'product-image')]", "//div[contains(@class, 'gallery')]//img" };
                    foreach (var selector in imageSelectors)
                    {
                        var nodes = htmlDoc.DocumentNode.SelectNodes(selector);
                        if (nodes != null)
                        {
                            foreach (var node in nodes)
                            {
                                var imgUrl = node.GetAttributeValue("src", "") ?? node.GetAttributeValue("data-src", "");
                                
                                if (!string.IsNullOrEmpty(imgUrl))
                                {
                                    if (imgUrl.StartsWith("//"))
                                        imgUrl = "https:" + imgUrl;
                                    else if (!imgUrl.StartsWith("http"))
                                        imgUrl = BaseUrl + imgUrl;
                                    
                                    if (!allImages.Contains(imgUrl))
                                        allImages.Add(imgUrl);
                                }
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
                var breadcrumbNodes = htmlDoc.DocumentNode.SelectNodes("//ol[contains(@class, 'breadcrumb')]//a");
                if (breadcrumbNodes != null && breadcrumbNodes.Count > 0)
                {
                    var categories = breadcrumbNodes
                        .Select(n => n.InnerText.Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s) && s != "Ana Sayfa")
                        .ToList();
                    
                    if (categories.Count > 0)
                    {
                        product.Category = string.Join(" > ", categories);
                    }
                }
            }
            catch { }

            // EXTRACT DESCRIPTION - Simplified
            try
            {
                var scriptNodes = htmlDoc.DocumentNode.SelectNodes("//script[contains(text(), 'description')]");
                if (scriptNodes != null)
                {
                    foreach (var scriptNode in scriptNodes)
                    {
                        var scriptText = scriptNode.InnerText;
                        var jsonMatch = Regex.Match(scriptText, @"""description"":\s*""([^""]{100,})""", RegexOptions.IgnoreCase);
                        if (jsonMatch.Success)
                        {
                            var desc = Regex.Unescape(jsonMatch.Groups[1].Value);
                            desc = Regex.Replace(desc, @"\s+", " ").Trim();
                            
                            if (desc.Length > 2000)
                                desc = desc.Substring(0, 2000) + "...";
                            
                            product.Description = desc;
                            break;
                        }
                    }
                }
            }
            catch { }

            // EXTRACT BARCODE - Simplified
            try
            {
                var stockCodePatterns = new[] { "//*[contains(text(), 'Stok Kodu')]", "//*[contains(text(), 'Barkod')]" };
                foreach (var pattern in stockCodePatterns)
                {
                    var nodes = htmlDoc.DocumentNode.SelectNodes(pattern);
                    if (nodes != null)
                    {
                        foreach (var node in nodes)
                        {
                            var text = node.InnerText.Trim();
                            var match = Regex.Match(text, @"(?:Stok Kodu|Barkod)[:\s]+([A-Z0-9]+)", RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                product.Barcode = match.Groups[1].Value;
                                break;
                            }
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(product.Barcode))
                        break;
                }
            }
            catch { }

            // EXTRACT PRODUCT ATTRIBUTES
            await ExtractHepsiburadaAttributes(htmlDoc, product);

            return product;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error scraping product: {ex.Message}");
        }

        return null;
    }

    private async Task ExtractHepsiburadaAttributes(HtmlDocument htmlDoc, ProductInfo product)
    {
        try
        {
            // Trigger lazy loading if using Selenium
            if (Method == ScrapeMethod.Selenium && _driver != null)
            {
                try
                {
                    var jsExecutor = (IJavaScriptExecutor)_driver;
                    jsExecutor.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                    await Task.Delay(700);
                    
                    jsExecutor.ExecuteScript(@"
                        var sections = document.querySelectorAll('section[data-hydration-on-demand]');
                        sections.forEach(function(section) {
                            section.style.display = 'block';
                            section.setAttribute('data-hydration-on-demand', 'false');
                        });
                    ");
                    
                    await Task.Delay(1000);
                    
                    var updatedHtml = _driver.PageSource;
                    htmlDoc = new HtmlDocument();
                    htmlDoc.LoadHtml(updatedHtml);
                }
                catch { }
            }
            
            // Try table selectors
            var attributeSelectors = new[]
            {
                "//table//tr[.//td[2]]",
                "//section[contains(@class, 'product')]//table//tr",
                "//div[contains(@class, 'product-detail')]//table//tr"
            };

            bool found = false;
            foreach (var selector in attributeSelectors)
            {
                var attributeRows = htmlDoc.DocumentNode.SelectNodes(selector);
                
                if (attributeRows != null && attributeRows.Count > 0)
                {
                    foreach (var row in attributeRows)
                    {
                        try
                        {
                            var cells = row.SelectNodes(".//td");
                            if (cells != null && cells.Count >= 2)
                            {
                                var key = Regex.Replace(cells[0].InnerText.Trim(), @"\s+", " ");
                                var value = Regex.Replace(cells[1].InnerText.Trim(), @"\s+", " ");
                                
                                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                                {
                                    product.Attributes[key] = value;
                                }
                            }
                        }
                        catch { }
                    }
                    
                    if (product.Attributes.Count > 0)
                    {
                        found = true;
                        break;
                    }
                }
            }
            
            // Try definition lists if tables didn't work
            if (!found)
            {
                var dtElements = htmlDoc.DocumentNode.SelectNodes("//dt");
                if (dtElements != null)
                {
                    foreach (var dt in dtElements)
                    {
                        try
                        {
                            var dd = dt.NextSibling;
                            while (dd != null && dd.Name != "dd")
                            {
                                dd = dd.NextSibling;
                            }
                            
                            if (dd != null)
                            {
                                var key = Regex.Replace(dt.InnerText.Trim(), @"\s+", " ");
                                var value = Regex.Replace(dd.InnerText.Trim(), @"\s+", " ");
                                
                                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                                {
                                    product.Attributes[key] = value;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        
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
            if (priceText.Contains("TL") || priceText.Contains("?"))
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
            
            await Task.Delay(300);
        }

        return products;
    }

    public void Dispose()
    {
        _driver?.Quit();
        _driver?.Dispose();
    }
}
