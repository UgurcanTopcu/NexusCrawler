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
            
            // Parse URL - preserve the original query string to avoid encoding issues
            var uri = new Uri(categoryUrl.StartsWith("http") ? categoryUrl : "https://" + categoryUrl);
            var basePath = uri.GetLeftPart(UriPartial.Path);
            var originalQuery = uri.Query; // Keep original query string as-is
            
            // Check if this is a search URL (/ara)
            bool isSearchUrl = basePath.Contains("/ara");
            
            Console.WriteLine("\n[Hepsiburada] Starting product discovery...");
            Console.WriteLine($"[Hepsiburada] URL Type: {(isSearchUrl ? "Search" : "Category")}");
            Console.WriteLine($"[Hepsiburada] Target: {maxProducts} products");
            Console.WriteLine($"[Hepsiburada] Base URL: {basePath}");
            Console.WriteLine($"[Hepsiburada] Original Query: {originalQuery}");
            Console.Out.Flush();
            
            if (onProgress != null)
            {
                await onProgress(5, $"?? Finding products (target: {maxProducts})...", "info");
            }
            
            int page = 1;
            // Hepsiburada shows ~36 products per page, calculate max pages accordingly
            int productsPerPage = 36;
            int maxPages = Math.Max(50, (maxProducts / productsPerPage) + 10);
            int previousCount = 0;
            int consecutiveEmptyPages = 0;
            int consecutiveNoNewProducts = 0;
            
            Console.WriteLine($"[Hepsiburada] Max pages to check: {maxPages}");
            
            while (page <= maxPages && productLinks.Count < maxProducts)
            {
                // Build paginated URL preserving original query string exactly
                string paginatedUrl;
                
                if (page == 1)
                {
                    // First page - use original URL as-is
                    paginatedUrl = categoryUrl.StartsWith("http") ? categoryUrl : "https://" + categoryUrl;
                }
                else
                {
                    // For subsequent pages, carefully append/replace sayfa parameter
                    if (string.IsNullOrEmpty(originalQuery))
                    {
                        // No existing query params
                        paginatedUrl = $"{basePath}?sayfa={page}";
                    }
                    else
                    {
                        // Has existing query params - preserve them and add/update sayfa
                        var queryWithoutQuestionMark = originalQuery.TrimStart('?');
                        
                        // Remove any existing sayfa parameter
                        var queryParts = queryWithoutQuestionMark.Split('&')
                            .Where(p => !p.StartsWith("sayfa=", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        
                        // Add the new sayfa parameter
                        queryParts.Add($"sayfa={page}");
                        
                        paginatedUrl = $"{basePath}?{string.Join("&", queryParts)}";
                    }
                }
                
                Console.WriteLine($"\n[Hepsiburada] Page {page}: {paginatedUrl}");
                Console.Out.Flush();
                
                _driver!.Navigate().GoToUrl(paginatedUrl);
                
                var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(25));
                
                // Try multiple selectors - search pages may use different CSS classes
                bool foundProducts = false;
                try
                {
                    // Wait for any product link to appear - increased timeout for search pages
                    wait.Until(d => 
                        d.FindElements(By.CssSelector("a[href*='-p-']")).Count > 0 ||
                        d.FindElements(By.CssSelector("[class*='productCard']")).Count > 0 ||
                        d.FindElements(By.CssSelector("[data-test-id='product-card-item']")).Count > 0
                    );
                    foundProducts = true;
                }
                catch 
                {
                    Console.WriteLine($"[Hepsiburada] ? No products found on page {page} (timeout)");
                    consecutiveEmptyPages++;
                    if (consecutiveEmptyPages >= 3)
                    {
                        Console.WriteLine($"[Hepsiburada] ? Reached end of results (3 consecutive empty pages)");
                        Console.Out.Flush();
                        break;
                    }
                    page++;
                    await Task.Delay(1000);
                    continue;
                }
                
                // Reset empty page counter on success
                consecutiveEmptyPages = 0;
                
                // More aggressive scrolling for search pages with lazy loading
                for (int i = 0; i < 12; i++)
                {
                    ((IJavaScriptExecutor)_driver).ExecuteScript($"window.scrollTo(0, document.body.scrollHeight * {(i + 1) / 12.0});");
                    await Task.Delay(200);
                }
                
                // Scroll back to top and then to bottom again to trigger any remaining lazy loads
                ((IJavaScriptExecutor)_driver).ExecuteScript("window.scrollTo(0, 0);");
                await Task.Delay(300);
                ((IJavaScriptExecutor)_driver).ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                await Task.Delay(800);

                // Extract links from current page using multiple methods
                var jsExecutor = (IJavaScriptExecutor)_driver;
                var productUrls = jsExecutor.ExecuteScript(@"
                    var links = [];
                    var seen = {};
                    
                    // Hepsiburada product URL pattern: /{name}-p-{CODE}
                    // We need to be more specific - only actual product cards
                    
                    // Method 1: Product cards (highest priority)
                    document.querySelectorAll('article.product, li.product, div[class*=""productListContent""]').forEach(function(card) {
                        var link = card.querySelector('a[href*=""-p-""]');
                        if (link && link.href) {
                            var href = link.href;
                            if (href.includes('-p-') && !seen[href]) {
                                seen[href] = true;
                                links.push(href);
                            }
                        }
                    });
                    
                    // Method 2: Direct product links in product cards with data-test-id
                    document.querySelectorAll('[data-test-id=""product-card-item""] a[href*=""-p-""]').forEach(function(link) {
                        var href = link.href;
                        if (href && href.includes('-p-') && !seen[href]) {
                            seen[href] = true;
                            links.push(href);
                        }
                    });
                    
                    // Method 3: Product list items
                    document.querySelectorAll('ul[class*=""product""] li a[href*=""-p-""], ol[class*=""product""] li a[href*=""-p-""]').forEach(function(link) {
                        var href = link.href;
                        if (href && href.includes('-p-') && !seen[href]) {
                            seen[href] = true;
                            links.push(href);
                        }
                    });
                    
                    // Method 4: Fallback - but ONLY first link in any container with -p-
                    if (links.length === 0) {
                        document.querySelectorAll('a[href*=""-p-""]').forEach(function(link) {
                            var href = link.href;
                            if (href && href.includes('-p-') && !seen[href]) {
                                // Check if this is actually a product link (not category/filter/etc)
                                if (href.match(/\/[^\/]+-p-[A-Z0-9]+$/)) {
                                    seen[href] = true;
                                    links.push(href);
                                }
                            }
                        });
                    }
                    
                    console.log('Found ' + links.length + ' product links on page');
                    return links.join('|||');
                ");
                
                int newLinksOnPage = 0;
                int rawLinksCount = 0;
                
                if (productUrls != null && !string.IsNullOrWhiteSpace(productUrls.ToString()))
                {
                    var urls = productUrls.ToString()!.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries);
                    rawLinksCount = urls.Length;
                    Console.WriteLine($"[Hepsiburada] Raw links found on page {page}: {rawLinksCount}");
                    
                    foreach (var url in urls)
                    {
                        if (productLinks.Count >= maxProducts) break;
                        
                        var cleanUrl = url.Split('?')[0].Split('#')[0];
                        
                        // Strict validation: URL must end with -p-{CODE} pattern
                        // Example: /product-name-p-HBCV00007XO59V
                        if (Regex.IsMatch(cleanUrl, @"^https?://[^/]+/[^/]+-p-[A-Z0-9]+$", RegexOptions.IgnoreCase))
                        {
                            if (!productLinks.Contains(cleanUrl))
                            {
                                productLinks.Add(cleanUrl);
                                newLinksOnPage++;
                                
                                // Log the first few to help debug
                                if (productLinks.Count <= 3)
                                {
                                    Console.WriteLine($"[Hepsiburada] Added: {cleanUrl}");
                                }
                            }
                        }
                        else
                        {
                            // Log rejected URLs for first page only
                            if (page == 1 && newLinksOnPage < 5)
                            {
                                Console.WriteLine($"[Hepsiburada] Rejected (invalid pattern): {cleanUrl}");
                            }
                        }
                    }
                }
                
                Console.WriteLine($"[Hepsiburada] Page {page}: +{newLinksOnPage} new | Total: {productLinks.Count}/{maxProducts}");
                Console.Out.Flush();
                
                // Send progress update
                if (onProgress != null && productLinks.Count % 10 == 0)
                {
                    var progressPercent = Math.Min(5 + (int)((productLinks.Count / (double)maxProducts) * 5), 10);
                    await onProgress(progressPercent, $"?? Found {productLinks.Count}/{maxProducts} products (page {page})", "info");
                }
                
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
                
                // Check for pages with no new products (all duplicates)
                if (newLinksOnPage == 0)
                {
                    consecutiveNoNewProducts++;
                    Console.WriteLine($"[Hepsiburada] ? Page {page} had no new products ({consecutiveNoNewProducts} consecutive)");
                    
                    // If page had raw links but all were duplicates, might be end of unique content
                    if (consecutiveNoNewProducts >= 3)
                    {
                        Console.WriteLine($"[Hepsiburada] ? End of unique products (3 pages with only duplicates)");
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
                    consecutiveNoNewProducts = 0;
                }
                
                // Check if page had no products at all
                if (rawLinksCount == 0)
                {
                    consecutiveEmptyPages++;
                    if (consecutiveEmptyPages >= 2)
                    {
                        Console.WriteLine($"[Hepsiburada] ? End of results (empty pages)");
                        break;
                    }
                }
                
                previousCount = productLinks.Count;
                page++;
                await Task.Delay(700); // Delay between pages to avoid rate limiting
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
            try
            {
                var match = Regex.Match(productUrl, @"-(pm?)-([A-Z0-9]+)$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    product.ProductId = match.Groups[2].Value;
                }
            }
            catch { }
            
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
                            var brandMatch = Regex.Match(scriptNode.InnerText, @"""brand"":\s*""([^""]+)""", RegexOptions.IgnoreCase);
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

            // EXTRACT IMAGES - from pdp-carouselContainer with size formatting
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
                            var seen = {};
                            
                            document.querySelectorAll('#pdp-carouselContainer picture source, #pdp-carouselContainer picture img').forEach(function(el) {
                                var src = el.srcset || el.src || '';
                                src = src.split(' ')[0].split(',')[0].trim();
                                if (src && src.includes('productimages.hepsiburada.net') && !seen[src]) {
                                    seen[src] = true;
                                    images.push(src);
                                }
                            });
                            
                            document.querySelectorAll('li[id^=""pdp-carousel__slide""] img').forEach(function(img) {
                                var src = img.src || img.getAttribute('data-src') || '';
                                if (src && src.includes('productimages.hepsiburada.net') && !seen[src]) {
                                    seen[src] = true;
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
                                var imgUrl = ConvertToHighResImage(url.Trim());
                                
                                if (imgUrl.StartsWith("//"))
                                    imgUrl = "https:" + imgUrl;
                                
                                if (imgUrl.Contains("automation", StringComparison.OrdinalIgnoreCase) || 
                                    imgUrl.Contains("badge", StringComparison.OrdinalIgnoreCase) ||
                                    imgUrl.Contains("banners", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                
                                if (!allImages.Contains(imgUrl))
                                    allImages.Add(imgUrl);
                            }
                        }
                    }
                    catch { }
                }
                
                // Fallback: Parse HTML directly
                if (allImages.Count == 0)
                {
                    var noscriptMatches = Regex.Matches(html, @"productimages\.hepsiburada\.net/s/\d+/[\d-]+/\d+\.jpg");
                    foreach (Match match in noscriptMatches)
                    {
                        var imgUrl = ConvertToHighResImage("https://" + match.Value);
                        if (!allImages.Contains(imgUrl))
                            allImages.Add(imgUrl);
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

            // EXTRACT DESCRIPTION - from sfProductDesc div
            try
            {
                var descNode = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'sfProductDesc')]/following-sibling::div[1]");
                if (descNode != null && !string.IsNullOrWhiteSpace(descNode.InnerText))
                {
                    var desc = Regex.Replace(descNode.InnerText.Trim(), @"\s+", " ");
                    if (desc.Length > 2000)
                        desc = desc.Substring(0, 2000) + "...";
                    product.Description = desc;
                }
                
                // Fallback to script extraction
                if (string.IsNullOrEmpty(product.Description))
                {
                    var scriptNodes = htmlDoc.DocumentNode.SelectNodes("//script[contains(text(), 'description')]");
                    if (scriptNodes != null)
                    {
                        foreach (var scriptNode in scriptNodes)
                        {
                            var jsonMatch = Regex.Match(scriptNode.InnerText, @"""description"":\s*""([^""]{100,})""", RegexOptions.IgnoreCase);
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
            }
            catch { }

            // EXTRACT BARCODE - from product_barcode in scripts
            try
            {
                var scriptNodes = htmlDoc.DocumentNode.SelectNodes("//script");
                if (scriptNodes != null)
                {
                    foreach (var scriptNode in scriptNodes)
                    {
                        if (scriptNode.InnerText.Contains("product_barcode"))
                        {
                            var barcodeMatch = Regex.Match(scriptNode.InnerText, @"""product_barcode""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                            if (barcodeMatch.Success && !string.IsNullOrWhiteSpace(barcodeMatch.Groups[1].Value))
                            {
                                product.Barcode = barcodeMatch.Groups[1].Value.Trim();
                                break;
                            }
                        }
                    }
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
                        document.querySelectorAll('section[data-hydration-on-demand]').forEach(function(section) {
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
            var attributeRows = htmlDoc.DocumentNode.SelectNodes("//table//tr[.//td[2]]");
            
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
            }
            
            // Try definition lists if tables didn't work
            if (product.Attributes.Count == 0)
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
                                dd = dd.NextSibling;
                            
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

    /// <summary>
    /// Converts Hepsiburada image URL to high resolution (1000x1000)
    /// Example: https://productimages.hepsiburada.net/s/777/424-600/110000936663290.jpg
    ///       -> https://productimages.hepsiburada.net/s/777/1000-1000/110000936663290.jpg
    /// </summary>
    private string ConvertToHighResImage(string imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return imageUrl;
        
        // Pattern: /s/{number}/{dimensions}/{imageid}.jpg
        // Replace dimensions like 424-600, 48-64, 222-222, etc. with 1000-1000
        var pattern = @"/s/(\d+)/(\d+-\d+)/";
        var replacement = "/s/$1/1000-1000/";
        
        var result = Regex.Replace(imageUrl, pattern, replacement);
        
        // Also remove /format:webp suffix if present
        result = Regex.Replace(result, @"/format:webp$", "");
        
        return result;
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
