using HtmlAgilityPack;
using Scrapper.Models;
using System.Globalization;
using System.Text.Json;

namespace Scrapper.Services;

/// <summary>
/// Fetches Akakce product pages via Scrape.do API and parses seller data from JSON-LD.
/// No Selenium needed — works purely over HTTP.
/// </summary>
public class AkakceScrapeDoService
{
    private readonly HttpClient _httpClient;
    private readonly ScrapeDoConfig _config;

    public AkakceScrapeDoService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _config = new ScrapeDoConfig();
    }

    /// <summary>
    /// Fetch an Akakce product page via Scrape.do and extract product info + sellers from JSON-LD.
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
            var idMatch = System.Text.RegularExpressions.Regex.Match(productUrl, @",(\d+)\.html$");
            if (idMatch.Success)
                product.ProductId = idMatch.Groups[1].Value;

            var encodedUrl = System.Net.WebUtility.UrlEncode(productUrl);
            var apiUrl = $"{_config.BaseUrl}?url={encodedUrl}&token={_config.ApiToken}";

            var response = await _httpClient.GetAsync(apiUrl);
            if (!response.IsSuccessStatusCode)
            {
                product.ErrorMessage = $"Scrape.do returned {(int)response.StatusCode}";
                return product;
            }

            var html = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(html) || html.Length < 500)
            {
                product.ErrorMessage = "Empty or too-short response from Scrape.do";
                return product;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Extract product name from <title>
            var titleNode = doc.DocumentNode.SelectSingleNode("//title");
            if (titleNode != null)
            {
                var title = System.Net.WebUtility.HtmlDecode(titleNode.InnerText.Trim());
                if (!title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
                {
                    if (title.Contains(" | ")) title = title.Split(" | ")[0].Trim();
                    if (title.Contains(" Fiyatlarý")) title = title.Split(" Fiyatlarý")[0].Trim();
                    product.Name = title;
                }
            }

            // Extract image
            var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
            if (ogImage != null)
            {
                var imgUrl = ogImage.GetAttributeValue("content", "");
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    if (imgUrl.StartsWith("//")) imgUrl = "https:" + imgUrl;
                    product.ImageUrl = imgUrl;
                }
            }

            // Parse sellers from JSON-LD (the reliable source in Scrape.do HTML)
            ParseSellersFromJsonLd(doc, product);

            if (product.Sellers.Count == 0)
            {
                // Diagnostic: check what we got
                var scriptNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
                var scriptCount = scriptNodes?.Count ?? 0;
                var htmlLen = html.Length;
                var hasCloudflare = html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase);

                Console.WriteLine($"[ScrapeDoAkakce] No sellers for {productUrl}");
                Console.WriteLine($"  HTML length: {htmlLen}, JSON-LD scripts found: {scriptCount}, Cloudflare page: {hasCloudflare}");
                if (scriptCount > 0)
                {
                    foreach (var s in scriptNodes!)
                    {
                        var preview = s.InnerHtml.Length > 200 ? s.InnerHtml[..200] : s.InnerHtml;
                        Console.WriteLine($"  JSON-LD preview: {preview}");
                    }
                }

                product.ErrorMessage = hasCloudflare
                    ? "Scrape.do returned a Cloudflare challenge page"
                    : $"No sellers found in JSON-LD (html={htmlLen}, scripts={scriptCount})";
            }
        }
        catch (TaskCanceledException)
        {
            product.ErrorMessage = "Scrape.do request timed out";
        }
        catch (Exception ex)
        {
            product.ErrorMessage = $"Scrape.do error: {ex.Message}";
        }

        return product;
    }

    /// <summary>
    /// Extract seller offers from the JSON-LD script block embedded in the Akakce page.
    /// </summary>
    private static void ParseSellersFromJsonLd(HtmlDocument doc, AkakceProductInfo product)
    {
        var scriptNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scriptNodes == null) return;

        foreach (var scriptNode in scriptNodes)
        {
            try
            {
                // Use InnerHtml for script tags — InnerText can mangle content in HtmlAgilityPack.
                // Do NOT HtmlDecode: &quot; inside <script> is literal JSON text, not an HTML entity.
                // Decoding &quot; ? " would break JSON string boundaries (e.g. 9.06&quot; Tablet).
                var json = scriptNode.InnerHtml.Trim();
                if (string.IsNullOrEmpty(json)) continue;

                using var jsonDoc = JsonDocument.Parse(json);
                var root = jsonDoc.RootElement;

                if (!root.TryGetProperty("@type", out var typeEl))
                    continue;

                var rootType = typeEl.GetString();
                // Akakce uses "Product" for single products and "ProductGroup" for grouped/variant products
                if (rootType != "Product" && rootType != "ProductGroup")
                    continue;

                if (!root.TryGetProperty("offers", out var offersRoot))
                    continue;

                // Get the offers — may be nested under AggregateOffer, a direct array, or a single Offer
                var offersList = new List<JsonElement>();

                if (offersRoot.TryGetProperty("offers", out var nested))
                {
                    // AggregateOffer with nested "offers" array
                    if (nested.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in nested.EnumerateArray())
                            offersList.Add(item);
                    }
                    else if (nested.ValueKind == JsonValueKind.Object)
                    {
                        offersList.Add(nested);
                    }
                }
                else if (offersRoot.ValueKind == JsonValueKind.Array)
                {
                    // Direct array of offers
                    foreach (var item in offersRoot.EnumerateArray())
                        offersList.Add(item);
                }
                else if (offersRoot.ValueKind == JsonValueKind.Object &&
                         offersRoot.TryGetProperty("price", out _))
                {
                    // Single Offer object
                    offersList.Add(offersRoot);
                }

                if (offersList.Count == 0) continue;

                int rank = 1;
                decimal? lowestPrice = null;
                decimal? highestPrice = null;

                foreach (var offer in offersList)
                {
                    if (!offer.TryGetProperty("price", out var priceEl))
                        continue;

                    decimal price = priceEl.ValueKind == JsonValueKind.String
                        ? decimal.TryParse(priceEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0
                        : priceEl.TryGetDecimal(out var d) ? d : 0;

                    if (price <= 0) continue;

                    var marketplace = "";
                    var sellerName = "";

                    if (offer.TryGetProperty("seller", out var sellerEl) &&
                        sellerEl.TryGetProperty("name", out var nameEl))
                    {
                        var fullName = nameEl.GetString() ?? "";
                        var slashIdx = fullName.IndexOf('/');
                        if (slashIdx > 0)
                        {
                            marketplace = fullName[..slashIdx].Trim();
                            sellerName = fullName[(slashIdx + 1)..].Trim();
                        }
                        else
                        {
                            marketplace = fullName.Trim();
                        }
                    }

                    if (string.IsNullOrEmpty(marketplace)) continue;

                    var sellerUrl = offer.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";

                    var seller = new AkakceSellerInfo
                    {
                        Rank = rank,
                        Price = price,
                        PriceFormatted = FormatTurkishPrice(price),
                        Marketplace = marketplace,
                        SellerName = sellerName,
                        ProductLink = sellerUrl,
                        InStock = true,
                        ParentProductUrl = product.ProductUrl,
                        ParentProductId = product.ProductId,
                        ParentProductName = product.Name
                    };

                    if (rank == 1) seller.Badges.Add("En Ucuz");

                    product.Sellers.Add(seller);

                    if (!lowestPrice.HasValue || price < lowestPrice) lowestPrice = price;
                    if (!highestPrice.HasValue || price > highestPrice) highestPrice = price;

                    rank++;
                }

                product.SellerCount = product.Sellers.Count;
                if (lowestPrice.HasValue) product.LowestPrice = FormatTurkishPrice(lowestPrice.Value);
                if (highestPrice.HasValue) product.HighestPrice = FormatTurkishPrice(highestPrice.Value);

                // Found sellers, no need to check other script blocks
                if (product.Sellers.Count > 0) return;
            }
            catch
            {
                // Skip malformed JSON-LD blocks
            }
        }
    }

    private static string FormatTurkishPrice(decimal price)
    {
        return price.ToString("N2", new CultureInfo("tr-TR")) + " TL";
    }
}
