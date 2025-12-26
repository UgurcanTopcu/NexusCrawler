using HtmlAgilityPack;
using Scrapper.Models;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using OpenQA.Selenium.Interactions;

namespace Scrapper.Services;

/// <summary>
/// Scraper for Akakce product pages - extracts product info and all seller listings
/// Uses Chrome with persistent profile and advanced anti-detection to bypass Cloudflare
/// </summary>
public class AkakceScraper : IDisposable
{
    private readonly HttpClient _httpClient;
    private IWebDriver? _driver;
    private const string BaseUrl = "https://www.akakce.com";
    private static readonly string UserDataDir = Path.Combine(Path.GetTempPath(), "AkakceChromeProfile");
    private static readonly Random _random = new Random();
    private int _productsScrapedSinceLastChallenge = 0;
    
    // Minimum delay between product page loads (in seconds)
    private const int MIN_DELAY_BETWEEN_PRODUCTS = 5;
    private const int MAX_DELAY_BETWEEN_PRODUCTS = 10;
    
    // User agent rotation list - realistic Chrome versions
    private static readonly string[] UserAgents = new[]
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
    };
    
    public ScrapeMethod Method { get; set; } = ScrapeMethod.Selenium;

    public AkakceScraper()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", GetRandomUserAgent());
    }

    private static string GetRandomUserAgent() => UserAgents[_random.Next(UserAgents.Length)];
    
    private static async Task RandomDelay(int minMs = 500, int maxMs = 1500)
    {
        await Task.Delay(_random.Next(minMs, maxMs));
    }

    private void InitializeDriver()
    {
        if (_driver == null)
        {
            Console.WriteLine("[Akakce] Initializing Chrome with enhanced anti-detection...");
            Console.WriteLine($"[Akakce] Profile directory: {UserDataDir}");
            
            // Create profile directory if it doesn't exist
            if (!Directory.Exists(UserDataDir))
            {
                Directory.CreateDirectory(UserDataDir);
            }
            
            var options = new ChromeOptions();
            
            // Use persistent profile - this helps bypass Cloudflare after first manual verification
            options.AddArgument($"--user-data-dir={UserDataDir}");
            options.AddArgument("--profile-directory=Default");
            
            // CRITICAL: NOT headless - required for Cloudflare bypass
            // Headless mode is easily detected by Cloudflare
            
            // Window and display settings
            options.AddArgument("--window-size=1920,1080");
            options.AddArgument("--start-maximized");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            
            // === ANTI-DETECTION MEASURES ===
            
            // 1. Disable automation flags
            options.AddArgument("--disable-blink-features=AutomationControlled");
            options.AddExcludedArgument("enable-automation");
            options.AddAdditionalOption("useAutomationExtension", false);
            
            // 2. Disable infobars and automation notifications
            options.AddArgument("--disable-infobars");
            options.AddArgument("--disable-notifications");
            
            // 3. Use random realistic user agent
            var userAgent = GetRandomUserAgent();
            options.AddArgument($"user-agent={userAgent}");
            Console.WriteLine($"[Akakce] Using User-Agent: {userAgent.Substring(0, 60)}...");
            
            // 4. Disable extensions and default apps (cleaner fingerprint)
            options.AddArgument("--disable-extensions");
            options.AddArgument("--disable-default-apps");
            options.AddArgument("--disable-component-update");
            options.AddArgument("--no-first-run");
            options.AddArgument("--no-default-browser-check");
            
            // 5. Enable features that real browsers have
            options.AddArgument("--enable-features=NetworkService,NetworkServiceInProcess");
            
            // 6. Set language to Turkish (matches Akakce)
            options.AddArgument("--lang=tr-TR");
            options.AddUserProfilePreference("intl.accept_languages", "tr-TR,tr,en-US,en");
            
            // 7. Disable password manager and other obvious automation tells
            options.AddUserProfilePreference("credentials_enable_service", false);
            options.AddUserProfilePreference("profile.password_manager_enabled", false);
            
            // 8. Suppress logging
            options.AddArgument("--log-level=3");
            options.AddArgument("--silent");
            
            var service = ChromeDriverService.CreateDefaultService();
            service.SuppressInitialDiagnosticInformation = true;
            service.HideCommandPromptWindow = true;
            
            try
            {
                _driver = new ChromeDriver(service, options, TimeSpan.FromMinutes(3));
                
                // Set page load timeout
                _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);
                _driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);
                
                // CRITICAL: Inject anti-detection scripts BEFORE any navigation
                InjectAntiDetectionScripts();
                
                Console.WriteLine("[Akakce] ? Chrome driver initialized with anti-detection");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Akakce] ? Error initializing Chrome: {ex.Message}");
                Console.WriteLine("[Akakce] TIP: Close any existing Chrome windows and try again");
                throw;
            }
        }
    }

    /// <summary>
    /// Inject comprehensive anti-detection JavaScript
    /// </summary>
    private void InjectAntiDetectionScripts()
    {
        if (_driver == null) return;
        
        try
        {
            var chromeDriver = (ChromeDriver)_driver;
            
            // Comprehensive anti-detection script
            var antiDetectionScript = @"
                // 1. Remove webdriver property
                Object.defineProperty(navigator, 'webdriver', {
                    get: () => undefined,
                    configurable: true
                });
                
                // 2. Mock plugins array (real browsers have plugins)
                Object.defineProperty(navigator, 'plugins', {
                    get: () => {
                        const plugins = [
                            { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer', description: 'Portable Document Format' },
                            { name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai', description: '' },
                            { name: 'Native Client', filename: 'internal-nacl-plugin', description: '' }
                        ];
                        plugins.length = 3;
                        return plugins;
                    },
                    configurable: true
                });
                
                // 3. Mock languages
                Object.defineProperty(navigator, 'languages', {
                    get: () => ['tr-TR', 'tr', 'en-US', 'en'],
                    configurable: true
                });
                
                // 4. Mock platform
                Object.defineProperty(navigator, 'platform', {
                    get: () => 'Win32',
                    configurable: true
                });
                
                // 5. Add chrome object (present in real Chrome)
                window.chrome = {
                    runtime: {},
                    loadTimes: function() { return {}; },
                    csi: function() { return {}; },
                    app: {}
                };
                
                // 6. Mock permissions
                const originalQuery = window.navigator.permissions.query;
                window.navigator.permissions.query = (parameters) => (
                    parameters.name === 'notifications' ?
                        Promise.resolve({ state: Notification.permission }) :
                        originalQuery(parameters)
                );
                
                // 7. Mock maxTouchPoints (some bots have 0)
                Object.defineProperty(navigator, 'maxTouchPoints', {
                    get: () => 1,
                    configurable: true
                });
                
                // 8. Mock hardware concurrency (real value, not 0)
                Object.defineProperty(navigator, 'hardwareConcurrency', {
                    get: () => 8,
                    configurable: true
                });
                
                // 9. Mock device memory
                Object.defineProperty(navigator, 'deviceMemory', {
                    get: () => 8,
                    configurable: true
                });
                
                console.log('[Anti-Detection] Scripts injected successfully');
            ";
            
            // Execute via CDP for earlier injection
            chromeDriver.ExecuteCdpCommand(
                "Page.addScriptToEvaluateOnNewDocument",
                new Dictionary<string, object> { ["source"] = antiDetectionScript }
            );
            
            Console.WriteLine("[Akakce] ? Anti-detection scripts injected via CDP");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] ? CDP injection failed: {ex.Message}");
            
            // Fallback: Direct JavaScript injection
            try
            {
                var jsExecutor = (IJavaScriptExecutor)_driver;
                jsExecutor.ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");
                Console.WriteLine("[Akakce] ? Fallback anti-detection applied");
            }
            catch { }
        }
    }

    /// <summary>
    /// Simulate human-like mouse movements and behavior
    /// </summary>
    private async Task SimulateHumanBehavior(bool extensive = false)
    {
        if (_driver == null) return;
        
        try
        {
            var actions = new Actions(_driver);
            var jsExecutor = (IJavaScriptExecutor)_driver;
            
            // Get viewport dimensions
            var viewportWidth = Convert.ToInt32(jsExecutor.ExecuteScript("return window.innerWidth") ?? 1920);
            var viewportHeight = Convert.ToInt32(jsExecutor.ExecuteScript("return window.innerHeight") ?? 1080);
            
            int iterations = extensive ? 5 : 2;
            
            for (int i = 0; i < iterations; i++)
            {
                // Random mouse movements
                var x = _random.Next(50, Math.Max(51, viewportWidth - 100));
                var y = _random.Next(50, Math.Max(51, viewportHeight - 100));
                
                try
                {
                    // Move mouse in small increments (more human-like)
                    actions.MoveByOffset(_random.Next(-30, 30), _random.Next(-30, 30)).Perform();
                    await Task.Delay(_random.Next(100, 300));
                }
                catch { }
            }
            
            // Random scroll (more human-like browsing)
            var scrollAmount = _random.Next(100, 400);
            jsExecutor.ExecuteScript($"window.scrollBy(0, {scrollAmount})");
            await Task.Delay(_random.Next(300, 600));
            
            // Scroll back partially
            jsExecutor.ExecuteScript($"window.scrollBy(0, -{scrollAmount / 2})");
            await Task.Delay(_random.Next(200, 400));
            
            if (extensive)
            {
                // Extra scrolling for extensive simulation
                jsExecutor.ExecuteScript("window.scrollTo(0, 0)");
                await Task.Delay(_random.Next(500, 1000));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Human simulation skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to click the Cloudflare Turnstile checkbox
    /// </summary>
    private async Task<bool> TryClickTurnstileCheckbox()
    {
        if (_driver == null) return false;
        
        try
        {
            Console.WriteLine("[Akakce] Looking for Turnstile checkbox...");
            
            // Wait a bit for the checkbox to be clickable
            await Task.Delay(2000);
            
            // Simulate human-like behavior before clicking
            await SimulateHumanBehavior(true);
            
            var jsExecutor = (IJavaScriptExecutor)_driver;
            
            // Try to find and click the Turnstile checkbox via JavaScript
            var clicked = jsExecutor.ExecuteScript(@"
                // Method 1: Look for iframe with Turnstile
                var iframes = document.querySelectorAll('iframe');
                for (var i = 0; i < iframes.length; i++) {
                    var iframe = iframes[i];
                    if (iframe.src && iframe.src.includes('challenges.cloudflare.com')) {
                        console.log('Found Turnstile iframe');
                        // Can't click inside iframe due to cross-origin, but we can try
                        try {
                            iframe.contentDocument.querySelector('input[type=checkbox]').click();
                            return true;
                        } catch(e) {
                            console.log('Cannot access iframe content');
                        }
                    }
                }
                
                // Method 2: Look for any checkbox with cf- in class or id
                var checkboxes = document.querySelectorAll('input[type=checkbox]');
                for (var j = 0; j < checkboxes.length; j++) {
                    var cb = checkboxes[j];
                    if (cb.id.includes('cf') || cb.className.includes('cf')) {
                        cb.click();
                        return true;
                    }
                }
                
                // Method 3: Click on the challenge container
                var container = document.querySelector('[class*=""challenge""]') || 
                               document.querySelector('[id*=""turnstile""]') ||
                               document.querySelector('[class*=""cf-turnstile""]');
                if (container) {
                    container.click();
                    return true;
                }
                
                return false;
            ");
            
            if (clicked != null && (bool)clicked)
            {
                Console.WriteLine("[Akakce] ? Clicked Turnstile element");
                await Task.Delay(3000); // Wait for verification
                return true;
            }
            
            // Try using Selenium to find and click
            try
            {
                // Look for the checkbox label or container
                var elements = _driver.FindElements(By.CssSelector("label, [class*='checkbox'], [class*='challenge']"));
                foreach (var element in elements)
                {
                    try
                    {
                        var text = element.Text.ToLower();
                        if (text.Contains("human") || text.Contains("verify") || text.Contains("robot"))
                        {
                            // Move to element first
                            var actions = new Actions(_driver);
                            actions.MoveToElement(element).Perform();
                            await Task.Delay(500);
                            
                            element.Click();
                            Console.WriteLine("[Akakce] ? Clicked verify element");
                            await Task.Delay(3000);
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            
            Console.WriteLine("[Akakce] Could not find Turnstile checkbox - manual action may be required");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Turnstile click error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Wait for Cloudflare challenge to complete with human behavior simulation
    /// </summary>
    private async Task<bool> WaitForCloudflareWithHumanBehavior(int maxWaitSeconds = 90)
    {
        if (_driver == null) return false;
        
        Console.WriteLine("[Akakce] Checking for Cloudflare challenge...");
        
        var startTime = DateTime.Now;
        bool wasCloudflare = false;
        bool turnstileClickAttempted = false;
        
        while ((DateTime.Now - startTime).TotalSeconds < maxWaitSeconds)
        {
            var title = _driver.Title ?? "";
            var pageSource = "";
            try { pageSource = _driver.PageSource ?? ""; } catch { }
            
            // Check for various Cloudflare indicators
            bool isCloudflare = 
                title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Security Check", StringComparison.OrdinalIgnoreCase) ||
                pageSource.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
                pageSource.Contains("cf_chl_opt", StringComparison.OrdinalIgnoreCase) ||
                pageSource.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
                pageSource.Contains("Verify you are human", StringComparison.OrdinalIgnoreCase);
            
            // Check specifically for Turnstile checkbox
            bool hasTurnstile = pageSource.Contains("turnstile", StringComparison.OrdinalIgnoreCase) ||
                               pageSource.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase) ||
                               pageSource.Contains("Verify you are human", StringComparison.OrdinalIgnoreCase);
            
            if (isCloudflare || hasTurnstile)
            {
                wasCloudflare = true;
                var elapsed = (int)(DateTime.Now - startTime).TotalSeconds;
                
                // Try to click Turnstile checkbox once
                if (hasTurnstile && !turnstileClickAttempted)
                {
                    turnstileClickAttempted = true;
                    Console.WriteLine("[Akakce] ?? Turnstile challenge detected - attempting to solve...");
                    
                    if (await TryClickTurnstileCheckbox())
                    {
                        await Task.Delay(5000); // Wait for verification to complete
                        continue; // Check again
                    }
                    else
                    {
                        Console.WriteLine("[Akakce] ? Please click the 'Verify you are human' checkbox manually...");
                    }
                }
                
                if (elapsed % 10 == 0)
                {
                    Console.WriteLine($"[Akakce] ? Waiting for Cloudflare... ({elapsed}s)");
                    
                    // Simulate human behavior while waiting
                    await SimulateHumanBehavior();
                }
                
                await Task.Delay(1000);
            }
            else if (title.Length > 5 && !title.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                if (wasCloudflare)
                {
                    Console.WriteLine($"[Akakce] ? Cloudflare challenge passed! (took {(int)(DateTime.Now - startTime).TotalSeconds}s)");
                    _productsScrapedSinceLastChallenge = 0; // Reset counter
                }
                else
                {
                    Console.WriteLine("[Akakce] ? No Cloudflare challenge detected");
                }
                return true;
            }
            else
            {
                await Task.Delay(500);
            }
        }
        
        Console.WriteLine($"[Akakce] ? Cloudflare challenge timeout after {maxWaitSeconds}s");
        Console.WriteLine("[Akakce] TIP: Solve the CAPTCHA manually, then the scraping will continue");
        return false;
    }

    /// <summary>
    /// Navigate to URL with retry logic and human behavior
    /// </summary>
    private async Task<bool> NavigateWithRetry(string url, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Console.WriteLine($"[Akakce] Loading: {url.Substring(0, Math.Min(80, url.Length))}... (attempt {attempt}/{maxRetries})");
                
                // Add significant delay between products to avoid triggering Cloudflare
                if (_productsScrapedSinceLastChallenge > 0)
                {
                    var delaySeconds = _random.Next(MIN_DELAY_BETWEEN_PRODUCTS, MAX_DELAY_BETWEEN_PRODUCTS);
                    Console.WriteLine($"[Akakce] ?? Waiting {delaySeconds}s before loading...");
                    await Task.Delay(delaySeconds * 1000);
                }
                
                // Simulate human behavior before navigation
                if (attempt > 1 || _productsScrapedSinceLastChallenge > 0)
                {
                    await SimulateHumanBehavior(true);
                }
                
                // Add random delay on retry
                if (attempt > 1)
                {
                    var retryDelay = _random.Next(5000, 10000) * attempt;
                    Console.WriteLine($"[Akakce] Retry delay: {retryDelay / 1000}s...");
                    await Task.Delay(retryDelay);
                }
                
                _driver!.Navigate().GoToUrl(url);
                await RandomDelay(2000, 4000);
                
                // Wait for Cloudflare with human behavior
                if (await WaitForCloudflareWithHumanBehavior())
                {
                    // Additional human-like behavior after page load
                    await SimulateHumanBehavior();
                    _productsScrapedSinceLastChallenge++;
                    return true;
                }
            }
            catch (WebDriverTimeoutException)
            {
                Console.WriteLine($"[Akakce] Page load timeout on attempt {attempt}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Akakce] Navigation error: {ex.Message}");
            }
        }
        
        return false;
    }

    /// <summary>
    /// Scrape a single Akakce product page
    /// </summary>
    public async Task<AkakceProductInfo> ScrapeProductAsync(string productUrl)
    {
        var product = new AkakceProductInfo
        {
            ProductUrl = productUrl,
            ScrapedAt = DateTime.Now
        };

        try
        {
            // Extract product ID from URL
            var idMatch = Regex.Match(productUrl, @",(\d+)\.html$");
            if (idMatch.Success)
            {
                product.ProductId = idMatch.Groups[1].Value;
                Console.WriteLine($"[Akakce] Product ID: {product.ProductId}");
            }
            else
            {
                Console.WriteLine($"[Akakce] WARNING: Could not extract product ID from URL: {productUrl}");
                product.ErrorMessage = "Invalid product URL format";
                return product;
            }

            InitializeDriver();
            
            // Navigate with retry logic
            if (!await NavigateWithRetry(productUrl))
            {
                product.ErrorMessage = "Failed to load page after retries (Cloudflare block)";
                product.Name = "Cloudflare Blocked";
                return product;
            }
            
            // Scroll to trigger lazy loading
            Console.WriteLine("[Akakce] Loading seller list...");
            var jsExecutor = (IJavaScriptExecutor)_driver!;
            
            for (int i = 1; i <= 5; i++)
            {
                jsExecutor.ExecuteScript($"window.scrollTo(0, document.body.scrollHeight * {i * 0.2});");
                await RandomDelay(300, 600);
            }
            
            jsExecutor.ExecuteScript("window.scrollTo(0, 0);");
            await RandomDelay(400, 800);
            
            var html = _driver.PageSource;
            Console.WriteLine($"[Akakce] Page loaded. Title: {_driver.Title}");
            
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Extract product details from page
            await ExtractProductDetails(htmlDoc, html, product);
            
            // Extract sellers using JavaScript execution on the page
            await ExtractSellersViaSelenium(product, html);

            if (product.Sellers.Count > 0)
            {
                Console.WriteLine($"[Akakce] ? SUCCESS: {product.Name}");
                Console.WriteLine($"[Akakce]   Sellers: {product.SellerCount} | Range: {product.LowestPrice} - {product.HighestPrice}");
            }
            else
            {
                Console.WriteLine($"[Akakce] ? No sellers found for: {product.Name}");
                product.ErrorMessage = "No sellers extracted";
            }
        }
        catch (Exception ex)
        {
            product.ErrorMessage = ex.Message;
            Console.WriteLine($"[Akakce] ERROR: {ex.Message}");
        }

        return product;
    }

    private async Task ExtractProductDetails(HtmlDocument htmlDoc, string html, AkakceProductInfo product)
    {
        try
        {
            var titleNode = htmlDoc.DocumentNode.SelectSingleNode("//title");
            if (titleNode != null)
            {
                var title = System.Net.WebUtility.HtmlDecode(titleNode.InnerText.Trim());
                if (!title.Contains("Just a moment"))
                {
                    if (title.Contains(" | ")) title = title.Split(" | ")[0].Trim();
                    if (title.Contains(" Fiyatlarý")) title = title.Split(" Fiyatlarý")[0].Trim();
                    product.Name = title;
                }
            }

            var ogImage = htmlDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
            if (ogImage != null)
            {
                var imgUrl = ogImage.GetAttributeValue("content", "");
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    if (imgUrl.StartsWith("//")) imgUrl = "https:" + imgUrl;
                    product.ImageUrl = imgUrl;
                }
            }
        }
        catch { }
        await Task.CompletedTask;
    }

    private async Task ExtractSellersViaSelenium(AkakceProductInfo product, string html)
    {
        if (_driver == null) return;

        try
        {
            var jsExecutor = (IJavaScriptExecutor)_driver;
            
            // Method 1: Extract from JSON-LD structured data (most reliable)
            // The JSON-LD contains: "seller":{"@type":"Organization","name":"Marketplace/SellerName"}
            Console.WriteLine("[Akakce] Extracting seller data from JSON-LD...");
            var jsonLdData = jsExecutor.ExecuteScript(@"
                var results = [];
                var scripts = document.querySelectorAll('script[type=""application/ld+json""]');
                
                scripts.forEach(function(script) {
                    try {
                        var data = JSON.parse(script.textContent);
                        
                        // Check for offers array in Product schema
                        if (data['@type'] === 'Product' && data.offers) {
                            var offersData = data.offers;
                            var offersList = offersData.offers || (offersData['@type'] === 'Offer' ? [offersData] : []);
                            
                            offersList.forEach(function(offer) {
                                if (offer.price && offer.seller && offer.seller.name) {
                                    var fullName = offer.seller.name;
                                    var marketplace = '';
                                    var sellerName = '';
                                    
                                    // Split by '/' to get Marketplace/SellerName
                                    var slashIndex = fullName.indexOf('/');
                                    if (slashIndex > 0) {
                                        marketplace = fullName.substring(0, slashIndex).trim();
                                        sellerName = fullName.substring(slashIndex + 1).trim();
                                    } else {
                                        // No slash means marketplace IS the seller
                                        marketplace = fullName.trim();
                                        sellerName = ''; // Leave empty when no sub-seller
                                    }
                                    
                                    results.push({
                                        price: parseFloat(offer.price),
                                        marketplace: marketplace,
                                        sellerName: sellerName,
                                        url: offer.url || ''
                                    });
                                }
                            });
                        }
                    } catch(e) {}
                });
                
                return JSON.stringify(results);
            ");
            
            if (jsonLdData != null && jsonLdData.ToString() != "[]" && jsonLdData.ToString() != "null")
            {
                var count = CountJsonArray(jsonLdData.ToString()!);
                Console.WriteLine($"[Akakce] Found {count} sellers in JSON-LD structured data");
                ParseJsonLdPrices(jsonLdData.ToString()!, product);
                
                // Enrich with DOM data if we have sellers but missing names
                if (product.Sellers.Count > 0)
                {
                    await EnrichSellerNamesViaDom(product);
                    return;
                }
            }
            
            // Method 2: Try qvPrices JavaScript variable
            Console.WriteLine("[Akakce] Trying qvPrices fallback...");
            var pricesJson = jsExecutor.ExecuteScript(@"
                if (typeof window.qvPrices !== 'undefined' && Array.isArray(window.qvPrices) && window.qvPrices.length > 0) {
                    var mapped = window.qvPrices.map(function(p) {
                        return {
                            price: p.price || 0,
                            vdCode: p.vdCode || '',
                            vdName: p.vdName || '',
                            badge: p.badge || '',
                            url: p.url || p.purl || ''
                        };
                    });
                    return JSON.stringify(mapped);
                }
                return null;
            ");
            
            if (pricesJson != null && !string.IsNullOrEmpty(pricesJson.ToString()) && pricesJson.ToString() != "null")
            {
                var count = CountJsonArray(pricesJson.ToString()!);
                Console.WriteLine($"[Akakce] Found {count} sellers in qvPrices");
                ParseQvPricesJson(pricesJson.ToString()!, product);
                
                // Enrich with DOM data since qvPrices lacks seller names
                if (product.Sellers.Count > 0)
                {
                    await EnrichSellerNamesViaDom(product);
                    return;
                }
            }
            
            // Method 3: DOM extraction fallback
            Console.WriteLine("[Akakce] Trying DOM extraction fallback...");
            var domPrices = jsExecutor.ExecuteScript(@"
                var results = [];
                var sellerItems = document.querySelectorAll('#APL li, ul.pl_v8 > li, ul.pl_v9 > li, li.p_w');
                
                sellerItems.forEach(function(item) {
                    var text = item.innerText || '';
                    
                    // Extract price
                    var priceMatch = text.match(/([0-9]{1,3}(?:\.[0-9]{3})*),(\d{2})\s*(?:TL)?/);
                    if (!priceMatch) return;
                    
                    var price = parseFloat(priceMatch[1].replace(/\./g, '') + '.' + priceMatch[2]);
                    if (price <= 0) return;
                    
                    // Get marketplace from logo alt
                    var marketplace = '';
                    var img = item.querySelector('img[alt]');
                    if (img && img.alt) {
                        marketplace = img.alt.trim();
                    }
                    
                    // Find seller name - look for '/SellerName' pattern
                    var sellerName = '';
                    
                    // Method 1: Find elements that start with '/'
                    var allElements = item.querySelectorAll('a span, a b, a > *');
                    for (var i = 0; i < allElements.length; i++) {
                        var el = allElements[i];
                        var elText = (el.textContent || '').trim();
                        
                        if (elText.startsWith('/') && elText.length > 1 && elText.length < 60) {
                            var candidate = elText.substring(1).trim();
                            candidate = candidate.split('\n')[0].trim();
                            candidate = candidate.split('Satýcýya')[0].trim();
                            candidate = candidate.split('Stokta')[0].trim();
                            candidate = candidate.split('Son güncelleme')[0].trim();
                            candidate = candidate.split('Kaçýrýlmayacak')[0].trim();
                            candidate = candidate.split('Bugün')[0].trim();
                            candidate = candidate.split('Kargo')[0].trim();
                            
                            if (candidate && candidate.length > 1 && candidate.length < 50 &&
                                !candidate.match(/^[0-9]/) && !candidate.includes('TL') &&
                                !candidate.includes(':') && !candidate.includes('gün')) {
                                sellerName = candidate;
                                break;
                            }
                        }
                    }
                    
                    // Method 2: Look at anchor text for '/' pattern
                    if (!sellerName) {
                        var anchors = item.querySelectorAll('a[href]');
                        for (var j = 0; j < anchors.length; j++) {
                            var anchorText = anchors[j].textContent || '';
                            var slashIdx = anchorText.indexOf('/');
                            if (slashIdx > 0 && slashIdx < anchorText.length - 1) {
                                var afterSlash = anchorText.substring(slashIdx + 1).trim();
                                afterSlash = afterSlash.split('\n')[0].trim();
                                afterSlash = afterSlash.split('Satýcýya')[0].trim();
                                afterSlash = afterSlash.split('Stokta')[0].trim();
                                afterSlash = afterSlash.split('Son güncelleme')[0].trim();
                                afterSlash = afterSlash.split('Kaçýrýlmayacak')[0].trim();
                                afterSlash = afterSlash.split('Bugün')[0].trim();
                                afterSlash = afterSlash.split('Kargo')[0].trim();
                                afterSlash = afterSlash.split(' iþ günü')[0].trim();
                                
                                if (afterSlash && afterSlash.length > 1 && afterSlash.length < 50 &&
                                    !afterSlash.match(/^[0-9]/) && !afterSlash.includes('TL') &&
                                    !afterSlash.includes(':') && !afterSlash.includes('gün') &&
                                    !afterSlash.includes('Fýrsatlar') && !afterSlash.includes('dakika')) {
                                    sellerName = afterSlash;
                                    break;
                                }
                            }
                        }
                    }
                    
                    // Get link
                    var linkUrl = '';
                    var linkEl = item.querySelector('a[href]');
                    if (linkEl) linkUrl = linkEl.href;
                    
                    if (marketplace) {
                        results.push({
                            price: price,
                            marketplace: marketplace,
                            sellerName: sellerName,
                            url: linkUrl
                        });
                    }
                });
                
                return JSON.stringify(results);
            ");
            
            if (domPrices != null && domPrices.ToString() != "[]")
            {
                Console.WriteLine($"[Akakce] DOM extraction found data");
                ParseDomPricesJson(domPrices.ToString()!, product);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Extraction error: {ex.Message}");
        }
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// Enriches seller data by extracting seller names from DOM
    /// Useful when JSON-LD or qvPrices provide marketplace but miss seller name
    /// </summary>
    private async Task EnrichSellerNamesViaDom(AkakceProductInfo product)
    {
        if (_driver == null) return;

        try
        {
            var jsExecutor = (IJavaScriptExecutor)_driver;
            
            // Extract seller names from DOM - looking specifically for the "/SellerName" pattern
            // In Akakce, the structure is: <img alt="marketplace"> followed by /sellerName as a direct text or in a specific element
            var sellerNamesJson = jsExecutor.ExecuteScript(@"
                var names = [];
                var items = document.querySelectorAll('#APL li, ul.pl_v8 > li, ul.pl_v9 > li, li.p_w');
                
                items.forEach(function(item) {
                    var sellerName = '';
                    var marketplace = '';
                    
                    // Get marketplace from image alt
                    var img = item.querySelector('img[alt]');
                    if (img && img.alt) {
                        marketplace = img.alt.trim().toLowerCase();
                    }
                    
                    // PRIMARY METHOD: Look for the seller info container that has the /name pattern
                    // The seller name appears right after the marketplace logo with a '/' prefix
                    // HTML structure often: <a>...<img alt='n11'>...<span>/btkurumsal</span>...</a>
                    
                    // Method 1: Find direct text nodes or spans that start with '/'
                    var allElements = item.querySelectorAll('a span, a b, a > *');
                    for (var i = 0; i < allElements.length; i++) {
                        var el = allElements[i];
                        var text = (el.textContent || '').trim();
                        
                        // Look for text that starts with '/' - this is the seller name indicator
                        if (text.startsWith('/') && text.length > 1 && text.length < 60) {
                            var candidate = text.substring(1).trim(); // Remove the leading '/'
                            
                            // Clean up: remove any trailing noise
                            candidate = candidate.split('\n')[0].trim();
                            candidate = candidate.split('Satýcýya')[0].trim();
                            candidate = candidate.split('Stokta')[0].trim();
                            candidate = candidate.split('Son güncelleme')[0].trim();
                            candidate = candidate.split('Kaçýrýlmayacak')[0].trim();
                            candidate = candidate.split('Bugün')[0].trim();
                            candidate = candidate.split('Kargo')[0].trim();
                            
                            // Validate: should be alphanumeric, not a price, not a date/time
                            if (candidate && 
                                candidate.length > 1 && 
                                candidate.length < 50 &&
                                !candidate.match(/^[0-9]/) &&
                                !candidate.includes('TL') &&
                                !candidate.includes(':') &&
                                !candidate.includes('gün') &&
                                !candidate.includes('adet')) {
                                sellerName = candidate;
                                break;
                            }
                        }
                    }
                    
                    // Method 2: If not found, look at anchor text for '/' pattern
                    if (!sellerName) {
                        var anchors = item.querySelectorAll('a[href]');
                        for (var j = 0; j < anchors.length; j++) {
                            var anchor = anchors[j];
                            var anchorText = anchor.textContent || '';
                            
                            // Find the '/' in the anchor text
                            var slashIdx = anchorText.indexOf('/');
                            if (slashIdx > 0 && slashIdx < anchorText.length - 1) {
                                var afterSlash = anchorText.substring(slashIdx + 1).trim();
                                
                                // Take first word/segment
                                afterSlash = afterSlash.split('\n')[0].trim();
                                afterSlash = afterSlash.split('Satýcýya')[0].trim();
                                afterSlash = afterSlash.split('Stokta')[0].trim();
                                afterSlash = afterSlash.split('Son güncelleme')[0].trim();
                                afterSlash = afterSlash.split('Kaçýrýlmayacak')[0].trim();
                                afterSlash = afterSlash.split('Bugün')[0].trim();
                                afterSlash = afterSlash.split('Kargo')[0].trim();
                                afterSlash = afterSlash.split(' iþ günü')[0].trim();
                                
                                // Validate
                                if (afterSlash && 
                                    afterSlash.length > 1 && 
                                    afterSlash.length < 50 &&
                                    !afterSlash.match(/^[0-9]/) &&
                                    !afterSlash.includes('TL') &&
                                    !afterSlash.includes(':') &&
                                    !afterSlash.includes('dakika') &&
                                    !afterSlash.includes('gün') &&
                                    !afterSlash.includes('adet') &&
                                    !afterSlash.includes('Fýrsatlar')) {
                                    sellerName = afterSlash;
                                    break;
                                }
                            }
                        }
                    }
                    
                    // Method 3: Last resort - check for span elements near the marketplace logo
                    if (!sellerName) {
                        var spans = item.querySelectorAll('span');
                        for (var k = 0; k < spans.length; k++) {
                            var span = spans[k];
                            var spanText = (span.textContent || '').trim();
                            var spanClass = (span.className || '').toLowerCase();
                            
                            // Skip known non-seller patterns
                            if (!spanText || spanText.length < 2 || spanText.length > 50) continue;
                            if (spanText.includes('TL') || spanText.includes('kargo')) continue;
                            if (spanText.includes('Satýcýya') || spanText.includes('Git')) continue;
                            if (spanText.includes('Stokta') || spanText.includes('adet')) continue;
                            if (spanText.includes('güncelleme') || spanText.includes('Bugün')) continue;
                            if (spanText.includes('Kaçýrýlmayacak') || spanText.includes('Fýrsatlar')) continue;
                            if (spanText.includes('En Ucuz') || spanText.includes('Dahil')) continue;
                            if (spanText.includes('iþ günü') || spanText.includes('dakika')) continue;
                            if (spanClass.includes('price') || spanClass.includes('btn')) continue;
                            if (spanText.match(/^[0-9]{1,2}:[0-9]{2}/)) continue; // Time pattern
                            if (spanText.match(/^[0-9]{1,3}[\.\,][0-9]{3}/)) continue; // Price pattern
                            
                            // Check if it starts with '/'
                            if (spanText.startsWith('/')) {
                                sellerName = spanText.substring(1).trim();
                                break;
                            }
                            
                            // Check if different from marketplace and looks like a name
                            if (spanText.toLowerCase() !== marketplace &&
                                spanText.match(/^[A-Za-z0-9ýðüþöçÝÐÜÞÖÇ]/)) {
                                // Could be seller name, but be cautious
                                // Only use if no better option
                            }
                        }
                    }
                    
                    names.push(sellerName);
                });
                
                return JSON.stringify(names);
            ");
            
            if (sellerNamesJson != null)
            {
                var names = System.Text.Json.JsonSerializer.Deserialize<List<string>>(sellerNamesJson.ToString()!);
                
                if (names != null && names.Count > 0)
                {
                    Console.WriteLine($"[Akakce] Found {names.Count} potential names in DOM for enrichment");
                    
                    int enrichedCount = 0;
                    int skippedCount = 0;
                    foreach (var seller in product.Sellers)
                    {
                        // Only enrich if SellerName is empty
                        if (string.IsNullOrEmpty(seller.SellerName) && seller.Rank <= names.Count)
                        {
                            var domName = names[seller.Rank - 1];
                            
                            // Validate the name - must not be noise
                            bool isValid = !string.IsNullOrWhiteSpace(domName) &&
                                !domName.Equals(seller.Marketplace, StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains("Satýcýya", StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains("Kaçýrýlmayacak", StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains("Fýrsatlar", StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains("güncelleme", StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains("En Ucuz", StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains("Kargo", StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains("Bugün", StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains("iþ günü", StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains("dakika", StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains("adet", StringComparison.OrdinalIgnoreCase) &&
                                !domName.Contains(":", StringComparison.OrdinalIgnoreCase) &&
                                domName.Length >= 2 &&
                                domName.Length < 50 &&
                                !Regex.IsMatch(domName, @"^\d");
                            
                            if (isValid)
                            {
                                seller.SellerName = domName;
                                enrichedCount++;
                                if (enrichedCount <= 3)
                                {
                                    Console.WriteLine($"[Akakce] Enriched Seller {seller.Rank}: {seller.Marketplace} -> {seller.SellerName}");
                                }
                            }
                            else
                            {
                                skippedCount++;
                            }
                        }
                    }
                    
                    if (enrichedCount > 3)
                    {
                        Console.WriteLine($"[Akakce] ... and {enrichedCount - 3} more sellers enriched");
                    }
                    if (skippedCount > 0)
                    {
                        Console.WriteLine($"[Akakce] Skipped {skippedCount} invalid/noise values");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Enrichment error: {ex.Message}");
        }
        
        await Task.CompletedTask;
    }

    private int CountJsonArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetArrayLength();
        }
        catch { return 0; }
    }

    private string FormatTurkishPrice(decimal price)
    {
        return price.ToString("N2", new System.Globalization.CultureInfo("tr-TR")) + " TL";
    }

    /// <summary>
    /// Parse JSON-LD structured data - most reliable source for Marketplace/SellerName
    /// </summary>
    private void ParseJsonLdPrices(string json, AkakceProductInfo product)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            int rank = 1;
            decimal? lowestPrice = null;
            decimal? highestPrice = null;
            int totalItems = doc.RootElement.GetArrayLength();

            Console.WriteLine($"[Akakce] Parsing {totalItems} sellers from JSON-LD...");

            foreach (var priceItem in doc.RootElement.EnumerateArray())
            {
                var seller = new AkakceSellerInfo
                {
                    Rank = rank,
                    ParentProductUrl = product.ProductUrl,
                    ParentProductId = product.ProductId,
                    ParentProductName = product.Name
                };

                // Get price
                if (priceItem.TryGetProperty("price", out var priceEl) && priceEl.TryGetDecimal(out var price))
                {
                    seller.Price = price;
                    seller.PriceFormatted = FormatTurkishPrice(price);
                    if (!lowestPrice.HasValue || price < lowestPrice) lowestPrice = price;
                    if (!highestPrice.HasValue || price > highestPrice) highestPrice = price;
                }

                // Get marketplace (before the slash)
                if (priceItem.TryGetProperty("marketplace", out var marketplaceEl))
                {
                    seller.Marketplace = marketplaceEl.GetString() ?? "";
                }
                
                // Get seller name (after the slash) - may be empty if no sub-seller
                if (priceItem.TryGetProperty("sellerName", out var sellerNameEl))
                {
                    seller.SellerName = sellerNameEl.GetString() ?? "";
                }
                
                // If seller name is empty, leave it empty (don't copy marketplace)
                // This is correct behavior - some listings don't have sub-sellers

                // Get URL
                if (priceItem.TryGetProperty("url", out var urlEl))
                {
                    var url = urlEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(url))
                    {
                        seller.ProductLink = url;
                    }
                }

                if (rank == 1) seller.Badges.Add("En Ucuz");
                seller.InStock = true;

                if (seller.Price > 0 && !string.IsNullOrEmpty(seller.Marketplace))
                {
                    product.Sellers.Add(seller);
                    
                    var displaySeller = string.IsNullOrEmpty(seller.SellerName) ? "(direct)" : seller.SellerName;
                    if (rank <= 5 || rank % 10 == 0 || rank == totalItems)
                    {
                        Console.WriteLine($"[Akakce] Seller {rank}/{totalItems}: {seller.Marketplace} / {displaySeller} - {seller.PriceFormatted}");
                    }
                    
                    rank++;
                }
            }

            product.SellerCount = product.Sellers.Count;
            if (lowestPrice.HasValue) product.LowestPrice = FormatTurkishPrice(lowestPrice.Value);
            if (highestPrice.HasValue) product.HighestPrice = FormatTurkishPrice(highestPrice.Value);
            
            Console.WriteLine($"[Akakce] ? Extracted {product.SellerCount} sellers from JSON-LD");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] JSON-LD parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse qvPrices JavaScript variable - only has marketplace (vdName), not seller
    /// </summary>
    private void ParseQvPricesJson(string json, AkakceProductInfo product)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            int rank = 1;
            decimal? lowestPrice = null;
            decimal? highestPrice = null;
            int totalItems = doc.RootElement.GetArrayLength();

            Console.WriteLine($"[Akakce] Parsing {totalItems} sellers from qvPrices...");

            foreach (var priceItem in doc.RootElement.EnumerateArray())
            {
                var seller = new AkakceSellerInfo
                {
                    Rank = rank,
                    ParentProductUrl = product.ProductUrl,
                    ParentProductId = product.ProductId,
                    ParentProductName = product.Name
                };

                // Get price
                if (priceItem.TryGetProperty("price", out var priceEl))
                {
                    if (priceEl.ValueKind == JsonValueKind.Number)
                    {
                        seller.Price = priceEl.GetDecimal();
                    }
                    else if (priceEl.ValueKind == JsonValueKind.String)
                    {
                        decimal.TryParse(priceEl.GetString()?.Replace(".", "").Replace(",", "."), out var p);
                        seller.Price = p;
                    }
                    
                    if (seller.Price > 0)
                    {
                        seller.PriceFormatted = FormatTurkishPrice(seller.Price);
                        if (!lowestPrice.HasValue || seller.Price < lowestPrice) lowestPrice = seller.Price;
                        if (!highestPrice.HasValue || seller.Price > highestPrice) highestPrice = seller.Price;
                    }
                }

                // Get marketplace from vdName
                // Note: qvPrices does NOT contain seller name, only marketplace
                string vdName = "";
                if (priceItem.TryGetProperty("vdName", out var vdNameEl))
                {
                    vdName = vdNameEl.GetString() ?? "";
                }
                
                seller.Marketplace = vdName;
                seller.SellerName = ""; // qvPrices doesn't have seller info, leave empty
                
                // Get badge
                if (priceItem.TryGetProperty("badge", out var badgeEl))
                {
                    var badge = badgeEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(badge))
                    {
                        seller.Badges.Add(badge);
                    }
                }

                // Get URL
                if (priceItem.TryGetProperty("url", out var urlEl))
                {
                    var url = urlEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(url))
                    {
                        if (url.StartsWith("//")) url = "https:" + url;
                        else if (url.StartsWith("/")) url = BaseUrl + url;
                        seller.ProductLink = url;
                    }
                }

                seller.InStock = true;

                if (seller.Price > 0 && !string.IsNullOrEmpty(seller.Marketplace))
                {
                    product.Sellers.Add(seller);
                    
                    if (rank <= 5 || rank % 10 == 0 || rank == totalItems)
                    {
                        Console.WriteLine($"[Akakce] Seller {rank}/{totalItems}: {seller.Marketplace} - {seller.PriceFormatted}");
                    }
                    
                    rank++;
                }
            }

            product.SellerCount = product.Sellers.Count;
            if (lowestPrice.HasValue) product.LowestPrice = FormatTurkishPrice(lowestPrice.Value);
            if (highestPrice.HasValue) product.HighestPrice = FormatTurkishPrice(highestPrice.Value);
            
            Console.WriteLine($"[Akakce] ? Extracted {product.SellerCount} sellers from qvPrices");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] qvPrices parse error: {ex.Message}");
        }
    }

    private void ParseDomPricesJson(string json, AkakceProductInfo product)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            int rank = 1;
            decimal? lowestPrice = null;
            decimal? highestPrice = null;
            int totalItems = doc.RootElement.GetArrayLength();

            Console.WriteLine($"[Akakce] Parsing {totalItems} DOM seller entries...");

            foreach (var priceItem in doc.RootElement.EnumerateArray())
            {
                var seller = new AkakceSellerInfo
                {
                    Rank = rank,
                    ParentProductUrl = product.ProductUrl,
                    ParentProductId = product.ProductId,
                    ParentProductName = product.Name
                };

                // Get price
                if (priceItem.TryGetProperty("price", out var priceEl) && priceEl.TryGetDecimal(out var price))
                {
                    seller.Price = price;
                    seller.PriceFormatted = FormatTurkishPrice(price);
                    if (!lowestPrice.HasValue || price < lowestPrice) lowestPrice = price;
                    if (!highestPrice.HasValue || price > highestPrice) highestPrice = price;
                }

                // Get marketplace
                if (priceItem.TryGetProperty("marketplace", out var marketplaceEl))
                {
                    seller.Marketplace = marketplaceEl.GetString() ?? "";
                }
                
                // Get seller name
                if (priceItem.TryGetProperty("sellerName", out var sellerNameEl))
                {
                    seller.SellerName = sellerNameEl.GetString() ?? "";
                }
                else
                {
                    seller.SellerName = "";
                }

                // Get URL
                if (priceItem.TryGetProperty("url", out var urlEl))
                {
                    var url = urlEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(url))
                    {
                        if (url.StartsWith("//")) url = "https:" + url;
                        else if (url.StartsWith("/")) url = BaseUrl + url;
                        seller.ProductLink = url;
                    }
                }

                if (rank == 1) seller.Badges.Add("En Ucuz");
                seller.InStock = true;

                if (seller.Price > 0 && !string.IsNullOrEmpty(seller.Marketplace))
                {
                    product.Sellers.Add(seller);
                    
                    if (rank <= 5 || rank % 10 == 0 || rank == totalItems)
                    {
                        Console.WriteLine($"[Akakce] DOM Seller {rank}/{totalItems}: {seller.Marketplace} - {seller.PriceFormatted}");
                    }
                    
                    rank++;
                }
            }

            product.SellerCount = product.Sellers.Count;
            if (lowestPrice.HasValue) product.LowestPrice = FormatTurkishPrice(lowestPrice.Value);
            if (highestPrice.HasValue) product.HighestPrice = FormatTurkishPrice(highestPrice.Value);
            
            Console.WriteLine($"[Akakce] ? Extracted {product.SellerCount} sellers from DOM");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] DOM JSON parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Extract product URLs from an Akakce category page with pagination support
    /// </summary>
    public async Task<List<string>> GetProductUrlsFromCategoryAsync(string categoryUrl, int maxProducts = 10, Func<int, string, string, Task>? onProgress = null)
    {
        var productUrls = new List<string>();
        
        try
        {
            Console.WriteLine($"[Akakce] Loading category page: {categoryUrl}");
            InitializeDriver();
            
            if (onProgress != null)
            {
                await onProgress(5, $"?? Loading category page...", "info");
            }
            
            // Navigate with retry logic
            if (!await NavigateWithRetry(categoryUrl))
            {
                if (onProgress != null)
                {
                    await onProgress(10, "? Page blocked by Cloudflare after retries", "error");
                }
                return productUrls;
            }
            
            int pageNumber = 1;
            int maxPages = 20; // Safety limit
            
            while (productUrls.Count < maxProducts && pageNumber <= maxPages)
            {
                Console.WriteLine($"[Akakce] Processing page {pageNumber}...");
                
                if (onProgress != null)
                {
                    await onProgress(10, $"?? Page {pageNumber}: Extracting URLs...", "info");
                }
                
                // Scroll to load more products on current page
                var jsExecutor = (IJavaScriptExecutor)_driver!;
                for (int i = 1; i <= 5; i++)
                {
                    jsExecutor.ExecuteScript($"window.scrollTo(0, document.body.scrollHeight * {i * 0.2});");
                    await RandomDelay(300, 600);
                }
                
                // Extract product URLs from current page
                var urlsJson = jsExecutor.ExecuteScript(@"
                    var urls = [];
                    var seen = {};
                    document.querySelectorAll('li[data-pr]').forEach(function(li) {
                        var links = li.querySelectorAll('a[href]');
                        links.forEach(function(a) {
                            var href = a.href;
                            if (href && href.match(/,\d+\.html$/)) {
                                if (!seen[href]) { seen[href] = true; urls.push(href); }
                            }
                        });
                    });
                    document.querySelectorAll('a[href*=""fiyati""]').forEach(function(a) {
                        var href = a.href;
                        if (href && href.match(/,\d+\.html$/) && !seen[href]) { seen[href] = true; urls.push(href); }
                    });
                    return JSON.stringify(urls);
                ");
                
                int urlsFoundOnPage = 0;
                
                if (urlsJson != null && !string.IsNullOrEmpty(urlsJson.ToString()))
                {
                    var urls = System.Text.Json.JsonSerializer.Deserialize<List<string>>(urlsJson.ToString()!);
                    
                    if (urls != null)
                    {
                        foreach (var url in urls)
                        {
                            if (productUrls.Count >= maxProducts) break;
                            
                            if (IsValidAkakceUrl(url) && !productUrls.Contains(url))
                            {
                                productUrls.Add(url);
                                urlsFoundOnPage++;
                                
                                Console.Write($"\r[Akakce] Found: {productUrls.Count}/{maxProducts} product URLs   ");
                                Console.Out.Flush();
                                
                                if (onProgress != null && (productUrls.Count % 10 == 0 || productUrls.Count == maxProducts))
                                {
                                    await onProgress(10, $"?? Found {productUrls.Count}/{maxProducts} product URLs", "info");
                                }
                            }
                        }
                    }
                }
                
                Console.WriteLine();
                Console.WriteLine($"[Akakce] Page {pageNumber}: Found {urlsFoundOnPage} new URLs (Total: {productUrls.Count})");
                
                if (productUrls.Count >= maxProducts)
                {
                    Console.WriteLine($"[Akakce] ? Reached target of {maxProducts} products!");
                    break;
                }
                
                // Try to find and navigate to next page
                bool hasNextPage = false;
                try
                {
                    var nextPageUrl = jsExecutor.ExecuteScript(@"
                        var nextLink = document.querySelector('a.p[title=""Sonraki""]') || document.querySelector('a[title=""Sonraki""]');
                        if (!nextLink) {
                            var links = document.querySelectorAll('a');
                            for (var i = 0; i < links.length; i++) {
                                if (links[i].textContent.trim() === 'Sonraki' || links[i].title === 'Sonraki') {
                                    nextLink = links[i]; break;
                                }
                            }
                        }
                        return nextLink ? nextLink.href : null;
                    ");
                    
                    if (nextPageUrl != null && !string.IsNullOrEmpty(nextPageUrl.ToString()))
                    {
                        var nextUrl = nextPageUrl.ToString()!;
                        Console.WriteLine($"[Akakce] Navigating to next page: {nextUrl}");
                        await Task.Delay(_random.Next(3000, 5000));
                        
                        if (await NavigateWithRetry(nextUrl, 2))
                        {
                            hasNextPage = true;
                            pageNumber++;
                        }
                        else
                        {
                            Console.WriteLine("[Akakce] Failed to load next page");
                            break;
                        }
                    }
                    else
                    {
                        Console.WriteLine("[Akakce] No next page found");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Akakce] Error navigating to next page: {ex.Message}");
                    break;
                }
            }
            
            Console.WriteLine($"[Akakce] ? Extracted {productUrls.Count} product URLs from {pageNumber} page(s)");
            
            if (onProgress != null)
            {
                if (productUrls.Count > 0)
                {
                    await onProgress(15, $"? Found {productUrls.Count} product URLs from {pageNumber} page(s)", "success");
                }
                else
                {
                    await onProgress(15, "? No product URLs found on page", "error");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Error extracting URLs: {ex.Message}");
            if (onProgress != null)
            {
                await onProgress(10, $"? Error: {ex.Message}", "error");
            }
        }
        
        return productUrls;
    }

    public static bool IsValidAkakceUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("akakce.com") && url.Contains(",") && url.EndsWith(".html");
    }

    public void Dispose()
    {
        if (_driver != null)
        {
            Console.WriteLine("[Akakce] Closing Chrome (profile saved for next time)...");
            try { _driver.Quit(); } catch { }
            try { _driver.Dispose(); } catch { }
            _driver = null;
        }
    }
}
