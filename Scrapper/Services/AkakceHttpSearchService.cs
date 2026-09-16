using HtmlAgilityPack;

namespace Scrapper.Services;

/// <summary>
/// Akakce product search over plain HTTP via Scrape.do.
///
/// Drop-in replacement for <see cref="AkakceScraper.SearchProductCandidatesAsync"/>,
/// which drives a real Edge browser. The search results page is fully server-rendered,
/// so a browser buys nothing here - and dropping it removes the Edge install, the
/// profile copy, the warm-up handshake and the reconnect loop, which is what stops
/// this job from running unattended.
///
/// Note: searching by GTIN does not work on Akakce - it silently returns a generic
/// "popular products" page rather than no results, which is worse than useless for
/// matching. Search by product name.
/// </summary>
public class AkakceHttpSearchService
{
    private const string SearchUrlTemplate = "https://www.akakce.com/arama/?q={0}";
    private const string BaseUrl = "https://www.akakce.com";

    private readonly ScrapeDoFetcher _fetcher;

    public AkakceHttpSearchService(ScrapeDoFetcher fetcher)
    {
        _fetcher = fetcher;
    }

    /// <summary>
    /// Run an Akakce search and return candidate products, in result order.
    /// Signature matches the Selenium implementation so callers can swap between them.
    /// </summary>
    public async Task<List<(string Title, string Url, decimal ListingPrice)>> SearchProductCandidatesAsync(
        string productName,
        int maxCandidates = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productName))
            return [];

        var url = string.Format(SearchUrlTemplate, Uri.EscapeDataString(productName));

        var (ok, _, html) = await _fetcher.GetHtmlAsync(url, cancellationToken);
        if (!ok || string.IsNullOrWhiteSpace(html))
            return [];

        return ParseSearchResults(html, maxCandidates);
    }

    /// <summary>
    /// Parse the search results list.
    ///
    /// Shape (verified against live pages):
    ///   ul#APL > li[data-pr][data-mk]
    ///     a.iC[href]        product URL
    ///     h3.pn_v8          title
    ///     span.pt_v9        price, e.g. "32.241" + &lt;i&gt;",02 TL"&lt;/i&gt;
    ///
    /// The listing price equals the product page's AggregateOffer lowPrice, so it is
    /// already the true market minimum - useful for sanity-scoring a candidate before
    /// deciding whether to spend a request on its detail page.
    /// </summary>
    internal static List<(string Title, string Url, decimal ListingPrice)> ParseSearchResults(
        string html,
        int maxCandidates)
    {
        var results = new List<(string Title, string Url, decimal ListingPrice)>();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // The main results list comes first, then anything else product-shaped on the
        // page. Akakce nests only some results directly under ul#APL - the rest sit in
        // sibling sections - so both are collected, in that priority order.
        var items = new List<HtmlNode>();

        var primary = doc.DocumentNode.SelectNodes("//ul[@id='APL']//li[@data-pr]");
        if (primary != null) items.AddRange(primary);

        var secondary = doc.DocumentNode.SelectNodes("//li[@data-pr]");
        if (secondary != null) items.AddRange(secondary);

        if (items.Count == 0) return results;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var li in items)
        {
            var anchor = li.SelectSingleNode(".//a[@href][h3]")
                         ?? li.SelectSingleNode(".//a[@href]");
            if (anchor == null) continue;

            var href = anchor.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href)) continue;

            // Only real product pages, which always end in ",<id>.html".
            if (!href.Contains(".html", StringComparison.OrdinalIgnoreCase)) continue;

            var absoluteUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : BaseUrl + href;

            if (!seen.Add(absoluteUrl)) continue;

            var titleNode = li.SelectSingleNode(".//h3");
            var title = titleNode != null
                ? System.Net.WebUtility.HtmlDecode(titleNode.InnerText).Trim()
                : System.Net.WebUtility.HtmlDecode(anchor.GetAttributeValue("title", "")).Trim();

            if (string.IsNullOrWhiteSpace(title)) continue;

            results.Add((title, absoluteUrl, ExtractListingPrice(li)));

            if (results.Count >= maxCandidates) break;
        }

        return results;
    }

    /// <summary>
    /// Read the price out of a result item.
    /// </summary>
    /// <remarks>
    /// The price node nests a sibling span holding the offer count ("+11 FIYAT"), which
    /// would otherwise be concatenated into the number. Only non-span children are read.
    /// </remarks>
    private static decimal ExtractListingPrice(HtmlNode li)
    {
        var priceNode = li.SelectSingleNode(".//span[contains(@class,'pt_v9')]")
                        ?? li.SelectSingleNode(".//span[contains(@class,'pt_v8')]");

        if (priceNode == null) return 0m;

        var text = string.Concat(
            priceNode.ChildNodes
                .Where(n => !string.Equals(n.Name, "span", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.InnerText));

        if (string.IsNullOrWhiteSpace(text))
            text = priceNode.InnerText;

        return AkakceScrapeDoService.ParseTurkishPrice(System.Net.WebUtility.HtmlDecode(text));
    }
}
