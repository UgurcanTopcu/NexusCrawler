using HtmlAgilityPack;
using Scrapper.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scrapper.Services;

/// <summary>
/// Fetches Akakce product pages via Scrape.do and parses them.
/// No Selenium needed - works purely over HTTP.
///
/// Three different things are pulled out of a product page, and they live in
/// three different places in the markup:
///
///  1. The marketplace seller list, from the JSON-LD "offers" array. Akakce only
///     enumerates about ten of these even when the product has hundreds, so this
///     list is a sample, not the full picture.
///  2. The true market min/max/count, from the JSON-LD AggregateOffer
///     lowPrice/highPrice/offerCount fields. These DO cover every offer, which is
///     why they must be preferred over Sellers.Min(Price).
///  3. First-party retailer prices (MediaMarkt, Teknosa, A101, ...) from the
///     separate "store prices" block. MediaMarkt Retail never appears in the
///     marketplace seller list, so this block is the only way to get it.
/// </summary>
public class AkakceScrapeDoService
{
    private readonly ScrapeDoFetcher _fetcher;

    /// <summary>
    /// Cap on how many variant pages we will fetch for one product. Two, because the
    /// best name match is sometimes a colour nobody stocks while a sibling variant is
    /// on sale; beyond that the extra requests stop paying for themselves.
    /// </summary>
    private const int MAX_VARIANT_FETCHES = 2;

    private static readonly Regex ProductIdRegex = new(@",(\d+)\.html", RegexOptions.Compiled);

    /// <summary>
    /// Fallback matcher for the store-prices block, used when the DOM shape changes.
    /// Matches the Astro island props, e.g. "price":[0,5499] ... "vdName":[0,"MediaMarkt"].
    /// </summary>
    private static readonly Regex StorePricePropsRegex = new(
        @"""price"":\[0,(\d+(?:\.\d+)?)\][^}]{0,300}?""vdName"":\[0,""([^""]+)""\]",
        RegexOptions.Compiled);

    public AkakceScrapeDoService(ScrapeDoFetcher fetcher)
    {
        _fetcher = fetcher;
    }

    /// <summary>
    /// Fetch an Akakce product page via Scrape.do and extract product info, sellers,
    /// aggregate pricing and first-party retailer prices.
    /// </summary>
    /// <param name="productUrl">Akakce product page URL.</param>
    /// <param name="variantHint">
    /// Source product name. When the page turns out to be a variant group (no prices of
    /// its own), this is used to pick which variant page to follow into.
    /// </param>
    public async Task<AkakceProductInfo> ScrapeProductAsync(
        string productUrl,
        string? variantHint = null,
        CancellationToken cancellationToken = default)
    {
        var product = new AkakceProductInfo
        {
            ProductUrl = productUrl,
            ScrapedAt = DateTime.Now
        };

        var idMatch = ProductIdRegex.Match(productUrl);
        if (idMatch.Success)
            product.ProductId = idMatch.Groups[1].Value;

        try
        {
            var (ok, status, html) = await FetchHtmlAsync(productUrl, cancellationToken);
            if (!ok)
            {
                product.ErrorMessage = $"Scrape.do returned {status}";
                return product;
            }

            if (string.IsNullOrWhiteSpace(html) || html.Length < 500)
            {
                product.ErrorMessage = "Empty or too-short response from Scrape.do";
                return product;
            }

            var variantLinks = PopulateFromHtml(html, product);

            // A ProductGroup landing page (iPhone 14 Pro 256 GB) carries no prices of its
            // own - the prices live on the per-colour variant pages. Follow the variant
            // that best matches the product we were asked about.
            if (!HasAnyPrice(product) && variantLinks.Count > 0)
            {
                await FollowVariantsAsync(product, variantLinks, variantHint, cancellationToken);
            }

            if (!HasAnyPrice(product) && product.ErrorMessage == null)
            {
                product.ErrorMessage = variantLinks.Count > 0
                    ? $"Variant product - no price found on {variantLinks.Count} variant(s)"
                    : BuildNoDataDiagnostic(html);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller asked us to stop - let it propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Same exception type, but raised by HttpClient's own timeout.
            product.ErrorMessage = "Scrape.do request timed out";
        }
        catch (Exception ex)
        {
            product.ErrorMessage = $"Scrape.do error: {ex.Message}";
        }

        return product;
    }

    // =====================================================================
    // Fetching
    // =====================================================================

    private Task<(bool Ok, int Status, string Html)> FetchHtmlAsync(
        string url,
        CancellationToken cancellationToken) => _fetcher.GetHtmlAsync(url, cancellationToken);

    // =====================================================================
    // Page parsing
    // =====================================================================

    /// <summary>
    /// Parse a product page into <paramref name="product"/>.
    /// Returns the variant links found on the page (empty for a normal product).
    /// </summary>
    private static List<(string Name, string Url)> PopulateFromHtml(string html, AkakceProductInfo product)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        ParseTitle(doc, product);
        ParseImage(doc, product);

        var variantLinks = ParseJsonLd(doc, product);
        ParseStorePrices(doc, html, product);

        return variantLinks;
    }

    private static void ParseTitle(HtmlDocument doc, AkakceProductInfo product)
    {
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode == null) return;

        var title = System.Net.WebUtility.HtmlDecode(titleNode.InnerText.Trim());
        if (title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
            return;

        if (title.Contains(" | ")) title = title.Split(" | ")[0].Trim();

        // Strip the " Fiyatlari" suffix Akakce appends to page titles.
        var idx = title.IndexOf(" Fiyat", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) title = title[..idx].Trim();

        if (!string.IsNullOrWhiteSpace(title))
            product.Name = title;
    }

    private static void ParseImage(HtmlDocument doc, AkakceProductInfo product)
    {
        var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
        var imgUrl = ogImage?.GetAttributeValue("content", "");
        if (string.IsNullOrEmpty(imgUrl)) return;

        if (imgUrl.StartsWith("//")) imgUrl = "https:" + imgUrl;
        product.ImageUrl = imgUrl;
    }

    /// <summary>
    /// Extract sellers, aggregate pricing and variant links from the JSON-LD block.
    /// Returns variant links when the block is a ProductGroup.
    /// </summary>
    private static List<(string Name, string Url)> ParseJsonLd(HtmlDocument doc, AkakceProductInfo product)
    {
        var variantLinks = new List<(string Name, string Url)>();

        var scriptNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scriptNodes == null) return variantLinks;

        foreach (var scriptNode in scriptNodes)
        {
            try
            {
                // Use InnerHtml for script tags - InnerText can mangle content in HtmlAgilityPack.
                // Do NOT HtmlDecode: &quot; inside <script> is literal JSON text, not an HTML entity.
                // Decoding &quot; -> " would break JSON string boundaries (e.g. 9.06&quot; Tablet).
                var json = scriptNode.InnerHtml.Trim();
                if (string.IsNullOrEmpty(json)) continue;

                using var jsonDoc = JsonDocument.Parse(json);
                var root = jsonDoc.RootElement;

                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("@type", out var typeEl)) continue;

                var rootType = typeEl.GetString();
                if (rootType != "Product" && rootType != "ProductGroup") continue;

                if (root.TryGetProperty("brand", out var brandEl))
                    product.Brand = ReadName(brandEl);

                if (root.TryGetProperty("category", out var catEl) && catEl.ValueKind == JsonValueKind.String)
                    product.Category = catEl.GetString() ?? string.Empty;

                // Variant group: remember where the real prices live.
                if (root.TryGetProperty("hasVariant", out var variantsEl) &&
                    variantsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in variantsEl.EnumerateArray())
                    {
                        var vUrl = v.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(vUrl)) continue;

                        var vName = v.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                        variantLinks.Add((vName, vUrl!));
                    }
                }

                if (root.TryGetProperty("offers", out var offersRoot))
                {
                    ParseAggregate(offersRoot, product);
                    ParseOffers(offersRoot, product);
                }

                if (product.Sellers.Count > 0 || product.MarketLowestPrice > 0)
                    break;
            }
            catch (JsonException)
            {
                // Skip malformed JSON-LD blocks.
            }
        }

        return variantLinks;
    }

    /// <summary>
    /// Read the AggregateOffer summary. These fields span every offer on the page,
    /// unlike the "offers" array which Akakce truncates to roughly ten entries.
    /// </summary>
    private static void ParseAggregate(JsonElement offersRoot, AkakceProductInfo product)
    {
        if (offersRoot.ValueKind != JsonValueKind.Object) return;

        if (TryReadDecimal(offersRoot, "lowPrice", out var low) && low > 0)
            product.MarketLowestPrice = low;

        if (TryReadDecimal(offersRoot, "highPrice", out var high) && high > 0)
            product.MarketHighestPrice = high;

        if (offersRoot.TryGetProperty("offerCount", out var countEl))
        {
            if (countEl.ValueKind == JsonValueKind.Number && countEl.TryGetInt32(out var c))
                product.MarketOfferCount = c;
            else if (countEl.ValueKind == JsonValueKind.String &&
                     int.TryParse(countEl.GetString(), out var cs))
                product.MarketOfferCount = cs;
        }
    }

    /// <summary>
    /// Extract the enumerated marketplace sellers. Seller names come through as
    /// "marketplace/seller" (e.g. "Trendyol/Kolaysepet"); a bare name means the
    /// marketplace sells it directly.
    /// </summary>
    private static void ParseOffers(JsonElement offersRoot, AkakceProductInfo product)
    {
        var offersList = new List<JsonElement>();

        if (offersRoot.ValueKind == JsonValueKind.Object &&
            offersRoot.TryGetProperty("offers", out var nested))
        {
            if (nested.ValueKind == JsonValueKind.Array)
                offersList.AddRange(nested.EnumerateArray());
            else if (nested.ValueKind == JsonValueKind.Object)
                offersList.Add(nested);
        }
        else if (offersRoot.ValueKind == JsonValueKind.Array)
        {
            offersList.AddRange(offersRoot.EnumerateArray());
        }
        else if (offersRoot.ValueKind == JsonValueKind.Object &&
                 offersRoot.TryGetProperty("price", out _))
        {
            offersList.Add(offersRoot);
        }

        if (offersList.Count == 0) return;

        int rank = 1;
        foreach (var offer in offersList)
        {
            if (!TryReadDecimal(offer, "price", out var price) || price <= 0)
                continue;

            var marketplace = string.Empty;
            var sellerName = string.Empty;

            if (offer.TryGetProperty("seller", out var sellerEl) &&
                sellerEl.TryGetProperty("name", out var nameEl))
            {
                var fullName = nameEl.GetString() ?? string.Empty;
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

            product.Sellers.Add(new AkakceSellerInfo
            {
                Rank = rank,
                Price = price,
                PriceFormatted = FormatTurkishPrice(price),
                Marketplace = marketplace,
                SellerName = sellerName,
                ProductLink = offer.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                InStock = true,
                ParentProductUrl = product.ProductUrl,
                ParentProductId = product.ProductId,
                ParentProductName = product.Name
            });

            rank++;
        }

        product.SellerCount = product.Sellers.Count;

        if (product.Sellers.Count > 0)
        {
            var min = product.Sellers.Min(s => s.Price);
            var max = product.Sellers.Max(s => s.Price);

            // Prefer the aggregate figures - the enumerated sellers are only a sample.
            if (product.MarketLowestPrice <= 0) product.MarketLowestPrice = min;
            if (product.MarketHighestPrice <= 0) product.MarketHighestPrice = max;

            product.Sellers.First(s => s.Price == min).Badges.Add("En Ucuz");
        }

        if (product.MarketLowestPrice > 0)
            product.LowestPrice = FormatTurkishPrice(product.MarketLowestPrice);
        if (product.MarketHighestPrice > 0)
            product.HighestPrice = FormatTurkishPrice(product.MarketHighestPrice);
    }

    /// <summary>
    /// Parse the first-party retailer block, e.g.
    /// <![CDATA[<span class="iC pt_v8"><b class="v_v8"><img alt="MediaMarkt"/></b>5.499 <i>TL</i></span>]]>
    ///
    /// These entries are rendered separately from the marketplace seller list and are
    /// the only source for the MediaMarkt Retail price. The distinguishing feature is
    /// the <c>b.v_v8</c> child - marketplace sellers use <c>span.v_v8</c> instead.
    /// </summary>
    private static void ParseStorePrices(HtmlDocument doc, string rawHtml, AkakceProductInfo product)
    {
        var nodes = doc.DocumentNode.SelectNodes(
            "//span[contains(@class,'pt_v8')][b[contains(@class,'v_v8')]/img[@alt]]");

        if (nodes != null)
        {
            foreach (var span in nodes)
            {
                var alt = span.SelectSingleNode(".//b/img[@alt]")?.GetAttributeValue("alt", "");
                if (string.IsNullOrWhiteSpace(alt)) continue;

                var price = ParseTurkishPrice(System.Net.WebUtility.HtmlDecode(span.InnerText));
                if (price <= 0) continue;

                AddStorePrice(product, System.Net.WebUtility.HtmlDecode(alt), price);
            }
        }

        // Fallback: read the Astro island props if the DOM shape changed.
        if (product.StorePrices.Count == 0)
        {
            var decoded = System.Net.WebUtility.HtmlDecode(rawHtml);
            foreach (Match m in StorePricePropsRegex.Matches(decoded))
            {
                if (!decimal.TryParse(m.Groups[1].Value, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var price) || price <= 0)
                    continue;

                AddStorePrice(product, m.Groups[2].Value, price);
            }
        }
    }

    private static void AddStorePrice(AkakceProductInfo product, string store, decimal price)
    {
        store = store.Trim();
        if (store.Length == 0) return;

        if (!product.StorePrices.TryGetValue(store, out var existing) || price < existing)
            product.StorePrices[store] = price;
    }

    // =====================================================================
    // Variants
    // =====================================================================

    /// <summary>
    /// Follow the variant page that best matches <paramref name="variantHint"/> and copy
    /// its pricing onto the parent product.
    /// </summary>
    private async Task FollowVariantsAsync(
        AkakceProductInfo product,
        List<(string Name, string Url)> variantLinks,
        string? variantHint,
        CancellationToken cancellationToken)
    {
        foreach (var (name, url) in RankVariants(variantLinks, variantHint).Take(MAX_VARIANT_FETCHES))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (ok, _, html) = await FetchHtmlAsync(url, cancellationToken);
            if (!ok || string.IsNullOrWhiteSpace(html)) continue;

            var variantProduct = new AkakceProductInfo { ProductUrl = url };
            PopulateFromHtml(html, variantProduct);

            if (!HasAnyPrice(variantProduct)) continue;

            product.Variants.Add(new AkakceProductVariant
            {
                VariantName = string.IsNullOrWhiteSpace(name) ? variantProduct.Name : name,
                VariantUrl = url,
                Sellers = variantProduct.Sellers,
                LowestPrice = variantProduct.LowestPrice,
                HighestPrice = variantProduct.HighestPrice
            });

            // Lift the variant's pricing onto the parent so callers can treat it
            // like any other product.
            product.MarketLowestPrice = variantProduct.MarketLowestPrice;
            product.MarketHighestPrice = variantProduct.MarketHighestPrice;
            product.MarketOfferCount = variantProduct.MarketOfferCount;
            product.LowestPrice = variantProduct.LowestPrice;
            product.HighestPrice = variantProduct.HighestPrice;
            product.SellerCount = variantProduct.SellerCount;

            foreach (var kv in variantProduct.StorePrices)
                AddStorePrice(product, kv.Key, kv.Value);

            break;
        }
    }

    /// <summary>
    /// Order variants by how well their name overlaps the source product name, so
    /// "... 256 GB Mor" picks the Mor variant rather than an arbitrary colour.
    /// </summary>
    private static IEnumerable<(string Name, string Url)> RankVariants(
        List<(string Name, string Url)> variants,
        string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return variants;

        var hintTokens = TokenizeSimple(hint);
        if (hintTokens.Count == 0) return variants;

        return variants
            .OrderByDescending(v => TokenizeSimple(v.Name).Count(hintTokens.Contains))
            .ToList();
    }

    private static HashSet<string> TokenizeSimple(string text)
    {
        return new HashSet<string>(
            text.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '-', '/', '(', ')', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 1),
            StringComparer.OrdinalIgnoreCase);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static bool HasAnyPrice(AkakceProductInfo product) =>
        product.MarketLowestPrice > 0 || product.Sellers.Count > 0 || product.StorePrices.Count > 0;

    private static string BuildNoDataDiagnostic(string html)
    {
        var hasCloudflare = html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                            || html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase);

        return hasCloudflare
            ? "Scrape.do returned a Cloudflare challenge page"
            : $"No price data found on page (html={html.Length})";
    }

    private static string ReadName(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Object when element.TryGetProperty("name", out var n) => n.GetString() ?? string.Empty,
            _ => string.Empty
        };

    private static bool TryReadDecimal(JsonElement parent, string property, out decimal value)
    {
        value = 0m;
        if (!parent.TryGetProperty(property, out var el)) return false;

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Any,
                                        CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    /// <summary>
    /// Parse a Turkish-formatted price such as "5.499", "32.241,02 TL" or "1.234,56".
    /// </summary>
    internal static decimal ParseTurkishPrice(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;

        var cleaned = new string(raw.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());
        if (cleaned.Length == 0) return 0m;

        return decimal.TryParse(cleaned, NumberStyles.Any, new CultureInfo("tr-TR"), out var value)
            ? value
            : 0m;
    }

    private static string FormatTurkishPrice(decimal price) =>
        price.ToString("N2", new CultureInfo("tr-TR")) + " TL";
}
