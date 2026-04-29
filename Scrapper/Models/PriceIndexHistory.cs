namespace Scrapper.Models;

/// <summary>
/// Persistent store for daily price snapshots, serialized to JSON on disk.
/// Each product is keyed by its rank (row position) in the uploaded file,
/// which represents GMV order. All rows are kept — no deduplication.
/// </summary>
public class PriceIndexHistory
{
    /// <summary>Key = "rank:0001" (zero-padded for correct string sort), Value = product with daily prices.</summary>
    public Dictionary<string, PriceIndexProduct> Products { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class PriceIndexProduct
{
    /// <summary>1-based rank (row position in the uploaded file = GMV order).</summary>
    public int Rank { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Gtin { get; set; } = string.Empty;
    public string OfferId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string SellerName { get; set; } = string.Empty;
    public int SoldItems30Days { get; set; }

    /// <summary>Key = "yyyy-MM-dd", Value = price on that day (0 = stock out).</summary>
    public Dictionary<string, decimal> PriceHistory { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One row in the generated Price Index report.</summary>
public class PriceIndexReportRow
{
    /// <summary>1-based rank (row position = GMV order).</summary>
    public int Rank { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Gtin { get; set; } = string.Empty;
    public string OfferId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string SellerName { get; set; } = string.Empty;
    public int SoldItems30Days { get; set; }

    public decimal TodayPrice { get; set; }
    public bool IsStockOut { get; set; }

    /// <summary>Price from the most recent previous snapshot.</summary>
    public string PreviousSnapshotDate { get; set; } = string.Empty;
    public decimal? PreviousPrice { get; set; }
    public decimal? DailyChange { get; set; }
    public decimal? DailyChangePct { get; set; }

    public string Price7DaysAgoDate { get; set; } = string.Empty;
    public decimal? Price7DaysAgo { get; set; }
    public decimal? Change7DayPct { get; set; }

    public string Price30DaysAgoDate { get; set; } = string.Empty;
    public decimal? Price30DaysAgo { get; set; }
    public decimal? Change30DayPct { get; set; }

    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
    public int SnapshotCount { get; set; }

    /// <summary>Chronologically ordered (date, price) pairs for the last 30 snapshots.</summary>
    public List<(string Date, decimal Price)> RecentHistory { get; set; } = [];
}

