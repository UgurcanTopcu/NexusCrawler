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
    private const string BaseUrl = "https://www.trendyol.com";
    public bool SaveHtmlForDebug { get; set; } = false;

    public TrendyolScraper()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
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
            
            // Hide automation indicators
            ((IJavaScriptExecutor)_driver).ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");
        }
    }

    public async Task<List<string>> GetProductLinksAsync(string categoryUrl)
    {
        var productLinks = new List<string>();
        Console.WriteLine($"Fetching product links from: {categoryUrl}");

        try
        {
            InitializeDriver();
            _driver!.Navigate().GoToUrl(categoryUrl);
            
            // Wait for products to load
            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
            wait.Until(d => d.FindElements(By.CssSelector("a[href*='-p-']")).Count > 0);
            
            // Scroll to load more products
            for (int i = 0; i < 5; i++)
            {
                ((IJavaScriptExecutor)_driver).ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
                await Task.Delay(500);
            }

            var linkElements = _driver.FindElements(By.CssSelector("a[href*='-p-']"));
            
            foreach (var element in linkElements)
            {
                try
                {
                    var href = element.GetAttribute("href");
                    if (!string.IsNullOrEmpty(href) && href.Contains("-p-"))
                    {
                        var fullUrl = href.StartsWith("http") ? href : BaseUrl + href;
                        
                        // Extract clean product URL (remove query parameters except essential ones)
                        var cleanUrl = fullUrl.Split('?')[0];
                        
                        if (!productLinks.Contains(cleanUrl))
                        {
                            productLinks.Add(cleanUrl);
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
            Console.WriteLine($"\nScraping: {productUrl}");
            
            InitializeDriver();
            _driver!.Navigate().GoToUrl(productUrl);
            
            // Wait for page to load - reduced timeout for speed
            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));
            
            // Wait for key elements to load
            try
            {
                // Wait for attribute items to appear
                wait.Until(d => d.FindElements(By.CssSelector(".attribute-item")).Count > 0);
            }
            catch 
            {
                // If attributes don't load quickly, continue anyway
            }
            
            // Reduced wait time for faster scraping
            await Task.Delay(1000);

            var product = new ProductInfo { ProductUrl = productUrl };

            // Get page source and parse with HtmlAgilityPack
            var html = _driver.PageSource;
            
            // Save HTML for debugging if enabled
            if (SaveHtmlForDebug)
            {
                var filename = $"debug_page_{DateTime.Now:yyyyMMdd_HHmmss}.html";
                await File.WriteAllTextAsync(filename, html);
                Console.WriteLine($"  Saved HTML to: {filename}");
                
                // Run comprehensive analysis
                await ScraperDebugger.AnalyzeProductPage(html, productUrl);
            }
            
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // EXTRACT PRODUCT NAME & BRAND
            try
            {
                // Try to get the full product name from h1
                var h1Node = htmlDoc.DocumentNode.SelectSingleNode("//h1[contains(@class, 'pr-new-br') or contains(@class, 'product-title')]");
                if (h1Node != null)
                {
                    var fullName = h1Node.InnerText.Trim();
                    product.Name = fullName;
                    
                    // EXTRACT BRAND: Get the first <strong> tag in the h1 (highlighted brand name)
                    var brandStrong = h1Node.SelectSingleNode(".//strong");
                    if (brandStrong != null)
                    {
                        product.Brand = brandStrong.InnerText.Trim();
                        Console.WriteLine($"  Brand (from strong): {product.Brand}");
                    }
                }
                
                // Fallback: try other selectors
                if (string.IsNullOrEmpty(product.Name))
                {
                    var nameSelectors = new[]
                    {
                        "//h1",
                        "//span[@class='product-name']",
                        "//div[@class='product-name-container']//h1"
                    };

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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Name extraction error: {ex.Message}");
            }

            // EXTRACT PRICES - GET CART PRICE (Sepette price - the actual selling price)
            try
            {
                // Try JavaScript first for accurate pricing
                try
                {
                    var jsExecutor = (IJavaScriptExecutor)_driver!;
                    
                    // PRIORITY 1: Get "Sepette" price (cart price) - this is the real selling price
                    var cartPrice = jsExecutor.ExecuteScript(@"
                        // Look for 'Sepette' price text
                        var priceElements = Array.from(document.querySelectorAll('*'));
                        var sepetteElement = priceElements.find(el => 
                            el.textContent.includes('Sepette') && 
                            el.textContent.match(/[\d.,]+\s*TL/)
                        );
                        
                        if (sepetteElement) {
                            var match = sepetteElement.textContent.match(/([\d.,]+)\s*TL/);
                            if (match) return match[1] + ' TL';
                        }
                        
                        // Fallback to price box
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
                        Console.WriteLine($"  Price (Sepette): {product.DiscountedPrice}");
                    }
                }
                catch { }
                
                // HTML fallback if JavaScript didn't work
                if (string.IsNullOrEmpty(product.DiscountedPrice))
                {
                    // PRIORITY 1: Look for "Sepette" price in HTML
                    var allElements = htmlDoc.DocumentNode.SelectNodes("//*[contains(text(), 'Sepette')]");

                    if (allElements != null)
                    {
                        foreach (var elem in allElements)
                        {
                            var text = elem.InnerText;
                            if (text.Contains("Sepette") && text.Contains("TL"))
                            {
                                var match = Regex.Match(text, @"([\d.,]+)\s*TL");
                                if (match.Success)
                                {
                                    product.DiscountedPrice = match.Groups[1].Value + " TL";
                                    Console.WriteLine($"  Price (Sepette HTML): {product.DiscountedPrice}");
                                    break;
                                }
                            }
                        }
                    }
                    
                    // PRIORITY 2: Fallback to price box
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
                            
                            var discountedPrice = priceBox.SelectSingleNode(".//span[contains(@class, 'prc-dsc')]");
                            if (discountedPrice != null)
                            {
                                product.DiscountedPrice = CleanPrice(discountedPrice.InnerText);
                            }
                            
                            if (string.IsNullOrEmpty(product.DiscountedPrice))
                            {
                                var singlePrice = priceBox.SelectSingleNode(".//span[contains(@class, 'prc-slg')]");
                                if (singlePrice != null)
                                {
                                    product.DiscountedPrice = CleanPrice(singlePrice.InnerText);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Price extraction error: {ex.Message}");
            }

            // EXTRACT RATING & REVIEWS - IMPROVED
            try
            {
                // Try JavaScript first
                try
                {
                    var jsExecutor = (IJavaScriptExecutor)_driver!;
                    var ratingData = jsExecutor.ExecuteScript(@"
                        return {
                            rating: document.querySelector('[class*=""rating-score""]')?.textContent?.trim() || '',
                            count: document.querySelector('[class*=""review""]')?.textContent?.trim() || ''
                        };
                    ");
                    
                    if (ratingData != null)
                    {
                        var ratingDict = ratingData as Dictionary<string, object>;
                        if (ratingDict != null)
                        {
                            var rating = ratingDict["rating"]?.ToString() ?? "";
                            var count = ratingDict["count"]?.ToString() ?? "";
                            
                            if (!string.IsNullOrWhiteSpace(rating))
                            {
                                var match = Regex.Match(rating, @"(\d+[.,]?\d*)");
                                if (match.Success)
                                    product.Rating = match.Groups[1].Value;
                            }
                            
                            if (!string.IsNullOrWhiteSpace(count))
                            {
                                var match = Regex.Match(count, @"(\d+)");
                                if (match.Success)
                                    product.ReviewCount = match.Groups[1].Value;
                            }
                        }
                    }
                }
                catch { }
                
                // HTML fallback
                if (string.IsNullOrEmpty(product.Rating))
                {
                    var ratingSelectors = new[]
                    {
                        "//div[contains(@class, 'rating-score')]",
                        "//span[contains(@class, 'rating')]",
                        "//*[contains(@class, 'star-rating')]"
                    };
                    
                    foreach (var selector in ratingSelectors)
                    {
                        var node = htmlDoc.DocumentNode.SelectSingleNode(selector);
                        if (node != null)
                        {
                            var ratingText = node.InnerText.Trim();
                            var match = Regex.Match(ratingText, @"(\d+[.,]?\d*)");
                            if (match.Success)
                            {
                                product.Rating = match.Groups[1].Value;
                                break;
                            }
                        }
                    }
                }
                
                if (string.IsNullOrEmpty(product.ReviewCount))
                {
                    var reviewSelectors = new[]
                    {
                        "//span[contains(@class, 'review-count')]",
                        "//span[contains(text(), 'değerlendirme')]",
                        "//span[contains(text(), 'yorum')]",
                        "//*[contains(@class, 'comment-count')]"
                    };
                    
                    foreach (var selector in reviewSelectors)
                    {
                        var node = htmlDoc.DocumentNode.SelectSingleNode(selector);
                        if (node != null)
                        {
                            var reviewText = node.InnerText.Trim();
                            var match = Regex.Match(reviewText, @"(\d+)");
                            if (match.Success)
                            {
                                product.ReviewCount = match.Groups[1].Value;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Rating extraction error: {ex.Message}");
            }

            // EXTRACT SELLER
            try
            {
                var sellerSelectors = new[]
                {
                    "//a[contains(@class, 'merchant-info-account')]",
                    "//div[contains(@class, 'seller-name')]",
                    "//a[contains(@href, '/magaza/')]"
                };

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
            catch (Exception ex)
            {
                Console.WriteLine($"  Seller extraction error: {ex.Message}");
            }

            // EXTRACT IMAGE - IMPROVED
            try
            {
                // Try JavaScript first for high-quality image
                try
                {
                    var jsExecutor = (IJavaScriptExecutor)_driver!;
                    var imageUrl = jsExecutor.ExecuteScript(@"
                        var img = document.querySelector('[class*=""gallery""] img, [class*=""product-image""] img, img[src*=""productimages""]');
                        if (img) {
                            return img.src || img.getAttribute('data-src') || '';
                        }
                        return '';
                    ");
                    
                    if (imageUrl != null && !string.IsNullOrWhiteSpace(imageUrl.ToString()))
                    {
                        var imgUrl = imageUrl.ToString();
                        if (imgUrl!.StartsWith("//"))
                            product.ImageUrl = "https:" + imgUrl;
                        else if (!imgUrl.StartsWith("http"))
                            product.ImageUrl = BaseUrl + imgUrl;
                        else
                            product.ImageUrl = imgUrl;
                    }
                }
                catch { }
                
                // HTML fallback
                if (string.IsNullOrEmpty(product.ImageUrl))
                {
                    var imageSelectors = new[]
                    {
                        "//img[contains(@src, 'productimages')]",
                        "//div[contains(@class, 'gallery')]//img",
                        "//img[contains(@class, 'base-product-image')]",
                        "//img[contains(@class, 'product-image')]",
                        "//div[@class='product-image']//img",
                        "//img[contains(@alt, 'product') or contains(@alt, 'ürün')]",
                        "//img[contains(@class, 'image')]"
                    };

                    foreach (var selector in imageSelectors)
                    {
                        var node = htmlDoc.DocumentNode.SelectSingleNode(selector);
                        if (node != null)
                        {
                            var imgUrl = node.GetAttributeValue("src", "");
                            if (string.IsNullOrEmpty(imgUrl))
                                imgUrl = node.GetAttributeValue("data-src", "");
                            if (string.IsNullOrEmpty(imgUrl))
                                imgUrl = node.GetAttributeValue("data-original", "");
                            
                            if (!string.IsNullOrEmpty(imgUrl))
                            {
                                if (imgUrl.StartsWith("//"))
                                    product.ImageUrl = "https:" + imgUrl;
                                else if (!imgUrl.StartsWith("http"))
                                    product.ImageUrl = BaseUrl + imgUrl;
                                else
                                    product.ImageUrl = imgUrl;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Image extraction error: {ex.Message}");
            }

            // EXTRACT CATEGORY
            try
            {
                var breadcrumbNodes = htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'breadcrumb')]//a | //nav//ol//a");
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
            catch (Exception ex)
            {
                Console.WriteLine($"  Category extraction error: {ex.Message}");
            }

            // EXTRACT DESCRIPTION - FROM "Ürün Açıklaması" SECTION
            try
            {
                // METHOD 1: Look specifically for "Ürün Açıklaması" heading and get the text AFTER it
                var descriptionFound = false;
                
                // Find the heading first
                var headingPatterns = new[]
                {
                    "//h2[contains(text(), 'Ürün Açıklaması')]",
                    "//h3[contains(text(), 'Ürün Açıklaması')]",
                    "//*[contains(@class, 'title') and contains(text(), 'Ürün Açıklaması')]"
                };

                foreach (var pattern in headingPatterns)
                {
                    var heading = htmlDoc.DocumentNode.SelectSingleNode(pattern);
                    if (heading != null)
                    {
                        // Get the parent container
                        var container = heading.ParentNode;
                        if (container != null)
                        {
                            // Get all text after the heading
                            var allText = container.InnerText.Trim();
                            
                            // Remove the heading text itself
                            allText = allText.Replace(heading.InnerText, "").Trim();
                            
                            // Also try to find sibling elements with the description
                            var nextSibling = heading.NextSibling;
                            string descText = "";
                            
                            while (nextSibling != null)
                            {
                                if (nextSibling.NodeType == HtmlNodeType.Element)
                                {
                                    var text = nextSibling.InnerText.Trim();
                                    if (!string.IsNullOrWhiteSpace(text))
                                    {
                                        descText += text + " ";
                                    }
                                }
                                else if (nextSibling.NodeType == HtmlNodeType.Text)
                                {
                                    var text = nextSibling.InnerText.Trim();
                                    if (!string.IsNullOrWhiteSpace(text))
                                    {
                                        descText += text + " ";
                                    }
                                }
                                nextSibling = nextSibling.NextSibling;
                            }
                            
                            // Use sibling text if found, otherwise use cleaned container text
                            string fullText = !string.IsNullOrWhiteSpace(descText) ? descText.Trim() : allText;
                            
                            if (!string.IsNullOrWhiteSpace(fullText) && fullText.Length > 20)
                            {
                                // Clean up the description
                                fullText = Regex.Replace(fullText, @"\s+", " ").Trim();
                                
                                // Remove bullet points at the start
                                fullText = Regex.Replace(fullText, @"^[•\-\*]+\s*", "").Trim();
                                
                                // Limit length to 2000 chars
                                if (fullText.Length > 2000)
                                    fullText = fullText.Substring(0, 2000) + "...";
                                
                                product.Description = fullText;
                                Console.WriteLine($"  Description: {fullText.Substring(0, Math.Min(150, fullText.Length))}...");
                                descriptionFound = true;
                                break;
                            }
                        }
                    }
                }
                
                // METHOD 2: Try JavaScript to get description content
                if (!descriptionFound)
                {
                    try
                    {
                        var jsExecutor = (IJavaScriptExecutor)_driver!;
                        var jsDesc = jsExecutor.ExecuteScript(@"
                            // Find the 'Ürün Açıklaması' heading
                            var headings = Array.from(document.querySelectorAll('h2, h3, div'));
                            var descHeading = headings.find(h => h.textContent.trim() === 'Ürün Açıklaması');
                            
                            if (descHeading) {
                                var result = '';
                                
                                // Get next siblings until we hit another heading or end of content
                                var sibling = descHeading.nextElementSibling;
                                while (sibling) {
                                    // Stop if we hit another major heading
                                    if (sibling.tagName.match(/H[1-3]/) && 
                                        !sibling.textContent.includes('Ürün Açıklaması')) {
                                        break;
                                    }
                                    
                                    var text = sibling.textContent.trim();
                                    if (text && text.length > 10) {
                                        result += text + ' ';
                                    }
                                    
                                    sibling = sibling.nextElementSibling;
                                }
                                
                                // If no siblings found, try parent's text content
                                if (!result && descHeading.parentElement) {
                                    result = descHeading.parentElement.textContent
                                        .replace(descHeading.textContent, '')
                                        .trim();
                                }
                                
                                return result.trim();
                            }
                            return '';
                        ");
                        
                        if (jsDesc != null && !string.IsNullOrWhiteSpace(jsDesc.ToString()))
                        {
                            var desc = jsDesc.ToString()!.Trim();
                            desc = Regex.Replace(desc, @"\s+", " ");
                            desc = Regex.Replace(desc, @"^[•\-\*]+\s*", "").Trim();
                            
                            if (desc.Length > 2000)
                                desc = desc.Substring(0, 2000) + "...";
                            
                            if (desc.Length > 20)
                            {
                                product.Description = desc;
                                Console.WriteLine($"  Description (JS): {desc.Substring(0, Math.Min(150, desc.Length))}...");
                                descriptionFound = true;
                            }
                        }
                    }
                    catch { }
                }
                
                // METHOD 3: Last resort - look for common description container classes
                if (!descriptionFound)
                {
                    var descContainerPatterns = new[]
                    {
                        "//div[@id='product-detail-description']",
                        "//div[contains(@class, 'detail-desc-container')]",
                        "//div[contains(@class, 'product-description-content')]",
                        "//div[contains(@class, 'detail-description')]"
                    };

                    foreach (var pattern in descContainerPatterns)
                    {
                        var container = htmlDoc.DocumentNode.SelectSingleNode(pattern);
                        if (container != null)
                        {
                            var text = container.InnerText.Trim();
                            
                            // Remove any headings
                            var innerHeadings = container.SelectNodes(".//h2 | .//h3");
                            if (innerHeadings != null)
                            {
                                foreach (var h in innerHeadings)
                                {
                                    text = text.Replace(h.InnerText, "").Trim();
                                }
                            }
                            
                            if (!string.IsNullOrWhiteSpace(text) && text.Length > 20)
                            {
                                text = Regex.Replace(text, @"\s+", " ").Trim();
                                text = Regex.Replace(text, @"^[•\-\*]+\s*", "").Trim();
                                
                                if (text.Length > 2000)
                                    text = text.Substring(0, 2000) + "...";
                                
                                product.Description = text;
                                Console.WriteLine($"  Description (container): {text.Substring(0, Math.Min(150, text.Length))}...");
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Description extraction error: {ex.Message}");
            }

            // EXTRACT BARCODE
            try
            {
                // METHOD 1: Look for text containing "Barkod" or "Barkod No"
                var barcodePatterns = new[]
                {
                    "//*[contains(text(), 'Barkod No:')]",
                    "//*[contains(text(), 'Barkod:')]",
                    "//*[contains(text(), 'Barcode:')]"
                };

                foreach (var pattern in barcodePatterns)
                {
                    var nodes = htmlDoc.DocumentNode.SelectNodes(pattern);
                    if (nodes != null)
                    {
                        foreach (var node in nodes)
                        {
                            var text = node.InnerText.Trim();
                            // Extract barcode number (usually after "Barkod No:" or "Barkod:")
                            var match = Regex.Match(text, @"Barkod\s*(?:No)?:\s*(\d+)", RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                product.Barcode = match.Groups[1].Value;
                                Console.WriteLine($"  Barcode: {product.Barcode}");
                                break;
                            }
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(product.Barcode))
                        break;
                }
                
                // METHOD 2: JavaScript extraction
                if (string.IsNullOrEmpty(product.Barcode))
                {
                    try
                    {
                        var jsExecutor = (IJavaScriptExecutor)_driver!;
                        var jsBarcode = jsExecutor.ExecuteScript(@"
                            var elements = Array.from(document.querySelectorAll('*'));
                            var barcodeElement = elements.find(el => 
                                el.textContent.includes('Barkod') || 
                                el.textContent.includes('Barcode')
                            );
                            
                            if (barcodeElement) {
                                var match = barcodeElement.textContent.match(/Barkod\s*(?:No)?:\s*(\d+)/i);
                                if (match) return match[1];
                            }
                            return '';
                        ");
                        
                        if (jsBarcode != null && !string.IsNullOrWhiteSpace(jsBarcode.ToString()))
                        {
                            product.Barcode = jsBarcode.ToString()!.Trim();
                            Console.WriteLine($"  Barcode (JS): {product.Barcode}");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Barcode extraction error: {ex.Message}");
            }

            // EXTRACT PRODUCT ATTRIBUTES - FAST & DIRECT
            await ExtractAllProductAttributes(html, htmlDoc, _driver, product);

            return product;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error scraping product details: {ex.Message}");
        }

        return null;
    }

    private async Task ExtractAllProductAttributes(string html, HtmlDocument htmlDoc, IWebDriver driver, ProductInfo product)
    {
        Console.WriteLine("  🔍 Extracting product attributes...");
        
        // DIRECT METHOD: Target the exact Trendyol structure
        // <div class="attribute-item"><div class="name">KEY</div><div class="value">VALUE</div></div>
        
        var attributeItems = htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'attribute-item')]");
        
        if (attributeItems != null && attributeItems.Count > 0)
        {
            Console.WriteLine($"    Found {attributeItems.Count} attribute items");
            
            foreach (var item in attributeItems)
            {
                try
                {
                    // Get the name div
                    var nameDiv = item.SelectSingleNode(".//div[contains(@class, 'name')]");
                    // Get the value div
                    var valueDiv = item.SelectSingleNode(".//div[contains(@class, 'value')]");
                    
                    if (nameDiv != null && valueDiv != null)
                    {
                        var key = nameDiv.InnerText.Trim();
                        var value = valueDiv.InnerText.Trim();
                        
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                        {
                            product.Attributes[key] = value;
                            Console.WriteLine($"      {key}: {value}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      Error parsing attribute item: {ex.Message}");
                }
            }
        }
        else
        {
            Console.WriteLine("    No attribute-item divs found, trying alternative methods...");
            
            // BACKUP: Try JavaScript extraction
            try
            {
                var jsExecutor = (IJavaScriptExecutor)driver;
                var jsData = jsExecutor.ExecuteScript(@"
                    var attrs = [];
                    document.querySelectorAll('.attribute-item').forEach(item => {
                        var name = item.querySelector('.name');
                        var value = item.querySelector('.value');
                        if (name && value) {
                            attrs.push({
                                key: name.textContent.trim(),
                                value: value.textContent.trim()
                            });
                        }
                    });
                    return JSON.stringify(attrs);
                ");
                
                if (jsData != null)
                {
                    using var doc = JsonDocument.Parse(jsData.ToString());
                    var root = doc.RootElement;
                    
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var attr in root.EnumerateArray())
                        {
                            if (attr.TryGetProperty("key", out var keyElem) && 
                                attr.TryGetProperty("value", out var valueElem))
                            {
                                var key = keyElem.GetString();
                                var value = valueElem.GetString();
                                
                                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                                {
                                    product.Attributes[key] = value;
                                    Console.WriteLine($"      [JS] {key}: {value}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    JavaScript extraction error: {ex.Message}");
            }
        }
        
        Console.WriteLine($"  ✅ Extracted {product.Attributes.Count} product features");
    }

    private string CleanPrice(string priceText)
    {
        if (string.IsNullOrWhiteSpace(priceText))
            return "";
        
        var match = Regex.Match(priceText, @"([\d.,]+)");
        if (match.Success)
        {
            var numericValue = match.Value;
            // Ensure TL suffix
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
            
            // Add a small delay to avoid overwhelming the server
            await Task.Delay(500);
        }

        return products;
    }

    public void Dispose()
    {
        _driver?.Quit();
        _driver?.Dispose();
    }
}
