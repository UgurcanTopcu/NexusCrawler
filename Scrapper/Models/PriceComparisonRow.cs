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

    /// <summary>Product title as found on Akakçe.</summary>
    public string AkakceName { get; set; } = string.Empty;

    /// <summary>Product page URL on Akakçe.</summary>
    public string AkakceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Best (lowest in-stock) price found per marketplace.
    /// Key = normalized marketplace name (e.g. "Hepsiburada"), Value = lowest price.
    /// </summary>
    public Dictionary<string, decimal> MarketplaceBestPrices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overall best price across all marketplaces (0 if none found).</summary>
    public decimal BestPrice => MarketplaceBestPrices.Count > 0 ? MarketplaceBestPrices.Values.Min() : 0;

    /// <summary>
    /// Delta percentage: (MyPrice - BestPrice) / BestPrice * 100.
    /// Positive = my price is higher than market best; negative = cheaper.
    /// Returns null when MyPrice is stock-out or no market price was found.
    /// </summary>
    public decimal? DeltaPercent =>
        !IsStockOut && MyPrice > 0 && BestPrice > 0
            ? Math.Round((MyPrice - BestPrice) / BestPrice * 100, 2)
            : null;

    /// <summary>Error message when Akakçe search/scrape failed.</summary>
    public string? ErrorMessage { get; set; }

    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
}
