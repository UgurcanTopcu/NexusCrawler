namespace Scrapper.Models;

/// <summary>
/// Represents a product scraped from Akakce with all seller listings
/// </summary>
public class AkakceProductInfo
{
    /// <summary>
    /// Original Akakce URL for this product
    /// </summary>
    public string ProductUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Product ID extracted from URL (e.g., "917781807")
    /// </summary>
    public string ProductId { get; set; } = string.Empty;
    
    /// <summary>
    /// Full product name/title
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Brand name (e.g., "Samsung", "Apple")
    /// </summary>
    public string Brand { get; set; } = string.Empty;
    
    /// <summary>
    /// Product category (e.g., "Cep Telefonu")
    /// </summary>
    public string Category { get; set; } = string.Empty;
    
    /// <summary>
    /// Category name derived from the category URL slug (e.g., "hali yikama makinesi")
    /// </summary>
    public string CategoryName { get; set; } = string.Empty;

    /// <summary>
    /// Original category URL that produced this product during category scraping
    /// </summary>
    public string SourceCategoryUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Main product image URL
    /// </summary>
    public string ImageUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Additional product images
    /// </summary>
    public List<string> AdditionalImages { get; set; } = new();
    
    /// <summary>
    /// Lowest price among all sellers (formatted with currency)
    /// </summary>
    public string LowestPrice { get; set; } = string.Empty;
    
    /// <summary>
    /// Highest price among all sellers (formatted with currency)
    /// </summary>
    public string HighestPrice { get; set; } = string.Empty;
    
    /// <summary>
    /// Number of sellers listing this product
    /// </summary>
    public int SellerCount { get; set; }

    /// <summary>
    /// True market minimum taken from the JSON-LD AggregateOffer "lowPrice" field.
    /// This covers every offer on the page, not just the ~10 the JSON-LD "offers"
    /// array actually enumerates, so it must be preferred over Sellers.Min(Price).
    /// </summary>
    public decimal MarketLowestPrice { get; set; }

    /// <summary>
    /// True market maximum from the JSON-LD AggregateOffer "highPrice" field.
    /// </summary>
    public decimal MarketHighestPrice { get; set; }

    /// <summary>
    /// Total offer count reported by the JSON-LD AggregateOffer "offerCount" field.
    /// Typically much larger than Sellers.Count (e.g. 139 vs 10).
    /// </summary>
    public int MarketOfferCount { get; set; }

    /// <summary>
    /// Prices from the "store prices" block of the product page, keyed by retailer
    /// name (e.g. "MediaMarkt", "Teknosa", "A101"). These are first-party retail
    /// listings and are rendered separately from the marketplace seller list, so
    /// they never show up in <see cref="Sellers"/>.
    /// Value = lowest price seen for that retailer.
    /// </summary>
    public Dictionary<string, decimal> StorePrices { get; set; } = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// List of all sellers with their pricing and details
    /// </summary>
    public List<AkakceSellerInfo> Sellers { get; set; } = new();
    
    /// <summary>
    /// Product variants (different storage, color, etc. combinations)
    /// </summary>
    public List<AkakceProductVariant> Variants { get; set; } = new();
    
    /// <summary>
    /// Whether this product has variants
    /// </summary>
    public bool HasVariants => Variants.Count > 0;
    
    /// <summary>
    /// Product specifications/attributes
    /// </summary>
    public Dictionary<string, string> Specifications { get; set; } = new();
    
    /// <summary>
    /// Product description or summary
    /// </summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Average rating across all sellers (if available)
    /// </summary>
    public string Rating { get; set; } = string.Empty;
    
    /// <summary>
    /// Total review count (if available)
    /// </summary>
    public string ReviewCount { get; set; } = string.Empty;
    
    /// <summary>
    /// Timestamp when this product was scraped
    /// </summary>
    public DateTime ScrapedAt { get; set; } = DateTime.Now;
    
    /// <summary>
    /// Any error message if scraping failed
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// Whether scraping was successful
    /// </summary>
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
    
    /// <summary>
    /// Get specification value by key (case-insensitive)
    /// </summary>
    public string GetSpecification(string key)
    {
        if (Specifications.TryGetValue(key, out var value))
            return value;
            
        // Try case-insensitive match
        var matchingKey = Specifications.Keys.FirstOrDefault(k => 
            k.Equals(key, StringComparison.OrdinalIgnoreCase));
            
        return matchingKey != null ? Specifications[matchingKey] : string.Empty;
    }
}
