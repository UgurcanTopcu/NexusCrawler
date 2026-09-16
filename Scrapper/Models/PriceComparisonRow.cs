namespace Scrapper.Models;

/// <summary>
/// Represents one product row in the price comparison report.
/// Holds the user's price, the best price found per marketplace, and the computed delta.
/// </summary>
public class PriceComparisonRow
{
    /// <summary>Offer id from the source Excel.</summary>
    public string OfferId { get; set; } = string.Empty;

    /// <summary>Focus category from the source Excel.</summary>
    public string FocusCategory { get; set; } = string.Empty;

    /// <summary>Category label from the source Excel.</summary>
    public string CategoryLabel { get; set; } = string.Empty;

    /// <summary>GTIN from the source Excel.</summary>
    public string Gtin { get; set; } = string.Empty;

    /// <summary>Product id from the source Excel.</summary>
    public string SourceProductId { get; set; } = string.Empty;

    /// <summary>Brand from the source Excel.</summary>
    public string SourceProductBrand { get; set; } = string.Empty;

    /// <summary>Total active offers from the source Excel.</summary>
    public string TotalActiveOffers { get; set; } = string.Empty;

    /// <summary>Stock value from the source Excel.</summary>
    public string SourceStock { get; set; } = string.Empty;

    /// <summary>Winner assortment type from the source Excel.</summary>
    public string WinnerAssortmentType { get; set; } = string.Empty;

    /// <summary>Offer score rank from the source Excel.</summary>
    public string OfferScoreRank { get; set; } = string.Empty;

    /// <summary>Seller name from the source Excel.</summary>
    public string SourceSellerName { get; set; } = string.Empty;

    /// <summary>Sold items metric from the source Excel.</summary>
    public string ProductSoldItems30d { get; set; } = string.Empty;

    /// <summary>GMV metric from the source Excel.</summary>
    public string ProductGmvInclShipping30d { get; set; } = string.Empty;

    /// <summary>PDP sessions metric from the source Excel.</summary>
    public string SessionsByProductWithPdp30d { get; set; } = string.Empty;

    /// <summary>Add to cart sessions metric from the source Excel.</summary>
    public string SessionsByProductWithAddToCartInPdp30d { get; set; } = string.Empty;

    /// <summary>Product name as provided in the input Excel.</summary>
    public string SearchName { get; set; } = string.Empty;

    /// <summary>User's own price for this product (0 when stock-out).</summary>
    public decimal MyPrice { get; set; }

    /// <summary>True when the input price cell contained "stock out" or was empty.</summary>
    public bool IsStockOut { get; set; }

    /// <summary>Product title as found on Akak�e.</summary>
    public string AkakceName { get; set; } = string.Empty;

    /// <summary>Product page URL on Akak�e.</summary>
    public string AkakceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Best (lowest in-stock) price found per marketplace.
    /// Key = normalized marketplace name (e.g. "Hepsiburada"), Value = lowest price.
    /// </summary>
    public Dictionary<string, decimal> MarketplaceBestPrices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overall best price across all marketplaces (0 if none found).</summary>
    public decimal BestPrice => MarketplaceBestPrices.Count > 0 ? MarketplaceBestPrices.Values.Min() : 0;

    // ---------------------------------------------------------------------
    // Retail (1P) vs Marketplace (3P) comparison
    //
    // The source CSV holds MediaMarkt *Marketplace* offers (third-party
    // sellers). The "MediaMarkt" entry on Akakce is MediaMarkt *Retail*
    // (first-party). Those are two different things, so they are tracked
    // separately here.
    // ---------------------------------------------------------------------

    /// <summary>
    /// MediaMarkt Retail (1P) price as listed on Akakce, taken from the store-prices
    /// block. 0 when MediaMarkt does not list this product.
    /// </summary>
    public decimal RetailPrice { get; set; }

    /// <summary>
    /// Seller name of the cheapest offer for this product in the source CSV
    /// (i.e. which marketplace seller set <see cref="MyPrice"/>).
    /// </summary>
    public string CsvCheapestSeller { get; set; } = string.Empty;

    /// <summary>
    /// True market minimum from the Akakce AggregateOffer, covering every offer on
    /// the page rather than only the enumerated ones. Preferred over <see cref="BestPrice"/>.
    /// </summary>
    public decimal MarketLowestPrice { get; set; }

    /// <summary>Total number of offers Akakce reports for this product.</summary>
    public int MarketOfferCount { get; set; }

    /// <summary>Marketplace of the cheapest enumerated seller (e.g. "Trendyol").</summary>
    public string CheapestMarketplace { get; set; } = string.Empty;

    /// <summary>Seller name of the cheapest enumerated seller.</summary>
    public string CheapestSeller { get; set; } = string.Empty;

    /// <summary>
    /// Cheapest enumerated price excluding every MediaMarkt entity (both Retail and
    /// Pazar Yeri), i.e. the genuine competitor level. 0 when nothing qualifies.
    /// </summary>
    public decimal CheapestExcludingMediaMarkt { get; set; }

    /// <summary>
    /// Per-retailer prices from the store-prices block (MediaMarkt, Teknosa, A101, ...).
    /// </summary>
    public Dictionary<string, decimal> StorePrices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Effective market minimum: the AggregateOffer low price when present, otherwise
    /// the best enumerated marketplace price.
    /// </summary>
    public decimal EffectiveMarketLowest => MarketLowestPrice > 0 ? MarketLowestPrice : BestPrice;

    /// <summary>
    /// HEADLINE METRIC. Percentage difference between MediaMarkt Retail on Akakce and
    /// the cheapest Marketplace offer from the CSV, relative to the Marketplace price.
    /// Positive = Retail is more expensive than our own marketplace sellers.
    /// Null when either side is missing or the CSV row is a stock-out.
    /// </summary>
    public decimal? RetailVsMarketplacePercent =>
        RetailPrice > 0 && MyPrice > 0 && !IsStockOut
            ? Math.Round((RetailPrice - MyPrice) / MyPrice * 100, 2)
            : null;

    /// <summary>
    /// Percentage difference between MediaMarkt Retail and the cheapest price anywhere
    /// on Akakce. Positive = Retail is above the market floor.
    /// </summary>
    public decimal? RetailVsMarketPercent =>
        RetailPrice > 0 && EffectiveMarketLowest > 0
            ? Math.Round((RetailPrice - EffectiveMarketLowest) / EffectiveMarketLowest * 100, 2)
            : null;

    /// <summary>
    /// Percentage difference between our cheapest CSV marketplace offer and the cheapest
    /// price anywhere on Akakce. Positive = our marketplace offer is above the market floor.
    /// </summary>
    public decimal? MarketplaceVsMarketPercent =>
        MyPrice > 0 && !IsStockOut && EffectiveMarketLowest > 0
            ? Math.Round((MyPrice - EffectiveMarketLowest) / EffectiveMarketLowest * 100, 2)
            : null;

    /// <summary>
    /// Delta percentage: (MyPrice - BestPrice) / BestPrice * 100.
    /// Positive = my price is higher than market best; negative = cheaper.
    /// Returns null when MyPrice is stock-out or no market price was found.
    /// </summary>
    public decimal? DeltaPercent =>
        !IsStockOut && MyPrice > 0 && BestPrice > 0
            ? Math.Round((MyPrice - BestPrice) / BestPrice * 100, 2)
            : null;

    /// <summary>Confidence score of the accepted match, for auditing the report.</summary>
    public int MatchScore { get; set; }

    /// <summary>
    /// Why the match was accepted (brand/model/attribute signals). Worth scanning when
    /// a row's price looks wrong.
    /// </summary>
    public string MatchNotes { get; set; } = string.Empty;

    /// <summary>Error message when Akak�e search/scrape failed.</summary>
    public string? ErrorMessage { get; set; }

    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
}
