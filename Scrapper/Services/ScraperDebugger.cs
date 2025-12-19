using HtmlAgilityPack;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scrapper.Services;

/// <summary>
/// Utility class for debugging and testing CSS selectors on Trendyol pages
/// </summary>
public class ScraperDebugger
{
    public static async Task DebugProductPage(string productUrl)
    {
        Console.WriteLine($"=== Debugging Product Page ===");
        Console.WriteLine($"URL: {productUrl}\n");

        var options = new ChromeOptions();
        // Don't use headless mode for debugging
        // options.AddArgument("--headless");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--window-size=1920,1080");

        using var driver = new ChromeDriver(options);
        driver.Navigate().GoToUrl(productUrl);

        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
        await Task.Delay(1500); // Wait for page to fully load

        var html = driver.PageSource;
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        Console.WriteLine("=== Testing Selectors ===\n");

        // Test Name Selectors
        Console.WriteLine("--- Product Name ---");
        TestSelector(htmlDoc, "//h1[@class='pr-new-br']//span[@class='product-brand-name-with-link']", "Name Selector 1");
        TestSelector(htmlDoc, "//h1[contains(@class, 'pr-new-br')]", "Name Selector 2");
        TestSelector(htmlDoc, "//h1[@class='product-name']", "Name Selector 3");

        // Test Brand Selectors
        Console.WriteLine("\n--- Brand ---");
        TestSelector(htmlDoc, "//a[@class='product-brand-name-with-link']", "Brand Selector 1");
        TestSelector(htmlDoc, "//h1[@class='pr-new-br']//span[@class='product-brand-name-with-link']", "Brand Selector 2");

        // Test Price Selectors
        Console.WriteLine("\n--- Prices ---");
        TestSelector(htmlDoc, "//span[@class='prc-org']", "Original Price Selector 1");
        TestSelector(htmlDoc, "//span[@class='prc-dsc']", "Discounted Price Selector 1");
        TestSelector(htmlDoc, "//span[@class='prc-slg']", "Single Price Selector");

        // Test Rating Selectors
        Console.WriteLine("\n--- Rating ---");
        TestSelector(htmlDoc, "//div[@class='rating-score']//span", "Rating Selector 1");
        TestSelector(htmlDoc, "//span[@class='rating-score']", "Rating Selector 2");

        // Test Review Count Selectors
        Console.WriteLine("\n--- Review Count ---");
        TestSelector(htmlDoc, "//span[@class='review-count']", "Review Count Selector 1");
        TestSelector(htmlDoc, "//a[contains(@class, 'rnr-com-btn')]//span", "Review Count Selector 2");

        // Test Seller Selectors
        Console.WriteLine("\n--- Seller ---");
        TestSelector(htmlDoc, "//a[@class='merchant-info-account']", "Seller Selector 1");
        TestSelector(htmlDoc, "//a[contains(@class, 'merchant')]", "Seller Selector 2");

        // Test Description Selectors
        Console.WriteLine("\n--- Description ---");
        TestSelector(htmlDoc, "//div[@class='detail-desc-container']//p", "Description Selector 1");
        TestSelector(htmlDoc, "//div[contains(@class, 'product-description')]", "Description Selector 2");

        // Test Image Selectors
        Console.WriteLine("\n--- Image ---");
        TestImageSelector(htmlDoc, "//img[@class='product-image']", "Image Selector 1");
        TestImageSelector(htmlDoc, "//img[contains(@class, 'gallery-modal')]", "Image Selector 2");

        // Show all class names in the document
        Console.WriteLine("\n=== All Unique Classes Found ===");
        var allElements = htmlDoc.DocumentNode.SelectNodes("//*[@class]");
        if (allElements != null)
        {
            var uniqueClasses = allElements
                .Select(e => e.GetAttributeValue("class", ""))
                .Where(c => !string.IsNullOrEmpty(c))
                .SelectMany(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct()
                .OrderBy(c => c)
                .Take(50); // First 50 classes

            foreach (var className in uniqueClasses)
            {
                Console.WriteLine($"  - {className}");
            }
        }

        Console.WriteLine("\n=== Press any key to close browser ===");
        Console.ReadKey();

        driver.Quit();
    }

    private static void TestSelector(HtmlDocument doc, string xpath, string selectorName)
    {
        try
        {
            var node = doc.DocumentNode.SelectSingleNode(xpath);
            if (node != null)
            {
                var text = node.InnerText.Trim();
                if (text.Length > 100)
                    text = text.Substring(0, 100) + "...";
                Console.WriteLine($"? {selectorName}: \"{text}\"");
            }
            else
            {
                Console.WriteLine($"? {selectorName}: Not found");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"? {selectorName}: Error - {ex.Message}");
        }
    }

    private static void TestImageSelector(HtmlDocument doc, string xpath, string selectorName)
    {
        try
        {
            var node = doc.DocumentNode.SelectSingleNode(xpath);
            if (node != null)
            {
                var src = node.GetAttributeValue("src", "");
                if (string.IsNullOrEmpty(src))
                    src = node.GetAttributeValue("data-src", "");
                
                if (!string.IsNullOrEmpty(src))
                {
                    if (src.Length > 80)
                        src = src.Substring(0, 80) + "...";
                    Console.WriteLine($"? {selectorName}: \"{src}\"");
                }
                else
                {
                    Console.WriteLine($"? {selectorName}: Found but no src attribute");
                }
            }
            else
            {
                Console.WriteLine($"? {selectorName}: Not found");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"? {selectorName}: Error - {ex.Message}");
        }
    }

    public static async Task<string> AnalyzeProductPage(string html, string productUrl)
    {
        var report = new StringBuilder();
        report.AppendLine("=== TRENDYOL PRODUCT PAGE ANALYSIS ===");
        report.AppendLine($"URL: {productUrl}");
        report.AppendLine($"Time: {DateTime.Now}");
        report.AppendLine($"HTML Length: {html.Length} characters");
        report.AppendLine();

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        // 1. EXTRACT JSON-LD DATA
        report.AppendLine("--- JSON-LD STRUCTURED DATA ---");
        var scriptNodes = htmlDoc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scriptNodes != null)
        {
            int jsonCount = 0;
            foreach (var scriptNode in scriptNodes)
            {
                try
                {
                    var jsonContent = scriptNode.InnerText;
                    using var doc = JsonDocument.Parse(jsonContent);
                    var formatted = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                    report.AppendLine($"\nJSON-LD Block {++jsonCount}:");
                    report.AppendLine(formatted.Length > 2000 ? formatted.Substring(0, 2000) + "..." : formatted);
                }
                catch (Exception ex)
                {
                    report.AppendLine($"Error parsing JSON-LD: {ex.Message}");
                }
            }
        }
        else
        {
            report.AppendLine("No JSON-LD data found");
        }

        // 2. EXTRACT WINDOW.__PRODUCT_DETAIL_APP_INITIAL_STATE__
        report.AppendLine("\n--- JAVASCRIPT VARIABLES ---");
        var jsPatterns = new[]
        {
            @"window\.__PRODUCT_DETAIL_APP_INITIAL_STATE__\s*=\s*(\{.*?\});",
            @"window\.__INITIAL_STATE__\s*=\s*(\{.*?\});",
            @"window\.TYPageInfo\s*=\s*(\{.*?\});"
        };

        foreach (var pattern in jsPatterns)
        {
            var matches = Regex.Matches(html, pattern, RegexOptions.Singleline);
            if (matches.Count > 0)
            {
                report.AppendLine($"\nFound JS variable matching: {pattern.Substring(0, 50)}...");
                foreach (Match match in matches)
                {
                    try
                    {
                        var jsonStr = match.Groups[1].Value;
                        if (jsonStr.Length > 100)
                        {
                            // Try to parse and format
                            using var doc = JsonDocument.Parse(jsonStr);
                            var formatted = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                            report.AppendLine(formatted.Length > 3000 ? formatted.Substring(0, 3000) + "..." : formatted);
                        }
                    }
                    catch { }
                }
            }
        }

        // 3. EXTRACT ALL ELEMENTS WITH "ATTR" IN CLASS
        report.AppendLine("\n--- ATTRIBUTE ELEMENTS (contains 'attr' in class) ---");
        var attrElements = htmlDoc.DocumentNode.SelectNodes("//*[contains(@class, 'attr')]");
        if (attrElements != null)
        {
            report.AppendLine($"Found {attrElements.Count} elements with 'attr' in class:");
            foreach (var elem in attrElements.Take(20))
            {
                var classes = elem.GetAttributeValue("class", "");
                var text = elem.InnerText.Trim();
                if (text.Length > 200) text = text.Substring(0, 200) + "...";
                report.AppendLine($"  Tag: {elem.Name}, Classes: {classes}");
                report.AppendLine($"  Text: {text}");
                report.AppendLine($"  HTML: {elem.OuterHtml.Substring(0, Math.Min(300, elem.OuterHtml.Length))}");
                report.AppendLine();
            }
        }
        else
        {
            report.AppendLine("No elements with 'attr' in class found");
        }

        // 4. EXTRACT ALL <LI> ELEMENTS
        report.AppendLine("\n--- ALL LIST ITEMS (li) ---");
        var listItems = htmlDoc.DocumentNode.SelectNodes("//li");
        if (listItems != null)
        {
            report.AppendLine($"Found {listItems.Count} <li> elements:");
            foreach (var li in listItems.Take(50))
            {
                var text = li.InnerText.Trim();
                if (!string.IsNullOrWhiteSpace(text) && text.Length < 300)
                {
                    var classes = li.GetAttributeValue("class", "");
                    report.AppendLine($"  Classes: {classes}");
                    report.AppendLine($"  Text: {text}");
                    
                    // Check for spans inside
                    var spans = li.SelectNodes(".//span");
                    if (spans != null && spans.Count > 0)
                    {
                        report.AppendLine($"  Contains {spans.Count} spans:");
                        foreach (var span in spans.Take(5))
                        {
                            report.AppendLine($"    - {span.InnerText.Trim()}");
                        }
                    }
                    report.AppendLine();
                }
            }
        }

        // 5. EXTRACT PRICE BOX
        report.AppendLine("\n--- PRICE INFORMATION ---");
        var priceBox = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'prc-box')]");
        if (priceBox != null)
        {
            report.AppendLine("Price box found:");
            report.AppendLine(priceBox.OuterHtml.Substring(0, Math.Min(500, priceBox.OuterHtml.Length)));
        }
        else
        {
            report.AppendLine("No price box found");
        }

        // 6. EXTRACT RATING INFO
        report.AppendLine("\n--- RATING INFORMATION ---");
        var ratingElements = htmlDoc.DocumentNode.SelectNodes("//*[contains(@class, 'rating') or contains(@class, 'review')]");
        if (ratingElements != null)
        {
            foreach (var elem in ratingElements.Take(10))
            {
                var classes = elem.GetAttributeValue("class", "");
                var text = elem.InnerText.Trim();
                report.AppendLine($"  Classes: {classes}");
                report.AppendLine($"  Text: {text}");
                report.AppendLine();
            }
        }

        // 7. EXTRACT ALL DIVS/SECTIONS WITH SPECIFIC PATTERNS
        report.AppendLine("\n--- POTENTIAL ATTRIBUTE CONTAINERS ---");
        var containerPatterns = new[]
        {
            "//div[contains(@class, 'detail')]",
            "//div[contains(@class, 'feature')]",
            "//div[contains(@class, 'property')]",
            "//div[contains(@class, 'spec')]",
            "//section[contains(@class, 'product')]",
            "//div[contains(@class, 'info')]",
            "//ul[contains(@class, 'list')]"
        };

        foreach (var pattern in containerPatterns)
        {
            var containers = htmlDoc.DocumentNode.SelectNodes(pattern);
            if (containers != null && containers.Count > 0)
            {
                report.AppendLine($"\n{pattern}: Found {containers.Count}");
                foreach (var container in containers.Take(3))
                {
                    var classes = container.GetAttributeValue("class", "");
                    report.AppendLine($"  Classes: {classes}");
                    var innerHtml = container.InnerHtml;
                    if (innerHtml.Length > 500)
                        innerHtml = innerHtml.Substring(0, 500) + "...";
                    report.AppendLine($"  Inner HTML: {innerHtml}");
                    report.AppendLine();
                }
            }
        }

        // 8. EXTRACT ALL TABLES
        report.AppendLine("\n--- TABLES ---");
        var tables = htmlDoc.DocumentNode.SelectNodes("//table");
        if (tables != null)
        {
            report.AppendLine($"Found {tables.Count} tables:");
            foreach (var table in tables)
            {
                var rows = table.SelectNodes(".//tr");
                if (rows != null)
                {
                    report.AppendLine($"  Table with {rows.Count} rows:");
                    foreach (var row in rows.Take(10))
                    {
                        var cells = row.SelectNodes(".//td | .//th");
                        if (cells != null)
                        {
                            var cellTexts = cells.Select(c => c.InnerText.Trim()).ToArray();
                            report.AppendLine($"    {string.Join(" | ", cellTexts)}");
                        }
                    }
                }
            }
        }

        // 9. FIND ALL CLASS NAMES
        report.AppendLine("\n--- ALL UNIQUE CLASS NAMES (related to attributes/details/features) ---");
        var allElements = htmlDoc.DocumentNode.SelectNodes("//*[@class]");
        if (allElements != null)
        {
            var relevantClasses = allElements
                .Select(e => e.GetAttributeValue("class", ""))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .SelectMany(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct()
                .Where(c => c.Contains("attr") || c.Contains("detail") || c.Contains("feature") || 
                           c.Contains("spec") || c.Contains("property") || c.Contains("info"))
                .OrderBy(c => c)
                .ToList();

            foreach (var className in relevantClasses.Take(100))
            {
                report.AppendLine($"  - {className}");
            }
        }

        var reportText = report.ToString();
        
        // Save to file
        var filename = $"page_analysis_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        await File.WriteAllTextAsync(filename, reportText);
        
        Console.WriteLine($"\n?? Detailed page analysis saved to: {filename}");
        
        return reportText;
    }
}
