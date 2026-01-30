using HtmlAgilityPack;
using Scrapper.Models;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Support.UI;
using OpenQA.Selenium.Interactions;

namespace Scrapper.Services;

/// <summary>
/// Scraper for Akakce product pages - extracts product info and all seller listings
/// Uses Edge with the user's actual profile to bypass Cloudflare
/// </summary>
public class AkakceScraper : IDisposable
{
    private readonly HttpClient _httpClient;
    
    // STATIC driver - shared across all instances to prevent multiple Edge windows
    private static IWebDriver? _driver;
    private static readonly object _driverLock = new object();
    private static bool _initializationAttempted = false;
    
    private const string BaseUrl = "https://www.akakce.com";
    
    // User's real Edge profile (source for copying)
    private static readonly string SourceProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Edge", "User Data");
    
    // Separate profile for scraper (so we don't lock user's profile)
    private static readonly string ScraperProfileDir = Path.Combine(
        Path.GetTempPath(), "AkakceScraperProfile");
    
    private static readonly Random _random = new Random();
    private int _productsScrapedSinceLastChallenge = 0;
    private bool _cloudflareDetected = false;
    
    // Remote debugging port for connecting to existing Edge
    private const int DEBUGGING_PORT = 9222;

    
    

    
    
    // Delay settings - SHORT since using real profile (no Cloudflare)
    private const int MIN_DELAY_SEARCH = 1;
    private const int MAX_DELAY_SEARCH = 2;
    
    // Minimum delay between product page loads (in seconds)
    private const int MIN_DELAY_BETWEEN_PRODUCTS = 2;
    private const int MAX_DELAY_BETWEEN_PRODUCTS = 4;
    
    // Cloudflare mode delays (only used when Cloudflare is detected)
    private const int MIN_DELAY_CLOUDFLARE = 5;
    private const int MAX_DELAY_CLOUDFLARE = 10;
    
    // Cloudflare wait timeout (seconds)
    private const int CLOUDFLARE_WAIT_TIMEOUT = 30;
    
    // User agent rotation list - realistic Edge versions
    private static readonly string[] UserAgents = new[]
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36 Edg/129.0.0.0",
        "Mozilla/5.0 (Windows NT 11.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0"
    };
    
    public ScrapeMethod Method { get; set; } = ScrapeMethod.Selenium;

    public AkakceScraper()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", GetRandomUserAgent());
    }

    private static string GetRandomUserAgent() => UserAgents[_random.Next(UserAgents.Length)];
    
    /// <summary>
    /// Find the active/most recently used Edge profile name
    /// Returns: "Default", "Profile 1", "Profile 2", etc.
    /// </summary>
    private static string? FindActiveEdgeProfile()
    {
        try
        {
            if (!Directory.Exists(SourceProfileDir))
            {
                Console.WriteLine("[Akakce] Edge User Data directory not found");
                return null;
            }
            
            // Look for profile directories
            var profileDirs = new List<(string name, string path)>();
            
            // Check Default profile
            var defaultProfile = Path.Combine(SourceProfileDir, "Default");
            if (Directory.Exists(defaultProfile))
            {
                profileDirs.Add(("Default", defaultProfile));
            }
            
            // Check Profile 1, Profile 2, etc.
            for (int i = 1; i <= 10; i++)
            {
                var profilePath = Path.Combine(SourceProfileDir, $"Profile {i}");
                if (Directory.Exists(profilePath))
                {
                    profileDirs.Add(($"Profile {i}", profilePath));
                }
            }
            
            Console.WriteLine($"[Akakce] Found {profileDirs.Count} Edge profiles");
            
            // Find the profile with the most recent History file (indicates active use)
            string? bestProfile = null;
            DateTime latestTime = DateTime.MinValue;
            
            foreach (var (name, path) in profileDirs)
            {
                // Check History file (more reliable than cookies for recent use)
                var historyFile = Path.Combine(path, "History");
                if (File.Exists(historyFile))
                {
                    var lastWrite = File.GetLastWriteTime(historyFile);
                    Console.WriteLine($"[Akakce]   - {name}: Last used {lastWrite:g}");
                    
                    if (lastWrite > latestTime)
                    {
                        latestTime = lastWrite;
                        bestProfile = name;
                    }
                }
            }
            
            if (bestProfile != null)
            {
                Console.WriteLine($"[Akakce] ✓ Most recent profile: {bestProfile}");
            }
            
            return bestProfile;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Error finding profile: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Copy user's Edge profile to a separate directory for the scraper
    /// This allows Edge to run alongside while we use a copy of the profile
    /// </summary>
    private static void CopyProfileForScraper()
    {
        try
        {
            // Create scraper profile directory
            var scraperDefaultDir = Path.Combine(ScraperProfileDir, "Default");
            if (!Directory.Exists(scraperDefaultDir))
            {
                Directory.CreateDirectory(scraperDefaultDir);
                var networkDir = Path.Combine(scraperDefaultDir, "Network");
                Directory.CreateDirectory(networkDir);
            }
            
            // Find user's active profile
            string profileName = FindActiveEdgeProfile() ?? "Default";
            var sourceProfilePath = Path.Combine(SourceProfileDir, profileName);
            
            Console.WriteLine($"[Akakce] Copying profile from: {profileName}");
            
            // Copy cookies (most important for bypassing Cloudflare)
            var sourceCookies = Path.Combine(sourceProfilePath, "Network", "Cookies");
            var destCookies = Path.Combine(scraperDefaultDir, "Network", "Cookies");
            if (File.Exists(sourceCookies))
            {
                File.Copy(sourceCookies, destCookies, overwrite: true);
                Console.WriteLine("[Akakce] ✓ Copied cookies");
            }
            
            // Copy Local State (encryption keys)
            var sourceLocalState = Path.Combine(SourceProfileDir, "Local State");
            var destLocalState = Path.Combine(ScraperProfileDir, "Local State");
            if (File.Exists(sourceLocalState))
            {
                File.Copy(sourceLocalState, destLocalState, overwrite: true);
                Console.WriteLine("[Akakce] ✓ Copied Local State");
            }
            
            // Copy Preferences (site settings)
            var sourcePrefs = Path.Combine(sourceProfilePath, "Preferences");
            var destPrefs = Path.Combine(scraperDefaultDir, "Preferences");
            if (File.Exists(sourcePrefs))
            {
                File.Copy(sourcePrefs, destPrefs, overwrite: true);
                Console.WriteLine("[Akakce] ✓ Copied Preferences");
            }
            
            Console.WriteLine("[Akakce] ✓ Profile copied successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] ⚠️ Could not copy profile: {ex.Message}");
            Console.WriteLine("[Akakce] Will use fresh profile (may trigger Cloudflare)");
        }
    }
    
    private static async Task RandomDelay(int minMs = 500, int maxMs = 1500)
    {
        await Task.Delay(_random.Next(minMs, maxMs));
    }

    /// <summary>
    /// Warm up the browser by connecting to existing Edge or providing setup instructions.
    /// </summary>
    public async Task<bool> WarmupAsync(Func<int, string, string, Task>? onProgress = null)
    {
        try
        {
            Console.WriteLine("[Akakce] ═══════════════════════════════════════════════════════════════");
            Console.WriteLine("[Akakce] CONNECTING TO YOUR EDGE BROWSER");
            Console.WriteLine("[Akakce] ═══════════════════════════════════════════════════════════════");
            
            if (onProgress != null)
            {
                await onProgress(5, "🔄 Connecting to your Edge browser...", "info");
            }
            
            // Try to connect to existing Edge with debugging port
            bool connected = await TryConnectToExistingEdgeAsync();
            
            if (!connected)
            {
                // Show setup instructions
                Console.WriteLine("");
                Console.WriteLine("[Akakce] ❌ Could not connect to Edge with debugging port.");
                Console.WriteLine("");
                Console.WriteLine("[Akakce] ╔══════════════════════════════════════════════════════════════╗");
                Console.WriteLine("[Akakce] ║  SETUP REQUIRED (ONE TIME ONLY):                            ║");
                Console.WriteLine("[Akakce] ║                                                              ║");
                Console.WriteLine("[Akakce] ║  1. CLOSE ALL Edge windows completely                       ║");
                Console.WriteLine("[Akakce] ║                                                              ║");
                Console.WriteLine("[Akakce] ║  2. Press Win+R and paste this command:                     ║");
                Console.WriteLine("[Akakce] ║                                                              ║");
                Console.WriteLine("[Akakce] ║     msedge --remote-debugging-port=9222                     ║");
                Console.WriteLine("[Akakce] ║                                                              ║");
                Console.WriteLine("[Akakce] ║  3. Edge will open with YOUR profile                        ║");
                Console.WriteLine("[Akakce] ║                                                              ║");
                Console.WriteLine("[Akakce] ║  4. Go to https://www.akakce.com (no Cloudflare!)          ║");
                Console.WriteLine("[Akakce] ║                                                              ║");
                Console.WriteLine("[Akakce] ║  5. Come back here and click Start again                    ║");
                Console.WriteLine("[Akakce] ╚══════════════════════════════════════════════════════════════╝");
                Console.WriteLine("");
                
                if (onProgress != null)
                {
                    await onProgress(10, "❌ Setup required - see console for instructions", "error");
                }
                return false;
            }
            
            Console.WriteLine("[Akakce] ✅ Connected to your Edge browser!");
            
            // Navigate to Akakce to verify
            if (onProgress != null)
            {
                await onProgress(7, "⏳ Navigating to Akakce...", "info");
            }
            
            _driver!.Navigate().GoToUrl("https://www.akakce.com/");
            await Task.Delay(3000);
            
            var title = _driver.Title ?? "";
            Console.WriteLine($"[Akakce] Page title: {title}");
            
            // Check for Cloudflare
            bool isCloudflare = title.Contains("Bir dakika", StringComparison.OrdinalIgnoreCase) ||
                               title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                               string.IsNullOrWhiteSpace(title);
            
            if (isCloudflare)
            {
                Console.WriteLine("[Akakce] ⚠️ Cloudflare detected - this shouldn't happen with your profile!");
                Console.WriteLine("[Akakce] Try visiting https://www.akakce.com manually in Edge first.");
                if (onProgress != null)
                {
                    await onProgress(10, "⚠️ Cloudflare detected - visit akakce.com in Edge first", "warning");
                }
                return false;
            }
            
            Console.WriteLine("[Akakce] ✅ No Cloudflare - ready to search!");
            if (onProgress != null)
            {
                await onProgress(10, "✅ Connected to your Edge - ready to search!", "success");
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Warmup error: {ex.Message}");
            if (onProgress != null)
            {
                await onProgress(10, $"❌ Error: {ex.Message}", "error");
            }
            return false;
        }
    }
    
    /// <summary>
    /// Connect to an existing Edge browser that was started with --remote-debugging-port=9222
    /// </summary>
    private async Task<bool> TryConnectToExistingEdgeAsync()
    {
        lock (_driverLock)
        {
            // If we already have a working driver, use it
            if (_driver != null)
            {
                try
                {
                    var _ = _driver.WindowHandles;
                    Console.WriteLine("[Akakce] ✓ Reusing existing connection");
                    return true;
                }
                catch
                {
                    Console.WriteLine("[Akakce] Previous connection is dead");
                    try { _driver.Quit(); } catch { }
                    _driver = null;
                    _initializationAttempted = false;
                }
            }
            
            // Check if port 9222 is open (Edge with debugging is running)
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var result = client.BeginConnect("127.0.0.1", DEBUGGING_PORT, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                
                if (!success || !client.Connected)
                {
                    Console.WriteLine($"[Akakce] Port {DEBUGGING_PORT} is not open - Edge not running with debugging");
                    return false;
                }
                client.Close();
            }
            catch
            {
                Console.WriteLine($"[Akakce] Cannot connect to port {DEBUGGING_PORT}");
                return false;
            }
            
            // Port is open, try to connect with Selenium
            try
            {
                Console.WriteLine($"[Akakce] Port {DEBUGGING_PORT} is open, connecting...");
                
                var options = new EdgeOptions();
                options.DebuggerAddress = $"127.0.0.1:{DEBUGGING_PORT}";
                
                var service = EdgeDriverService.CreateDefaultService();
                service.SuppressInitialDiagnosticInformation = true;
                service.HideCommandPromptWindow = true;
                
                _driver = new EdgeDriver(service, options, TimeSpan.FromSeconds(10));
                _initializationAttempted = true;
                
                // Verify connection
                var handles = _driver.WindowHandles;
                Console.WriteLine($"[Akakce] ✅ Connected! Found {handles.Count} tab(s)");
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Akakce] Failed to connect: {ex.Message}");
                _driver = null;
                return false;
            }
        }
    }


    private void InitializeDriver()
    {
        // This method is now a no-op for the search service
        // Connection is handled by TryConnectToExistingEdgeAsync
        // We keep this for backwards compatibility with the category scraper
        
        lock (_driverLock)
        {
            // If driver already exists and is working, reuse it
            if (_driver != null)
            {
                try
                {
                    var _ = _driver.WindowHandles;
                    Console.WriteLine("[Akakce] Reusing existing Edge driver");
                    return;
                }
                catch
                {
                    Console.WriteLine("[Akakce] Existing driver is dead");
                    try { _driver?.Quit(); } catch { }
                    _driver = null;
                    _initializationAttempted = false;
                }
            }
            
            if (_initializationAttempted && _driver == null)
            {
                Console.WriteLine("[Akakce] ⚠️ Already attempted initialization");
                return;
            }
            
            _initializationAttempted = true;
            
            // For the category scraper, start Edge with a fresh profile
            Console.WriteLine("[Akakce] Starting fresh Edge for category scraping...");
            
            var options = new EdgeOptions();
            
            // Use separate profile directory for scraper
            Console.WriteLine($"[Akakce] Using scraper profile: {ScraperProfileDir}");
            if (!Directory.Exists(ScraperProfileDir))
            {
                Directory.CreateDirectory(ScraperProfileDir);
            }
            options.AddArgument($"--user-data-dir={ScraperProfileDir}");
            options.AddArgument("--profile-directory=Default");
            
            // Enable remote debugging so we can reconnect
            options.AddArgument($"--remote-debugging-port={DEBUGGING_PORT}");
            
            // Prevent popups
            options.AddArgument("--no-first-run");
            options.AddArgument("--no-default-browser-check");
            options.AddArgument("--disable-popup-blocking");
            
            // CRITICAL: NOT headless
            options.AddArgument("--window-size=1920,1080");
            options.AddArgument("--start-maximized");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--no-sandbox");
            
            // Anti-detection
            options.AddArgument("--disable-blink-features=AutomationControlled");
            options.AddExcludedArgument("enable-automation");
            options.AddAdditionalOption("useAutomationExtension", false);
            options.AddArgument("--disable-infobars");
            
            // Realistic user agent
            var userAgent = GetRandomUserAgent();
            options.AddArgument($"user-agent={userAgent}");
            
            // Language
            options.AddArgument("--lang=tr-TR");
            options.AddUserProfilePreference("intl.accept_languages", "tr-TR,tr,en-US,en");
            options.AddUserProfilePreference("credentials_enable_service", false);
            options.AddUserProfilePreference("profile.password_manager_enabled", false);
            
            // Suppress logging
            options.AddArgument("--log-level=3");
            options.AddArgument("--silent");
            
            var service = EdgeDriverService.CreateDefaultService();
            service.SuppressInitialDiagnosticInformation = true;
            service.HideCommandPromptWindow = true;
            
            try
            {
                _driver = new EdgeDriver(service, options, TimeSpan.FromMinutes(3));
                _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);
                _driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);
                
                // Inject anti-detection scripts
                InjectAntiDetectionScripts();
                
                Console.WriteLine("[Akakce] ✓ Edge driver initialized");
                Console.WriteLine("[Akakce] ✓ First time? Cloudflare will appear - solve it manually, then scraper will remember");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Akakce] ✗ Error initializing Edge: {ex.Message}");
                throw;
            }
        } // end lock
    }

    /// <summary>
    /// Inject comprehensive anti-detection JavaScript
    /// </summary>
    private void InjectAntiDetectionScripts()
    {
        if (_driver == null) return;
        
        try
        {
            var edgeDriver = (EdgeDriver)_driver;
            
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
                            { name: 'PDF Viewer', filename: 'internal-pdf-viewer', description: 'Portable Document Format' },
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
                
                // 5. Add chrome object (Edge is Chromium-based)
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
            
            // Execute via CDP for earlier injection (Edge supports CDP like Chrome)
            edgeDriver.ExecuteCdpCommand(
                "Page.addScriptToEvaluateOnNewDocument",
                new Dictionary<string, object> { ["source"] = antiDetectionScript }
            );
            
            Console.WriteLine("[Akakce] ✓ Anti-detection scripts injected via CDP");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] ⚠ CDP injection failed: {ex.Message}");
            
            // Fallback: Direct JavaScript injection
            try
            {
                var jsExecutor = (IJavaScriptExecutor)_driver;
                jsExecutor.ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");
                Console.WriteLine("[Akakce] ✓ Fallback anti-detection applied");
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
            for (int retry = 0; retry < 2; retry++)
            {
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
                
                // Wait and retry
                Console.WriteLine("[Akakce] Retry clicking Turnstile checkbox...");
                await Task.Delay(2000);
            }
            
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
                _cloudflareDetected = true; // Mark that we've seen Cloudflare
                var elapsed = (int)(DateTime.Now - startTime).TotalSeconds;
                
                // Try to click Turnstile checkbox once
                if (hasTurnstile && !turnstileClickAttempted)
                {
                    turnstileClickAttempted = true;
                    Console.WriteLine("[Akakce] 🔐 Turnstile challenge detected - attempting to solve...");
                    
                    if (await TryClickTurnstileCheckbox())
                    {
                        await Task.Delay(5000); // Wait for verification to complete
                        continue; // Check again
                    }
                    else
                    {
                        Console.WriteLine("[Akakce] ✋ Please click the 'Verify you are human' checkbox manually...");
                    }
                }
                
                if (elapsed % 10 == 0)
                {
                    Console.WriteLine($"[Akakce] ⏳ Waiting for Cloudflare... ({elapsed}s)");
                    
                    // Simulate human behavior while waiting
                    await SimulateHumanBehavior();
                }
                
                await Task.Delay(1000);
            }
            else if (title.Length > 5 && !title.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                if (wasCloudflare)
                {
                    Console.WriteLine($"[Akakce] ✅ Cloudflare challenge passed! (took {(int)(DateTime.Now - startTime).TotalSeconds}s)");
                    _productsScrapedSinceLastChallenge = 0; // Reset counter
                }
                else
                {
                    Console.WriteLine("[Akakce] ✅ No Cloudflare challenge detected");
                }
                return true;
            }
            else
            {
                await Task.Delay(500);
            }
        }
        
        Console.WriteLine($"[Akakce] ⏰ Cloudflare challenge timeout after {maxWaitSeconds}s");
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
                
                // Add delay between products - use shorter delay if no Cloudflare detected
                if (_productsScrapedSinceLastChallenge > 0)
                {
                    int minDelay = _cloudflareDetected ? MIN_DELAY_CLOUDFLARE : MIN_DELAY_BETWEEN_PRODUCTS;
                    int maxDelay = _cloudflareDetected ? MAX_DELAY_CLOUDFLARE : MAX_DELAY_BETWEEN_PRODUCTS;
                    var delaySeconds = _random.Next(minDelay, maxDelay);
                    Console.WriteLine($"[Akakce] ⏳ Waiting {delaySeconds}s before loading...");
                    await Task.Delay(delaySeconds * 1000);
                }
                
                // Only simulate human behavior on retries or if Cloudflare was detected
                if (attempt > 1 || _cloudflareDetected)
                {
                    await SimulateHumanBehavior(extensive: attempt > 1);
                }
                
                // Add random delay on retry
                if (attempt > 1)
                {
                    var retryDelay = _random.Next(3000, 6000) * attempt;
                    Console.WriteLine($"[Akakce] Retry delay: {retryDelay / 1000}s...");
                    await Task.Delay(retryDelay);
                }
                
                _driver!.Navigate().GoToUrl(url);
                await RandomDelay(1000, 2000); // Reduced from 2-4s
                
                // Wait for Cloudflare with human behavior
                if (await WaitForCloudflareWithHumanBehavior())
                {
                    // Only do extensive human behavior if Cloudflare was detected
                    if (_cloudflareDetected)
                    {
                        await SimulateHumanBehavior();
                    }
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
    /// Search for a product by name and return the first result's URL
    /// Uses the Akakce search form: /arama/?q={query}
    /// </summary>
    public async Task<string?> SearchProductAsync(string productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            Console.WriteLine("[Akakce] ERROR: Product name is empty");
            return null;
        }

        try
        {
            // Don't call InitializeDriver - we must be connected via WarmupAsync
            if (_driver == null)
            {
                Console.WriteLine("[Akakce] ERROR: Not connected to Edge. Run WarmupAsync first.");
                return null;
            }
            
            // Short delay before search
            var preSearchDelay = _random.Next(MIN_DELAY_SEARCH, MAX_DELAY_SEARCH);
            Console.WriteLine($"[Akakce] ⏳ Waiting {preSearchDelay}s before search...");
            await Task.Delay(preSearchDelay * 1000);
            
            // URL encode the product name for search
            var encodedQuery = System.Net.WebUtility.UrlEncode(productName.Trim());
            var searchUrl = $"https://www.akakce.com/arama/?q={encodedQuery}";
            
            Console.WriteLine($"[Akakce] Searching for: {productName}");
            Console.WriteLine($"[Akakce] Search URL: {searchUrl}");
            
            // Navigate directly
            Console.WriteLine("[Akakce] Navigating to search page...");
            _driver.Navigate().GoToUrl(searchUrl);
            
            // Wait for page to load
            await Task.Delay(3000);
            
            Console.WriteLine($"[Akakce] Current URL: {_driver.Url}");
            Console.WriteLine($"[Akakce] Page title: {_driver.Title}");
            
            // Check if we were redirected directly to a product page
            var currentUrl = _driver.Url;
            if (IsProductUrl(currentUrl))
            {
                Console.WriteLine($"[Akakce] Search redirected directly to product: {currentUrl}");
                return currentUrl;
            }
            
            // Check for Cloudflare
            var title = _driver.Title ?? "";
            if (title.Contains("Bir dakika", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[Akakce] ⚠️ Cloudflare detected on search - this shouldn't happen!");
                Console.WriteLine("[Akakce] Your Edge session may have expired. Restart Edge with debugging.");
                return null;
            }
            
            // Extract first product URL from search results
            var jsExecutor = (IJavaScriptExecutor)_driver;

            
            // Scroll to load lazy content
            jsExecutor.ExecuteScript("window.scrollTo(0, 300);");
            await RandomDelay(500, 1000);
            
            var firstProductUrl = jsExecutor.ExecuteScript(@"
                // Method 1: Look for product links in search results list
                var productList = document.querySelector('ul#CPL') || 
                                 document.querySelector('ul.pl_v9.qv_v9') ||
                                 document.querySelector('ul.pl_v9');
                
                if (productList) {
                    var firstProduct = productList.querySelector('li[data-pr] a[href]');
                    if (firstProduct) {
                        var href = firstProduct.getAttribute('href');
                        if (href && href.match(/,\d+\.html$/)) {
                            console.log('[Akakce Search] Found product in list: ' + href);
                            return href.startsWith('/') ? 'https://www.akakce.com' + href : href;
                        }
                    }
                }
                
                // Method 2: Look for any product link matching the pattern
                var allLinks = document.querySelectorAll('a[href*="",""][href$="".html""]');
                for (var i = 0; i < allLinks.length; i++) {
                    var href = allLinks[i].getAttribute('href');
                    if (href && href.match(/,\d+\.html$/)) {
                        console.log('[Akakce Search] Found product link: ' + href);
                        return href.startsWith('/') ? 'https://www.akakce.com' + href : href;
                    }
                }
                
                // Method 3: Check if page shows 'no results' message
                var noResults = document.querySelector('.no-result') || 
                               document.querySelector('[class*=""noResult""]') ||
                               document.querySelector('[class*=""empty""]');
                if (noResults) {
                    console.log('[Akakce Search] No results found');
                    return 'NO_RESULTS';
                }
                
                console.log('[Akakce Search] No product links found on page');
                return null;
            ");
            
            if (firstProductUrl == null || string.IsNullOrEmpty(firstProductUrl.ToString()))
            {
                Console.WriteLine("[Akakce] No product found in search results");
                return null;
            }
            
            var productUrl = firstProductUrl.ToString()!;
            
            if (productUrl == "NO_RESULTS")
            {
                Console.WriteLine("[Akakce] Search returned no results");
                return null;
            }
            
            // Validate it's a proper product URL
            if (!IsProductUrl(productUrl))
            {
                Console.WriteLine($"[Akakce] Invalid product URL format: {productUrl}");
                return null;
            }
            
            Console.WriteLine($"[Akakce] ✓ Found product: {productUrl}");
            return productUrl;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Search error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Simple navigation that waits for Cloudflare challenge to complete
    /// </summary>
    private async Task<bool> NavigateSimple(string url, int timeoutSeconds = 30)
    {
        if (_driver == null) return false;
        
        try
        {
            Console.WriteLine($"[Akakce] Loading: {url.Substring(0, Math.Min(80, url.Length))}...");
            
            _driver.Navigate().GoToUrl(url);
            
            // Wait for initial page load
            await Task.Delay(_random.Next(3000, 5000));
            
            // Check for Cloudflare and wait for it to complete
            var startTime = DateTime.Now;
            var maxWaitTime = TimeSpan.FromSeconds(timeoutSeconds);
            
            while ((DateTime.Now - startTime) < maxWaitTime)
            {
                var title = _driver.Title ?? "";
                var pageUrl = _driver.Url ?? "";
                
                // Check if we're past Cloudflare
                bool isCloudflare = title.Contains("Bir dakika", StringComparison.OrdinalIgnoreCase) ||
                                   title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                                   title.Contains("lütfen", StringComparison.OrdinalIgnoreCase) ||
                                   title.Contains("Attention", StringComparison.OrdinalIgnoreCase) ||
                                   title.Contains("Checking", StringComparison.OrdinalIgnoreCase);
                
                if (!isCloudflare && title.Length > 5)
                {
                    Console.WriteLine($"[Akakce] ✓ Page loaded: {title.Substring(0, Math.Min(50, title.Length))}");
                    return true;
                }
                
                var elapsed = (int)(DateTime.Now - startTime).TotalSeconds;
                if (elapsed % 10 == 0 && elapsed > 0)
                {
                    Console.WriteLine($"[Akakce] ⏳ Waiting for Cloudflare... ({elapsed}s/{timeoutSeconds}s)");
                }
                
                await Task.Delay(1000);
            }
            
            // Timeout - check final state
            var finalTitle = _driver.Title ?? "";
            Console.WriteLine($"[Akakce] Timeout. Final title: {finalTitle}");
            
            return !finalTitle.Contains("Bir dakika", StringComparison.OrdinalIgnoreCase) &&
                   !finalTitle.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
        }
        catch (WebDriverTimeoutException)
        {
            Console.WriteLine("[Akakce] Page load timeout");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Navigation error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if a URL is a valid Akakce product page URL
    /// </summary>
    private bool IsProductUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return url.Contains("akakce.com") && 
               System.Text.RegularExpressions.Regex.IsMatch(url, @",\d+\.html$");
    }

    /// <summary>
    /// Scrape a single Akakce product page with optional variant scanning
    /// </summary>
    public async Task<AkakceProductInfo> ScrapeProductAsync(string productUrl, bool scanAllVariants = false)
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
            
            // Scroll to trigger lazy loading - reduced iterations from 5 to 3
            Console.WriteLine("[Akakce] Loading seller list...");
            var jsExecutor = (IJavaScriptExecutor)_driver!;
            
            for (int i = 1; i <= 3; i++)
            {
                jsExecutor.ExecuteScript($"window.scrollTo(0, document.body.scrollHeight * {i * 0.33});");
                await RandomDelay(200, 400); // Reduced from 300-600ms
            }
            
            jsExecutor.ExecuteScript("window.scrollTo(0, 0);");
            await RandomDelay(300, 500); // Reduced from 400-800ms
            
            var html = _driver.PageSource;
            Console.WriteLine($"[Akakce] Page loaded. Title: {_driver.Title}");
            
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Extract product details from page
            await ExtractProductDetails(htmlDoc, html, product);
            
            // Check if product has variants and scanAllVariants is enabled
            if (scanAllVariants)
            {
                var variantInfos = await DetectAndExtractVariants(jsExecutor);
                
                if (variantInfos.Count > 0)
                {
                    Console.WriteLine($"[Akakce] Found {variantInfos.Count} variant URLs to scrape");
                    await ScrapeAllVariants(product, variantInfos, jsExecutor);
                }
                else
                {
                    // No variants detected - scrape as single product
                    Console.WriteLine($"[Akakce] No variants detected, scraping as single product");
                    await ExtractSellersViaSelenium(product, html);
                }
            }
            else
            {
                // Default behavior - scrape current variant only
                await ExtractSellersViaSelenium(product, html);
            }

            if (product.HasVariants)
            {
                var totalSellers = product.Variants.Sum(v => v.SellerCount);
                Console.WriteLine($"[Akakce] ✅ SUCCESS: {product.Name} - {product.Variants.Count} variants, {totalSellers} total sellers");
            }
            else if (product.Sellers.Count > 0)
            {
                Console.WriteLine($"[Akakce] ✅ SUCCESS: {product.Name} - {product.SellerCount} sellers");
            }
            else
            {
                Console.WriteLine($"[Akakce] ⚠ No sellers found for: {product.Name}");
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
    
    public static bool IsValidAkakceUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("akakce.com") && url.Contains(",") && url.EndsWith(".html");
    }

    /// <summary>
    /// Detect variant URLs on the page (e.g., color variants, storage options)
    /// Variants in Akakce are separate product pages with their own URLs
    /// </summary>
    private async Task<List<VariantInfo>> DetectAndExtractVariants(IJavaScriptExecutor jsExecutor)
    {
        var variants = new List<VariantInfo>();
        
        try
        {
            Console.WriteLine("[Akakce] Detecting product variants...");
            
            // Extract variant URLs from the page
            // Variants are in: 
            // - #PRV_v8 (Renk seçenekleri - Color options)
            // - #PRG_v8 (Seçenekler - Storage/capacity options)
            var variantData = jsExecutor.ExecuteScript(@"
                var variants = [];
                
                // Helper function to extract variants from a container
                function extractVariants(container, groupName) {
                    if (!container) return;
                    
                    var links = container.querySelectorAll('li a[href]');
                    links.forEach(function(link) {
                        var href = link.getAttribute('href') || '';
                        var title = link.getAttribute('title') || '';
                        var text = link.textContent.trim();
                        
                        // Get variant name from the link text or title
                        var variantName = title || text.split('\n')[0].trim();
                        
                        // Clean up the variant name
                        variantName = variantName.replace(/\d+[\.,]\d+\s*TL/g, '').trim();
                        
                        // Check if this is a valid product URL
                        if (href && href.match(/,\d+\.html$/)) {
                            // Make URL absolute
                            var fullUrl = href;
                            if (href.startsWith('/')) {
                                fullUrl = 'https://www.akakce.com' + href;
                            }
                            
                            // Check if it's the current page (marked with class 'c')
                            var isCurrent = link.closest('li')?.classList.contains('c') || false;
                            
                            variants.push({
                                url: fullUrl,
                                name: variantName,
                                group: groupName,
                                isCurrent: isCurrent
                            });
                        }
                    });
                }
                
                // Look for color variants (PRV_v8)
                var colorContainer = document.querySelector('#PRV_v8');
                if (colorContainer) {
                    console.log('[Akakce Variants] Found color variants container');
                    extractVariants(colorContainer, 'Color');
                }
                
                // Look for storage/capacity variants (PRG_v8)
                var storageContainer = document.querySelector('#PRG_v8');
                if (storageContainer) {
                    console.log('[Akakce Variants] Found storage variants container');
                    extractVariants(storageContainer, 'Storage');
                }
                
                // Also look for generic variant containers with class prv_v8
                var genericContainers = document.querySelectorAll('span.prv_v8 ul');
                genericContainers.forEach(function(container, idx) {
                    if (container.id !== 'PRV_v8' && container.id !== 'PRG_v8') {
                        console.log('[Akakce Variants] Found generic variants container #' + idx);
                        extractVariants(container, 'Option' + (idx + 1));
                    }
                });
                
                console.log('[Akakce Variants] Total variants found: ' + variants.length);
                return JSON.stringify(variants);
            ");
            
            if (variantData != null && !string.IsNullOrEmpty(variantData.ToString()))
            {
                using var doc = JsonDocument.Parse(variantData.ToString()!);
                
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var url = item.GetProperty("url").GetString() ?? "";
                    var name = item.GetProperty("name").GetString() ?? "";
                    var group = item.GetProperty("group").GetString() ?? "";
                    var isCurrent = item.GetProperty("isCurrent").GetBoolean();
                    
                    if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(name))
                    {
                        variants.Add(new VariantInfo
                        {
                            Url = url,
                            Name = name,
                            Group = group,
                            IsCurrent = isCurrent
                        });
                    }
                }
                
                Console.WriteLine($"[Akakce] Detected {variants.Count} variant URLs:");
                foreach (var v in variants)
                {
                    var marker = v.IsCurrent ? " (current)" : "";
                    Console.WriteLine($"[Akakce]   - [{v.Group}] {v.Name}{marker}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Variant detection error: {ex.Message}");
        }
        
        await Task.CompletedTask;
        return variants;
    }

    /// <summary>
    /// Helper class for variant information
    /// </summary>
    private class VariantInfo
    {
        public string Url { get; set; } = "";
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public bool IsCurrent { get; set; }
    }

    /// <summary>
    /// Scrape sellers for all variant URLs by navigating to each variant page
    /// </summary>
    private async Task ScrapeAllVariants(
        AkakceProductInfo product, 
        List<VariantInfo> variantInfos,
        IJavaScriptExecutor jsExecutor)
    {
        int variantIndex = 1;
        int totalVariants = variantInfos.Count;
        
        // First, if current page has variants, scrape the current variant
        var currentVariant = variantInfos.FirstOrDefault(v => v.IsCurrent);
        
        foreach (var variantInfo in variantInfos)
        {
            try
            {
                Console.WriteLine($"[Akakce] Scraping variant {variantIndex}/{totalVariants}: {variantInfo.Name}");
                
                // If this is NOT the current page, navigate to the variant URL
                if (!variantInfo.IsCurrent)
                {
                    Console.WriteLine($"[Akakce] Navigating to variant URL: {variantInfo.Url}");
                    
                    // Navigate with retry logic
                    if (!await NavigateWithRetry(variantInfo.Url, 2))
                    {
                        Console.WriteLine($"[Akakce] ⚠ Failed to load variant page: {variantInfo.Name}");
                        variantIndex++;
                        continue;
                    }
                    
                    // Scroll to trigger lazy loading
                    var js = (IJavaScriptExecutor)_driver!;
                    for (int i = 1; i <= 3; i++)
                    {
                        js.ExecuteScript($"window.scrollTo(0, document.body.scrollHeight * {i * 0.3});");
                        await RandomDelay(300, 500);
                    }
                    js.ExecuteScript("window.scrollTo(0, 0);");
                    await RandomDelay(300, 500);
                }
                
                // Extract sellers for this variant
                var html = _driver!.PageSource;
                var variantProduct = new AkakceProductInfo
                {
                    ProductUrl = variantInfo.Url,
                    ProductId = ExtractProductIdFromUrl(variantInfo.Url),
                    Name = product.Name
                };
                
                await ExtractSellersViaSelenium(variantProduct, html);
                
                // Create variant object with options dictionary
                var options = new Dictionary<string, string>
                {
                    { variantInfo.Group, variantInfo.Name }
                };
                
                var variant = new AkakceProductVariant
                {
                    Options = options,
                    VariantName = variantInfo.Name,
                    VariantUrl = variantInfo.Url,
                    Sellers = variantProduct.Sellers,
                    LowestPrice = variantProduct.LowestPrice,
                    HighestPrice = variantProduct.HighestPrice
                };
                
                product.Variants.Add(variant);
                
                Console.WriteLine($"[Akakce] ✓ Variant '{variantInfo.Name}': {variant.SellerCount} sellers, range: {variant.LowestPrice} - {variant.HighestPrice}");
                
                variantIndex++;
                
                // Add delay between variants to avoid triggering Cloudflare
                if (!variantInfo.IsCurrent && variantIndex <= totalVariants)
                {
                    var delay = _random.Next(3000, 5000);
                    Console.WriteLine($"[Akakce] ⏳ Waiting {delay/1000}s before next variant...");
                    await Task.Delay(delay);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Akakce] ❌ Error scraping variant {variantIndex}: {ex.Message}");
                variantIndex++;
            }
        }
        
        Console.WriteLine($"[Akakce] ✅ Completed scraping {product.Variants.Count} variants");
    }

    /// <summary>
    /// Extract product ID from URL
    /// </summary>
    private string ExtractProductIdFromUrl(string url)
    {
        var idMatch = Regex.Match(url, @",(\d+)\.html$");
        return idMatch.Success ? idMatch.Groups[1].Value : "";
    }

    /// <summary>
    /// Extract product details from page
    /// </summary>
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
                    if (title.Contains(" Fiyatları")) title = title.Split(" Fiyatları")[0].Trim();
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
            Console.WriteLine("[Akakce] Extracting seller data from JSON-LD...");
            var jsonLdData = jsExecutor.ExecuteScript(@"
                var results = [];
                var scripts = document.querySelectorAll('script[type=""application/ld+json""]');
                
                scripts.forEach(function(script) {
                    try {
                        var data = JSON.parse(script.textContent);
                        
                        if (data['@type'] === 'Product' && data.offers) {
                            var offersData = data.offers;
                            var offersList = offersData.offers || (offersData['@type'] === 'Offer' ? [offersData] : []);
                            
                            offersList.forEach(function(offer) {
                                if (offer.price && offer.seller && offer.seller.name) {
                                    var fullName = offer.seller.name;
                                    var marketplace = '';
                                    var sellerName = '';
                                    
                                    var slashIndex = fullName.indexOf('/');
                                    if (slashIndex > 0) {
                                        marketplace = fullName.substring(0, slashIndex).trim();
                                        sellerName = fullName.substring(slashIndex + 1).trim();
                                    } else {
                                        marketplace = fullName.trim();
                                        sellerName = '';
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
                    
                    var priceMatch = text.match(/([0-9]{1,3}(?:\.[0-9]{3})*),(\d{2})\s*(?:TL)?/);
                    if (!priceMatch) return;
                    
                    var price = parseFloat(priceMatch[1].replace(/\./g, '') + '.' + priceMatch[2]);
                    if (price <= 0) return;
                    
                    var marketplace = '';
                    var img = item.querySelector('img[alt]');
                    if (img && img.alt) {
                        marketplace = img.alt.trim();
                    }
                    
                    var sellerName = '';
                    var allElements = item.querySelectorAll('a span, a b, a > *');
                    for (var i = 0; i < allElements.length; i++) {
                        var el = allElements[i];
                        var elText = (el.textContent || '').trim();
                        
                        if (elText.startsWith('/') && elText.length > 1 && elText.length < 60) {
                            var candidate = elText.substring(1).trim();
                            candidate = candidate.split('\n')[0].trim();
                            candidate = candidate.split('Satıcıya')[0].trim();
                            
                            if (candidate && candidate.length > 1 && candidate.length < 50 &&
                                !candidate.match(/^[0-9]/) && !candidate.includes('TL')) {
                                sellerName = candidate;
                                break;
                            }
                        }
                    }
                    
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

    private async Task EnrichSellerNamesViaDom(AkakceProductInfo product)
    {
        if (_driver == null) return;

        try
        {
            var jsExecutor = (IJavaScriptExecutor)_driver;
            
            var sellerNamesJson = jsExecutor.ExecuteScript(@"
                var names = [];
                var items = document.querySelectorAll('#APL li, ul.pl_v8 > li, ul.pl_v9 > li, li.p_w');
                
                items.forEach(function(item) {
                    var sellerName = '';
                    
                    var allElements = item.querySelectorAll('a span, a b, a > *');
                    for (var i = 0; i < allElements.length; i++) {
                        var el = allElements[i];
                        var text = (el.textContent || '').trim();
                        
                        if (text.startsWith('/') && text.length > 1 && text.length < 60) {
                            var candidate = text.substring(1).trim();
                            candidate = candidate.split('\n')[0].trim();
                            candidate = candidate.split('Satıcıya')[0].trim();
                            
                            if (candidate && candidate.length > 1 && candidate.length < 50 &&
                                !candidate.match(/^[0-9]/) && !candidate.includes('TL')) {
                                sellerName = candidate;
                                break;
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
                
                if (names != null && names.Count > 0 && names.Count == product.Sellers.Count)
                {
                    int enrichedCount = 0;
                    foreach (var seller in product.Sellers)
                    {
                        if (string.IsNullOrEmpty(seller.SellerName) && seller.Rank <= names.Count)
                        {
                            var domName = names[seller.Rank - 1];
                            if (!string.IsNullOrWhiteSpace(domName) && domName.Length >= 2 && domName.Length < 50)
                            {
                                seller.SellerName = domName;
                                enrichedCount++;
                            }
                        }
                    }
                    if (enrichedCount > 0)
                    {
                        Console.WriteLine($"[Akakce] Enriched {enrichedCount} seller names from DOM");
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

    private void ParseJsonLdPrices(string json, AkakceProductInfo product)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            int rank = 1;
            decimal? lowestPrice = null;
            decimal? highestPrice = null;

            foreach (var priceItem in doc.RootElement.EnumerateArray())
            {
                var seller = new AkakceSellerInfo
                {
                    Rank = rank,
                    ParentProductUrl = product.ProductUrl,
                    ParentProductId = product.ProductId,
                    ParentProductName = product.Name
                };

                if (priceItem.TryGetProperty("price", out var priceEl) && priceEl.TryGetDecimal(out var price))
                {
                    seller.Price = price;
                    seller.PriceFormatted = FormatTurkishPrice(price);
                    if (!lowestPrice.HasValue || price < lowestPrice) lowestPrice = price;
                    if (!highestPrice.HasValue || price > highestPrice) highestPrice = price;
                }

                if (priceItem.TryGetProperty("marketplace", out var marketplaceEl))
                {
                    seller.Marketplace = marketplaceEl.GetString() ?? "";
                }
                
                if (priceItem.TryGetProperty("sellerName", out var sellerNameEl))
                {
                    seller.SellerName = sellerNameEl.GetString() ?? "";
                }

                if (priceItem.TryGetProperty("url", out var urlEl))
                {
                    seller.ProductLink = urlEl.GetString() ?? "";
                }

                if (rank == 1) seller.Badges.Add("En Ucuz");
                seller.InStock = true;

                if (seller.Price > 0 && !string.IsNullOrEmpty(seller.Marketplace))
                {
                    product.Sellers.Add(seller);
                    rank++;
                }
            }

            product.SellerCount = product.Sellers.Count;
            if (lowestPrice.HasValue) product.LowestPrice = FormatTurkishPrice(lowestPrice.Value);
            if (highestPrice.HasValue) product.HighestPrice = FormatTurkishPrice(highestPrice.Value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] JSON-LD parse error: {ex.Message}");
        }
    }

    private void ParseQvPricesJson(string json, AkakceProductInfo product)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            int rank = 1;
            decimal? lowestPrice = null;
            decimal? highestPrice = null;

            foreach (var priceItem in doc.RootElement.EnumerateArray())
            {
                var seller = new AkakceSellerInfo
                {
                    Rank = rank,
                    ParentProductUrl = product.ProductUrl,
                    ParentProductId = product.ProductId,
                    ParentProductName = product.Name
                };

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

                if (priceItem.TryGetProperty("vdName", out var vdNameEl))
                {
                    seller.Marketplace = vdNameEl.GetString() ?? "";
                }
                
                seller.SellerName = "";

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
                    rank++;
                }
            }

            product.SellerCount = product.Sellers.Count;
            if (lowestPrice.HasValue) product.LowestPrice = FormatTurkishPrice(lowestPrice.Value);
            if (highestPrice.HasValue) product.HighestPrice = FormatTurkishPrice(highestPrice.Value);
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

            foreach (var priceItem in doc.RootElement.EnumerateArray())
            {
                var seller = new AkakceSellerInfo
                {
                    Rank = rank,
                    ParentProductUrl = product.ProductUrl,
                    ParentProductId = product.ProductId,
                    ParentProductName = product.Name
                };

                if (priceItem.TryGetProperty("price", out var priceEl) && priceEl.TryGetDecimal(out var price))
                {
                    seller.Price = price;
                    seller.PriceFormatted = FormatTurkishPrice(price);
                    if (!lowestPrice.HasValue || price < lowestPrice) lowestPrice = price;
                    if (!highestPrice.HasValue || price > highestPrice) highestPrice = price;
                }

                if (priceItem.TryGetProperty("marketplace", out var marketplaceEl))
                {
                    seller.Marketplace = marketplaceEl.GetString() ?? "";
                }
                
                if (priceItem.TryGetProperty("sellerName", out var sellerNameEl))
                {
                    seller.SellerName = sellerNameEl.GetString() ?? "";
                }

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
                    rank++;
                }
            }

            product.SellerCount = product.Sellers.Count;
            if (lowestPrice.HasValue) product.LowestPrice = FormatTurkishPrice(lowestPrice.Value);
            if (highestPrice.HasValue) product.HighestPrice = FormatTurkishPrice(highestPrice.Value);
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
                await onProgress(5, $"🔄 Loading category page...", "info");
            }
            
            if (!await NavigateWithRetry(categoryUrl))
            {
                if (onProgress != null)
                {
                    await onProgress(10, "❌ Page blocked by Cloudflare after retries", "error");
                }
                return productUrls;
            }
            
            int pageNumber = 1;
            int maxPages = 20;
            
            while (productUrls.Count < maxProducts && pageNumber <= maxPages)
            {
                Console.WriteLine($"[Akakce] Processing page {pageNumber}...");
                
                if (onProgress != null)
                {
                    await onProgress(10, $"📄 Page {pageNumber}: Extracting URLs...", "info");
                }
                
                var jsExecutor = (IJavaScriptExecutor)_driver!;
                for (int i = 1; i <= 5; i++)
                {
                    jsExecutor.ExecuteScript($"window.scrollTo(0, document.body.scrollHeight * {i * 0.2});");
                    await RandomDelay(300, 600);
                }
                
                var urlsJson = jsExecutor.ExecuteScript(@"
                    var urls = [];
                    var seen = {};
                    
                    var productList = document.querySelector('ul#CPL') || 
                                     document.querySelector('ul.pl_v9.qv_v9') ||
                                     document.querySelector('ul.pl_v9');
                    
                    if (productList) {
                        var productItems = productList.querySelectorAll(':scope > li[data-pr]');
                        
                        productItems.forEach(function(li) {
                            var links = li.querySelectorAll('a[href]');
                            var foundUrl = false;
                            
                            links.forEach(function(a) {
                                if (foundUrl) return;
                                
                                var href = a.href;
                                if (href && href.match(/,\d+\.html$/)) {
                                    if (!seen[href]) { 
                                        seen[href] = true; 
                                        urls.push(href);
                                        foundUrl = true;
                                    }
                                }
                            });
                        });
                    }
                    
                    if (urls.length === 0) {
                        document.querySelectorAll('ul.pl_v9 > li[data-pr] a[href]').forEach(function(a) {
                            var href = a.href;
                            if (href && href.match(/,\d+\.html$/) && !seen[href]) {
                                seen[href] = true;
                                urls.push(href);
                            }
                        });
                    }
                    
                    return JSON.stringify(urls);
                ");
                
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
                            }
                        }
                    }
                }
                
                Console.WriteLine($"[Akakce] Page {pageNumber}: Total URLs: {productUrls.Count}");
                
                if (productUrls.Count >= maxProducts)
                {
                    break;
                }
                
                // Try to find next page
                var nextPageUrl = jsExecutor.ExecuteScript(@"
                    var nextLink = document.querySelector('a.p[title=""Sonraki""]') || document.querySelector('a[title=""Sonraki""]');
                    return nextLink ? nextLink.href : null;
                ");
                
                if (nextPageUrl != null && !string.IsNullOrEmpty(nextPageUrl.ToString()))
                {
                    await Task.Delay(_random.Next(3000, 5000));
                    
                    if (await NavigateWithRetry(nextPageUrl.ToString()!, 2))
                    {
                        pageNumber++;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
            
            Console.WriteLine($"[Akakce] ✓ Extracted {productUrls.Count} product URLs from {pageNumber} page(s)");
            
            if (onProgress != null)
            {
                await onProgress(15, $"✓ Found {productUrls.Count} product URLs", "success");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce] Error extracting URLs: {ex.Message}");
        }
        
        return productUrls;
    }

    public void Dispose()
    {
        // Don't close the static driver - it's shared across instances
        // The driver will be reused for subsequent searches
        Console.WriteLine("[Akakce] Scraper instance disposed (driver remains open for reuse)");
    }
    
    /// <summary>
    /// Force close the Edge driver (call when completely done with all scraping)
    /// </summary>
    public static void ForceCloseDriver()
    {
        lock (_driverLock)
        {
            if (_driver != null)
            {
                Console.WriteLine("[Akakce] Force closing Edge driver...");
                try { _driver.Quit(); } catch { }
                try { _driver.Dispose(); } catch { }
                _driver = null;
            }
            _initializationAttempted = false;
        }
    }
}
