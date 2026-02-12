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




            Console.Out.Flush();
            
            if (onProgress != null)
            {
                await onProgress(5, $"?? Finding products (target: {maxProducts})...", "info");
            }
            
            int page = 1;
            // Hepsiburada shows ~36 products per page, support up to 2000 products
            int productsPerPage = 36;
            int maxPages = Math.Max(100, (maxProducts / productsPerPage) + 15);
            int consecutiveEmptyPages = 0;
            int consecutiveNoNewProducts = 0;
            
            while (page <= maxPages && productLinks.Count < maxProducts)
            {
                // Build paginated URL - Hepsiburada uses "sayfa" parameter
                string paginatedUrl;
                
                if (page == 1)
                {
                    paginatedUrl = categoryUrl.StartsWith("http") ? categoryUrl : "https://" + categoryUrl;
                }
                else
                {
                    // Hepsiburada pagination: ?sayfa=X or &sayfa=X
                    if (string.IsNullOrEmpty(originalQuery))
                    {
                        paginatedUrl = $"{basePath}?sayfa={page}";
                    }
                    else
                    {
                        var queryWithoutQuestionMark = originalQuery.TrimStart('?');
                        var queryParts = queryWithoutQuestionMark.Split('&')
                            .Where(p => !p.StartsWith("sayfa=", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        queryParts.Add($"sayfa={page}");
                        paginatedUrl = $"{basePath}?{string.Join("&", queryParts)}";
                    }
                }
                Console.Out.Flush();
                
                _driver!.Navigate().GoToUrl(paginatedUrl);
                
                // Wait for page to load - look for product cards OR any content
                var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(20));
                
                try
                {
                    // Wait for product content to load - multiple possible selectors
                    wait.Until(d => 
                        d.FindElements(By.CssSelector("[data-test-id='product-card-item']")).Count > 0 ||
                        d.FindElements(By.CssSelector("[class*='productCard']")).Count > 0 ||
                        d.FindElements(By.CssSelector("li[class*='product']")).Count > 0 ||
                        d.FindElements(By.CssSelector("a[href*='-p-']")).Count > 0 ||
                        d.FindElements(By.CssSelector("a[href*='-pm-']")).Count > 0 ||
                        d.FindElements(By.CssSelector("a[href*='/p-']")).Count > 0
                    );
                }
                catch 
                {
                    consecutiveEmptyPages++;
                    if (consecutiveEmptyPages >= 3)
                    {
                        break;
                    }
                    page++;
                    await Task.Delay(1000);
                    continue;
                }
                
                consecutiveEmptyPages = 0;
                
                // Aggressive scrolling to load all lazy-loaded products
                for (int i = 0; i < 15; i++)
                {
                    ((IJavaScriptExecutor)_driver).ExecuteScript($"window.scrollTo(0, document.body.scrollHeight * {(i + 1) / 15.0});");
                    await Task.Delay(300);
                }
                
                // Scroll back to top and down again
                ((IJavaScriptExecutor)_driver).ExecuteScript("window.scrollTo(0, 0);");
                await Task.Delay(500);
                ((IJavaScriptExecutor)_driver).ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                await Task.Delay(1000);

                // Extract product links using comprehensive JavaScript
                var jsExecutor = (IJavaScriptExecutor)_driver;
                var productUrls = jsExecutor.ExecuteScript(@"
                    var links = [];
                    var seen = {};
                    var stats = { 
                        totalATags: 0,
                        productCards: 0,
                        fromCards: 0,
                        fromDirectLinks: 0,
                        fromAdRedirects: 0,
                        rejected: { category: 0, duplicate: 0, noPattern: 0, nonProduct: 0 }
                    };
                    
                    console.log('[HB] Starting product link extraction...');
                    
                    // DEBUG: First, let's see what product containers exist on the page
                    var debugInfo = {
                        'li.productListContent': document.querySelectorAll('li.productListContent-zAP0Y').length,
                        'li with product class': document.querySelectorAll('li[class*=""product""]').length,
                        'article product': document.querySelectorAll('article[class*=""product""]').length,
                        'div productListContent': document.querySelectorAll('div[class*=""productListContent""]').length,
                        'all li in ul': document.querySelectorAll('ul > li').length,
                        'links with -p-': document.querySelectorAll('a[href*=""-p-""]').length,
                        'ad service links': document.querySelectorAll('a[href*=""adservice.hepsiburada.com""]').length
                    };
                    console.log('[HB] Debug selectors: ' + JSON.stringify(debugInfo));
                    
                    // METHOD 1: Find ALL links (including ad service links)
                    var productLinks = document.querySelectorAll('a[href]');
                    console.log('[HB] Found ' + productLinks.length + ' total links');
                    
                    productLinks.forEach(function(link) {
                        var href = link.href;
                        if (!href || !href.includes('hepsiburada.com')) return;
                        
                        var cleanHref = href;
                        var isAdLink = false;
                        
                        // CHECK 1: Is this an ad service link with redirect parameter?
                        if (href.includes('adservice.hepsiburada.com') && href.includes('redirect=')) {
                            // Extract the product title from the link element to verify it's a product
                            var title = link.getAttribute('title') || '';
                            
                            // Check if this looks like a product ad (has title and redirect)
                            if (title.length > 0 && href.includes('eventName=sp-click')) {
                                // Use the adservice URL as-is - Selenium will follow the redirect
                                cleanHref = href;
                                isAdLink = true;
                                
                                // Skip if already seen
                                if (seen[cleanHref]) {
                                    stats.rejected.duplicate++;
                                    return;
                                }
                                
                                seen[cleanHref] = true;
                                links.push(cleanHref);
                                stats.fromAdRedirects++;
                                console.log('[HB] Added ad link: ' + title);
                                return;
                            }
                        }
                        
                        // CHECK 2: Skip other adservice links (tracking, non-product)
                        if (href.includes('adservice') || 
                            href.includes('/track') || 
                            href.includes('/event')) {
                            if (!isAdLink) {
                                stats.rejected.nonProduct++;
                            }
                            return;
                        }
                        
                        // CHECK 3: Regular product link processing
                        cleanHref = href.split('?')[0].split('#')[0];
                        
                        // Skip if already seen
                        if (seen[cleanHref]) {
                            stats.rejected.duplicate++;
                            return;
                        }
                        
                        // Skip category pages
                        if (cleanHref.includes('-c-')) {
                            stats.rejected.category++;
                            return;
                        }
                        
                        // Skip non-product links
                        if (cleanHref.includes('/hesabim') ||
                            cleanHref.includes('/magaza/') ||
                            cleanHref.includes('/liste/') ||
                            cleanHref.includes('/ara?')) {
                            stats.rejected.nonProduct++;
                            return;
                        }
                        
                        // Must contain -p- or -pm- (product indicator)
                        if (cleanHref.includes('-p-') || cleanHref.includes('-pm-')) {
                            seen[cleanHref] = true;
                            links.push(cleanHref);
                            stats.fromDirectLinks++;
                        }
                    });
                    
                    console.log('[HB] Total unique product links: ' + links.length);
                    console.log('[HB] From direct links: ' + stats.fromDirectLinks);
                    console.log('[HB] From ad redirects: ' + stats.fromAdRedirects);
                    stats.productCards = links.length;
                    stats.totalATags = productLinks.length;
                    
                    // If we found less than expected, try additional patterns
                    if (links.length < 30) {
                        console.log('[HB] Trying additional patterns...');
                        
                        // Try /p- and -pm- patterns
                        document.querySelectorAll('a[href*=""/p-""], a[href*=""-pm-""]').forEach(function(link) {
                            var href = link.href;
                            if (!href || !href.includes('hepsiburada.com')) return;
                            
                            var cleanHref = href.split('?')[0].split('#')[0];
                            if (seen[cleanHref]) return;
                            if (cleanHref.includes('-c-')) return;
                            
                            seen[cleanHref] = true;
                            links.push(cleanHref);
                            stats.fromCards++;
                        });
                        
                        // Try links ending with HB product codes
                        document.querySelectorAll('a[href*=""hepsiburada.com""], a[href*=""-pm-""]').forEach(function(link) {
                            var href = link.href;
                            if (!href) return;
                            
                            var cleanHref = href.split('?')[0].split('#')[0];
                            if (seen[cleanHref]) return;
                            if (cleanHref.includes('-c-')) return;
                            
                            // Match pattern: ends with HB followed by alphanumeric
                            if (cleanHref.match(/HB[A-Z0-9]{8,}$/i)) {
                                seen[cleanHref] = true;
                                links.push(cleanHref);
                                stats.fromCards++;
                            }
                        });
                    }
                    
                    console.log('[HB] Final total: ' + links.length + ' unique product links');
                    console.log('[HB] Stats: DirectLinks=' + stats.fromDirectLinks + ', AdRedirects=' + stats.fromAdRedirects + ', Additional=' + stats.fromCards);
                    console.log('[HB] Rejected: Category=' + stats.rejected.category + ', Duplicate=' + stats.rejected.duplicate + ', NonProduct=' + stats.rejected.nonProduct);
                    
                    // Show sample
                    if (links.length > 0) {
                        console.log('[HB] Sample:');
                        for (var i = 0; i < Math.min(5, links.length); i++) {
                            console.log('  ' + links[i]);
                        }
                    }
                    
                    return JSON.stringify({
                        links: links,
                        stats: stats
                    });
                ");
                
                int newLinksOnPage = 0;
                int rawLinksCount = 0;
                int productCardsFound = 0;
                int fromAdRedirects = 0;
                
                if (productUrls != null && !string.IsNullOrWhiteSpace(productUrls.ToString()))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(productUrls.ToString()!);
                        
                        // Extract stats
                        if (doc.RootElement.TryGetProperty("stats", out var statsElem))
                        {
                            if (statsElem.TryGetProperty("productCards", out var cardsElem))
                                productCardsFound = cardsElem.GetInt32();
                            if (statsElem.TryGetProperty("fromAdRedirects", out var adRedirectsElem))
                                fromAdRedirects = adRedirectsElem.GetInt32();
                        }
                        
                        // Extract links
                        if (doc.RootElement.TryGetProperty("links", out var linksElem))
                        {
                            var linksList = new List<string>();
                            foreach (var linkElem in linksElem.EnumerateArray())
                            {
                                var link = linkElem.GetString();
                                if (!string.IsNullOrEmpty(link))
                                    linksList.Add(link);
                            }
                            
                            rawLinksCount = linksList.Count;
                            var adInfo = fromAdRedirects > 0 ? $" (including {fromAdRedirects} from ads)" : "";
                            
                            foreach (var url in linksList)
                            {
                                if (productLinks.Count >= maxProducts) break;
                                
                                var cleanUrl = url.Trim();
                                
                                if (!productLinks.Contains(cleanUrl))
                                {
                                    productLinks.Add(cleanUrl);
                                    newLinksOnPage++;
                                    
                                    if (productLinks.Count <= 5)
                                    {
                                    }
                                }
                            }
                            
                            if (newLinksOnPage != rawLinksCount)
                            {
                            }
                        }
                    }
                    catch (Exception parseEx)
                    {
                    }
                }
                else
                {
                }
                Console.Out.Flush();
                
                // Progress update
                if (onProgress != null && productLinks.Count % 10 == 0)
                {
                    var progressPercent = Math.Min(5 + (int)((productLinks.Count / (double)maxProducts) * 5), 10);
                    await onProgress(progressPercent, $"?? Found {productLinks.Count}/{maxProducts} products (page {page})", "info");
                }
                
                // Check if we reached target
                if (productLinks.Count >= maxProducts)
                {
                    if (onProgress != null)
                    {
                        await onProgress(10, $"? Found all {productLinks.Count} product URLs!", "success");
                    }
                    break;
                }
                
                // Check for pages with no new products
                if (newLinksOnPage == 0)
                {
                    consecutiveNoNewProducts++;
                    
                    if (consecutiveNoNewProducts >= 3)
                    {
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
                        break;
                    }
                }
                
                page++;
                await Task.Delay(800); // Delay between pages
            }
            Console.Out.Flush();
            
            if (productLinks.Count > 0)
            {
                foreach (var link in productLinks.Take(5))
                {
                }
            }
        }
        catch (Exception ex)
        {

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
                
                // If this was an adservice URL, wait for redirect
                if (productUrl.Contains("adservice.hepsiburada.com"))
                {
                    var redirectWait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));
                    try
                    {
                        // Wait for redirect to complete - URL should change to actual product page
                        redirectWait.Until(d => !d.Url.Contains("adservice.hepsiburada.com"));
                    }
                    catch
                    {
                    }
                    
                    // Update productUrl to the actual product URL after redirect
                    productUrl = _driver.Url.Split('?')[0].Split('#')[0];
                }
                
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
            
            // EXTRACT CATEGORY HIERARCHY from utagData script
            // This is critical for grouping products by category in Excel export
            try
            {
                var scriptNodes = htmlDoc.DocumentNode.SelectNodes("//script[contains(text(), 'utagData')]");
                if (scriptNodes != null)
                {
                    foreach (var scriptNode in scriptNodes)
                    {
                        var scriptText = scriptNode.InnerText;
                        
                        // Extract category_id_hierarchy: "2147483637 > 235604 > 234329"
                        var categoryIdMatch = Regex.Match(scriptText, @"""category_id_hierarchy""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        if (categoryIdMatch.Success)
                        {
                            product.CategoryIdHierarchy = categoryIdMatch.Groups[1].Value.Trim();
                        }
                        
                        // Extract category_name_hierarchy: "Beyaz Esya / Mutfak > Beyaz Esya & Ankastre > Ankastre Setler"
                        var categoryNameMatch = Regex.Match(scriptText, @"""category_name_hierarchy""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        if (categoryNameMatch.Success)
                        {
                            product.CategoryNameHierarchy = categoryNameMatch.Groups[1].Value.Trim();
                        }
                        
                        // Also try to extract barcode from utagData if not found elsewhere
                        if (string.IsNullOrEmpty(product.Barcode))
                        {
                            var barcodeMatch = Regex.Match(scriptText, @"""product_barcode""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                            if (barcodeMatch.Success && !string.IsNullOrWhiteSpace(barcodeMatch.Groups[1].Value))
                            {
                                product.Barcode = barcodeMatch.Groups[1].Value.Trim();
                            }
                        }
                        
                        // Extract brand from utagData if not found
                        if (string.IsNullOrEmpty(product.Brand))
                        {
                            var brandMatch = Regex.Match(scriptText, @"""product_brand""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                            if (brandMatch.Success)
                            {
                                product.Brand = brandMatch.Groups[1].Value.Trim();
                            }
                        }
                        
                        break; // Found utagData script, no need to continue
                    }
                }
            }
            catch (Exception ex)
            {
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

                                // For WebP images, take the 1000x1000 variant if available
                                if (src.includes('/s/') && src.includes('/format:webp')) {
                                    var webpSrc = src.replace(/\/s\/\\d+\\/[^/]+\\-([^/]+)\\.(webp)/, '/s/1000-1000/$1.$2');
                                    images.push(webpSrc);
                                } else {
                                    src = src.split(' ')[0].split(',')[0].trim();
                                    if (src && src.includes('productimages.hepsiburada.net') && !seen[src]) {
                                        seen[src] = true;
                                        images.push(src);
                                    }
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
        }

        return null;
    }

    private async Task ExtractHepsiburadaAttributes(HtmlDocument htmlDoc, ProductInfo product)
    {
        try
        {
            
            // Method 1: JavaScript extraction for Selenium (most reliable for dynamic content)
            if (Method == ScrapeMethod.Selenium && _driver != null)
            {
                try
                {
                    var jsExecutor = (IJavaScriptExecutor)_driver;
                    
                    // Scroll to trigger lazy loading of product details section
                    jsExecutor.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                    await Task.Delay(700);
                    
                    // Force display of lazy-loaded sections
                    jsExecutor.ExecuteScript(@"
                        document.querySelectorAll('section[data-hydration-on-demand]').forEach(function(section) {
                            section.style.display = 'block';
                            section.setAttribute('data-hydration-on-demand', 'false');
                        });
                    ");
                    
                    await Task.Delay(1000);
                    
                    // Extract attributes using multiple methods
                    var jsData = jsExecutor.ExecuteScript(@"
                        var attrs = [];
                        
                        console.log('[Hepsiburada JS] Starting attribute extraction...');
                        
                        // METHOD 1: Target the specific container divs with partial class matching
                        // Look for divs that contain 'jkj4C4LML4qv2Iq8GkL3' in their class
                        var attributeContainers = document.querySelectorAll('div[class*=""jkj4C4LML4qv2Iq8GkL3""]');
                        
                        console.log('[Hepsiburada JS] Method 1: Found ' + attributeContainers.length + ' containers with jkj4C4LML4qv2Iq8GkL3');
                        
                        for (var i = 0; i < attributeContainers.length; i++) {
                            var container = attributeContainers[i];
                            
                            // Get the key (attribute name) - look for div with OXP5AzPvafgN_i3y6wGp
                            var keyDiv = container.querySelector('div[class*=""OXP5AzPvafgN_i3y6wGp""]');
                            if (!keyDiv) continue;
                            
                            var key = keyDiv.textContent.trim();
                            
                            // Stop if we hit 'Hatalý içerik bildir'
                            if (key.includes('Hatalý') || key.includes('içerik') || key.includes('bildir')) {
                                console.log('[Hepsiburada JS] Reached end marker at index ' + i);
                                break;
                            }
                            
                            // Get the value - look for div with AxM3TmSghcDRH1F871Vh
                            var valueDiv = container.querySelector('div[class*=""AxM3TmSghcDRH1F871Vh""]');
                            if (!valueDiv) continue;
                            
                            var valueSpan = valueDiv.querySelector('span');
                            var value = valueSpan ? valueSpan.textContent.trim() : valueDiv.textContent.trim();
                            
                            if (key && value && key.length > 0 && value.length > 0) {
                                console.log('[Hepsiburada JS] Extracted: ' + key + ' = ' + value);
                                attrs.push({ key: key, value: value });
                            }
                        }
                        
                        // METHOD 2: If nothing found, try finding all divs with both key and value patterns
                        if (attrs.length === 0) {
                            console.log('[Hepsiburada JS] Method 2: Trying alternative selector...');
                            
                            // Find all divs that might be attribute rows
                            var allDivs = document.querySelectorAll('div');
                            var possibleContainers = [];
                            
                            for (var j = 0; j < allDivs.length; j++) {
                                var div = allDivs[j];
                                
                                // Check if this div has a child with key-like class
                                var hasKey = div.querySelector('div[class*=""OXP5AzPvafgN""]');
                                var hasValue = div.querySelector('div[class*=""AxM3TmSghcDRH1F871Vh""]');
                                
                                if (hasKey && hasValue) {
                                    var key2 = hasKey.textContent.trim();
                                    
                                    // Stop at end marker
                                    if (key2.includes('Hatalý') || key2.includes('içerik') || key2.includes('bildir')) {
                                        console.log('[Hepsiburada JS] Method 2: Reached end marker');
                                        break;
                                    }
                                    
                                    var valueSpan2 = hasValue.querySelector('span');
                                    var value2 = valueSpan2 ? valueSpan2.textContent.trim() : hasValue.textContent.trim();
                                    
                                    if (key2 && value2 && key2.length > 0 && value2.length > 0) {
                                        console.log('[Hepsiburada JS] Method 2 extracted: ' + key2 + ' = ' + value2);
                                        attrs.push({ key: key2, value: value2 });
                                    }
                                }
                            }
                        }
                        
                        console.log('[Hepsiburada JS] Total attributes extracted: ' + attrs.length);
                        return JSON.stringify(attrs);
                    ");
                    
                    if (jsData != null && !string.IsNullOrWhiteSpace(jsData.ToString()))
                    {
                        using var doc = JsonDocument.Parse(jsData.ToString()!);
                        int jsCount = 0;
                        foreach (var attr in doc.RootElement.EnumerateArray())
                        {
                            if (attr.TryGetProperty("key", out var keyElem) && 
                                attr.TryGetProperty("value", out var valueElem))
                            {
                                var key = keyElem.GetString();
                                var value = valueElem.GetString();
                                
                                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                                {
                                    // Clean up whitespace
                                    key = Regex.Replace(key, @"\s+", " ").Trim();
                                    value = Regex.Replace(value, @"\s+", " ").Trim();
                                    
                                    // Skip if it's the end marker
                                    if (key.Contains("Hatalý", StringComparison.OrdinalIgnoreCase) ||
                                        key.Contains("içerik", StringComparison.OrdinalIgnoreCase) ||
                                        key.Contains("bildir", StringComparison.OrdinalIgnoreCase))
                                    {
                                        break;
                                    }
                                    
                                    if (!product.Attributes.ContainsKey(key))
                                    {
                                        product.Attributes[key] = value;
                                        jsCount++;
                                    }
                                }
                            }
                        }
                    }
                    
                    // Get updated HTML after lazy loading
                    var updatedHtml = _driver.PageSource;
                    htmlDoc = new HtmlDocument();
                    htmlDoc.LoadHtml(updatedHtml);
                }
                catch (Exception jsEx)
                {
                }
            }
            
            // Method 2: HTML parsing with specific Hepsiburada classes
            if (product.Attributes.Count == 0)
            {
                
                // Try with partial class matching using contains
                var attributeContainers = htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'jkj4C4LML4qv2Iq8GkL3')]");
                
                if (attributeContainers != null && attributeContainers.Count > 0)
                {
                    int htmlCount = 0;
                    foreach (var container in attributeContainers)
                    {
                        try
                        {
                            // Get key from div with class containing OXP5AzPvafgN_i3y6wGp
                            var keyDiv = container.SelectSingleNode(".//div[contains(@class, 'OXP5AzPvafgN_i3y6wGp')]");
                            if (keyDiv == null) continue;
                            
                            var key = Regex.Replace(keyDiv.InnerText.Trim(), @"\s+", " ");
                            
                            // Stop at end marker
                            if (key.Contains("Hatalý", StringComparison.OrdinalIgnoreCase) ||
                                key.Contains("içerik", StringComparison.OrdinalIgnoreCase) ||
                                key.Contains("bildir", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }
                            
                            // Get value from div with class containing AxM3TmSghcDRH1F871Vh
                            var valueDiv = container.SelectSingleNode(".//div[contains(@class, 'AxM3TmSghcDRH1F871Vh')]");
                            if (valueDiv == null) continue;
                            
                            // Try to get span first, fallback to div text
                            var valueSpan = valueDiv.SelectSingleNode(".//span");
                            var value = valueSpan != null 
                                ? Regex.Replace(valueSpan.InnerText.Trim(), @"\s+", " ")
                                : Regex.Replace(valueDiv.InnerText.Trim(), @"\s+", " ");
                            
                            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value) && !product.Attributes.ContainsKey(key))
                            {
                                product.Attributes[key] = value;
                                htmlCount++;
                            }
                        }
                        catch { }
                    }
                }
                else
                {
                }
            }
            
            // Method 3: Fallback to table parsing (old structure)
            if (product.Attributes.Count == 0)
            {
                var attributeRows = htmlDoc.DocumentNode.SelectNodes("//table//tr[.//td[2]]");
                
                if (attributeRows != null && attributeRows.Count > 0)
                {
                    int tableCount = 0;
                    foreach (var row in attributeRows)
                    {
                        try
                        {
                            var cells = row.SelectNodes(".//td");
                            if (cells != null && cells.Count >= 2)
                            {
                                var key = Regex.Replace(cells[0].InnerText.Trim(), @"\s+", " ");
                                var value = Regex.Replace(cells[1].InnerText.Trim(), @"\s+", " ");
                                
                                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value) && !product.Attributes.ContainsKey(key))
                                {
                                    product.Attributes[key] = value;
                                    tableCount++;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            
            // Method 4: Parse definition lists (another fallback)
            if (product.Attributes.Count == 0)
            {
                var dtElements = htmlDoc.DocumentNode.SelectNodes("//dt");
                if (dtElements != null)
                {
                    int dtCount = 0;
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
                                
                                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value) && !product.Attributes.ContainsKey(key))
                                {
                                    product.Attributes[key] = value;
                                    dtCount++;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            if (product.Attributes.Count > 0)
            {
            }
            else
            {
            }
        }
        catch (Exception ex)
        {
        }
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// Converts Hepsiburada image URL to high resolution (1000x1000)
    /// Example: https://productimages.hepsiburada.net/s/777/424-600/110000936663290.jpg
    ///       -> https://productimages.hepsiburada.net/s/777/1000-1000/110000936663290.jpg
    /// Also handles: /s/777/375/... -> /s/777/1000-1000/...
    /// </summary>
    private string ConvertToHighResImage(string imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return imageUrl;
        
        // Pattern 1: /s/{number}/{dimensions like 424-600}/{imageid}.jpg
        // Replace dimensions like 424-600, 48-64, 222-222, etc. with 1000-1000
        var pattern1 = @"/s/(\d+)/(\d+-\d+)/";
        var result = Regex.Replace(imageUrl, pattern1, "/s/$1/1000-1000/");
        
        // Pattern 2: /s/{number}/{single number like 375}/{imageid}
        // Replace single dimension numbers (2-4 digits) with 1000-1000
        var pattern2 = @"/s/(\d+)/(\d{2,4})/";
        result = Regex.Replace(result, pattern2, "/s/$1/1000-1000/");
        
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
