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

    public async Task<List<string>> GetProductLinksAsync(string categoryUrl, int maxProducts = 50, Func<int, string, string, Task>? onProgress = null)
    {
        if (Method == ScrapeMethod.ScrapeDo)
        {
            return await _scrapeDoService!.GetProductLinksAsync(categoryUrl, maxProducts, isHepsiburada: true);
        }

        var productLinks = new List<string>();

        try
        {
            InitializeDriver();
            
            // Parse URL to preserve existing query parameters
            var uri = new Uri(categoryUrl.StartsWith("http") ? categoryUrl : "https://" + categoryUrl);
            var basePath = uri.GetLeftPart(UriPartial.Path);
            var existingParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
            
            // Remove pagination parameter if it exists
            existingParams.Remove("sayfa");
            
            Console.WriteLine("\n[Hepsiburada] Starting product discovery...");
            Console.WriteLine($"[Hepsiburada] Target: {maxProducts} products");
            Console.Out.Flush();
            
            if (onProgress != null)
            {
                await onProgress(5, $"?? Finding products (target: {maxProducts})...", "info");
            }
            
            int page = 1;
            int maxPages = Math.Max(30, (maxProducts / 20) + 5);
            int previousCount = 0;
            int emptyPageCount = 0;
            
            while (page <= maxPages && productLinks.Count < maxProducts)
            {
                // Build paginated URL preserving existing parameters
                string paginatedUrl;
                var pageParams = System.Web.HttpUtility.ParseQueryString(existingParams.ToString());
                
                if (page > 1)
                {
                    pageParams["sayfa"] = page.ToString();
                }
                
                paginatedUrl = pageParams.Count > 0 ? $"{basePath}?{pageParams}" : basePath;
                
                Console.WriteLine($"[Hepsiburada] Page {page}...");
                Console.Out.Flush();
                
                _driver!.Navigate().GoToUrl(paginatedUrl);
                
                var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
                
                try
                {
                    wait.Until(d => d.FindElements(By.CssSelector("[class*='productCardLink']")).Count > 0);
                }
                catch 
                {
                    emptyPageCount++;
                    if (emptyPageCount >= 2)
                    {
                        Console.WriteLine($"[Hepsiburada] No more products available");
                        Console.Out.Flush();
                        break;
                    }
                    page++;
                    continue;
                }
                
                // Scroll to load lazy content
                for (int i = 0; i < 5; i++)
                {
                    ((IJavaScriptExecutor)_driver).ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                    await Task.Delay(400);
                }
                
                await Task.Delay(500);

                // Extract links from current page
                var jsExecutor = (IJavaScriptExecutor)_driver;
                var productUrls = jsExecutor.ExecuteScript(@"
                    var links = [];
                    var productCardLinks = document.querySelectorAll('a[class*=""productCardLink""]');
                    productCardLinks.forEach(function(link) {
                        var href = link.href;
                        if (href && href.includes('-p-')) {
                            links.push(href);
                        }
                    });
                    return links.join('|||');
                ");
                
                int newLinksOnPage = 0;
                
                if (productUrls != null && !string.IsNullOrWhiteSpace(productUrls.ToString()))
                {
                    var urls = productUrls.ToString()!.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries);
                    
                    foreach (var url in urls)
                    {
                        if (productLinks.Count >= maxProducts) break;
                        
                        var cleanUrl = url.Split('?')[0];
                        
                        if (Regex.IsMatch(cleanUrl, @"-(pm|p)-[A-Z0-9]+$"))
                        {
                            if (!productLinks.Contains(cleanUrl))
                            {
                                productLinks.Add(cleanUrl);
                                newLinksOnPage++;
                                
                                // Show LIVE count on console
                                Console.Write($"\r[Hepsiburada] Found: {productLinks.Count}/{maxProducts} products   ");
                                Console.Out.Flush();
                                
                                // Send to UI every 5 products or at target
                                if (onProgress != null && (productLinks.Count % 5 == 0 || productLinks.Count == maxProducts))
                                {
                                    var progressPercent = Math.Min(5 + (int)((productLinks.Count / (double)maxProducts) * 5), 10);
                                    await onProgress(progressPercent, $"?? Found {productLinks.Count}/{maxProducts} products", "info");
                                }
                            }
                        }
                    }
                }
                
                Console.WriteLine(); // New line after the count
                Console.Out.Flush();
                
                // Check if we reached target
                if (productLinks.Count >= maxProducts)
                {
                    Console.WriteLine($"[Hepsiburada] ? Target reached!");
                    Console.Out.Flush();
                    if (onProgress != null)
                    {
                        await onProgress(10, $"? Found all {productLinks.Count} product URLs!", "success");
                    }
                    break;
                }
                
                // Check for empty pages
                if (productLinks.Count == previousCount)
                {
                    emptyPageCount++;
                    if (emptyPageCount >= 2)
                    {
                        Console.WriteLine($"[Hepsiburada] ? End of available products");
                        Console.Out.Flush();
                        if (onProgress != null && productLinks.Count > 0)
                        {
                            await onProgress(10, $"? Found {productLinks.Count} products (all available)", "success");
                        }
                        break;
                    }
                }
                else
                {
                    emptyPageCount = 0;
                    previousCount = productLinks.Count;
                }
                
                page++;
                await Task.Delay(300);
            }
            
            Console.WriteLine($"\n[Hepsiburada] ? Total: {productLinks.Count} products from {page - 1} pages\n");
            Console.Out.Flush();
            
            // Debug: Print first few links
            if (productLinks.Count > 0)
            {
                Console.WriteLine("Sample product links:");
                foreach (var link in productLinks.Take(3))
                {
                    Console.WriteLine($"  - {link}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching product links: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            if (onProgress != null)
            {
                await onProgress(10, $"? Error finding products: {ex.Message}", "error");
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
                    wait.Until(d => d.FindElements(By.CssSelector("h1, .product-name")).Count > 0);
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
                Source = "Orange"
            };
            
            // EXTRACT PRODUCT ID from URL
            // Hepsiburada URL format: https://www.hepsiburada.com/product-name-p-HBCV0000870EF8 or -pm-CODE
            try
            {
                var match = Regex.Match(productUrl, @"-(pm?)-([A-Z0-9]+)$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    product.ProductId = match.Groups[2].Value;
                    Console.WriteLine($"[Product ID] ? Extracted: {product.ProductId} from {productUrl}");
                }
                else
                {
                    Console.WriteLine($"[Product ID] ? FAILED to extract from URL: {productUrl}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Product ID] ? Extraction error: {ex.Message}");
            }
            
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
                                
                                // Filter out marketing/automation images
                                if (imgUrl.Contains("automation", StringComparison.OrdinalIgnoreCase) || 
                                    imgUrl.Contains("marketing", StringComparison.OrdinalIgnoreCase))
                                {
                                    Console.WriteLine($"[Image Filter] Skipping marketing/automation image: {imgUrl.Substring(0, Math.Min(80, imgUrl.Length))}");
                                    continue;
                                }
                                
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
                                    
                                    // Filter out marketing/automation images
                                    if (imgUrl.Contains("automation", StringComparison.OrdinalIgnoreCase) || 
                                        imgUrl.Contains("marketing", StringComparison.OrdinalIgnoreCase))
                                    {
                                        Console.WriteLine($"[Image Filter] Skipping marketing/automation image: {imgUrl.Substring(0, Math.Min(80, imgUrl.Length))}");
                                        continue;
                                    }
                                    
                                    if (!allImages.Contains(imgUrl))
                                        allImages.Add(imgUrl);
                                }
                            }
                        }
                    }
                }
                
                if (allImages.Count > 0)
                {
                    Console.WriteLine($"[Image Extraction] Found {allImages.Count} valid product images (after filtering)");
                    product.ImageUrl = allImages[0];
                    for (int i = 1; i < allImages.Count; i++)
                    {
                        product.AdditionalImages.Add(allImages[i]);
                    }
                }
                else
                {
                    Console.WriteLine("[Image Extraction] No valid images found after filtering");
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
        
        var productLinks = await GetProductLinksAsync(categoryUrl, maxProducts);
        
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
