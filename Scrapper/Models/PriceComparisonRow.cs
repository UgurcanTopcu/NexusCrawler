namespace Scrapper.Models;

/// <summary>
/// Represents one product row in the price comparison report.
/// Holds the user's price, the best price found per marketplace, and the computed delta.
/// </summary>
public class PriceComparisonRow
{
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
