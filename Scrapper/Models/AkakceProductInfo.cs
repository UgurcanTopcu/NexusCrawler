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
    /// List of all sellers with their pricing and details
    /// </summary>
    public List<AkakceSellerInfo> Sellers { get; set; } = new();
    
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
