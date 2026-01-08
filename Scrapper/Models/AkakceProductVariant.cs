namespace Scrapper.Models;

/// <summary>
/// Represents a product variant with specific options (e.g., storage + color)
/// </summary>
public class AkakceProductVariant
{
    /// <summary>
    /// Variant options (e.g., {"Hafýza": "128 GB", "Renk": "Mavi"})
    /// </summary>
    public Dictionary<string, string> Options { get; set; } = new();
    
    /// <summary>
    /// Display name for this variant (e.g., "128 GB - Mavi")
    /// </summary>
    public string VariantName { get; set; } = string.Empty;
    
    /// <summary>
    /// URL for this specific variant (each variant has its own product page)
    /// </summary>
    public string VariantUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Sellers for this specific variant
    /// </summary>
    public List<AkakceSellerInfo> Sellers { get; set; } = new();
    
    /// <summary>
    /// Number of sellers for this variant
    /// </summary>
    public int SellerCount => Sellers.Count;
    
    /// <summary>
    /// Lowest price for this variant (formatted with currency)
    /// </summary>
    public string LowestPrice { get; set; } = string.Empty;
    
    /// <summary>
    /// Highest price for this variant (formatted with currency)
    /// </summary>
    public string HighestPrice { get; set; } = string.Empty;
    
    /// <summary>
    /// Timestamp when this variant was scraped
    /// </summary>
    public DateTime ScrapedAt { get; set; } = DateTime.Now;
}
