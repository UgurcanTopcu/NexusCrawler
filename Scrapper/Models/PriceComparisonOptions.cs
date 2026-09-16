namespace Scrapper.Models;

/// <summary>
/// Scope and output settings for a price comparison run.
/// </summary>
public class PriceComparisonOptions
{
    /// <summary>
    /// Cap on how many unique products to process. Null or 0 means all of them.
    /// Used for pilot runs; the sample is spread across Focus Categories rather than
    /// taken off the top of the list.
    /// </summary>
    public int? MaxProducts { get; set; }

    /// <summary>
    /// Restrict the run to these Focus Category values (e.g. "TV", "SDA").
    /// Null or empty means no filter.
    /// </summary>
    public HashSet<string>? FocusCategories { get; set; }

    /// <summary>
    /// When true, emit the Retail vs Marketplace report instead of the original
    /// marketplace-only comparison report.
    /// </summary>
    public bool RetailReport { get; set; }

    /// <summary>
    /// How many products to match at once.
    /// </summary>
    /// <remarks>
    /// Each product costs several sequential Scrape.do calls, so a serial run averaged
    /// about 13 seconds per product - roughly six and a half hours for the full 1,815.
    /// Five concurrent workers bring that into the low hours while staying well inside
    /// normal Scrape.do concurrency allowances. Lower it if the provider starts
    /// rate-limiting.
    /// </remarks>
    public int DegreeOfParallelism { get; set; } = 5;
}
