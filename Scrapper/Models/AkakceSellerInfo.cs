namespace Scrapper.Models;

/// <summary>
/// Represents a seller listing for a product on Akakce
/// </summary>
public class AkakceSellerInfo
{
    /// <summary>
    /// Seller/Store name - the actual seller (e.g., "CepHane Teknoloji", "MediaMarkt")
    /// </summary>
    public string SellerName { get; set; } = string.Empty;
    
    /// <summary>
    /// Marketplace name (e.g., "pttavm", "idefix", "hepsiburada")
    /// The platform where the seller operates
    /// </summary>
    public string Marketplace { get; set; } = string.Empty;
    
    /// <summary>
    /// Seller logo URL (SVG from Akakce CDN)
    /// </summary>
    public string SellerLogo { get; set; } = string.Empty;
    
    /// <summary>
    /// Product price from this seller (numeric value)
    /// </summary>
    public decimal Price { get; set; }
    
    /// <summary>
    /// Formatted price string with currency (e.g., "54.999,00 TL")
    /// </summary>
    public string PriceFormatted { get; set; } = string.Empty;
    
    /// <summary>
    /// Original price before discount (if applicable)
    /// </summary>
    public string OriginalPrice { get; set; } = string.Empty;
    
    /// <summary>
    /// Discount percentage (if applicable)
    /// </summary>
    public string DiscountPercentage { get; set; } = string.Empty;
    
    /// <summary>
    /// Direct link to product on seller's website
    /// </summary>
    public string ProductLink { get; set; } = string.Empty;
    
    /// <summary>
    /// Shipping cost information (e.g., "Ücretsiz Kargo", "49,90 TL")
    /// </summary>
    public string ShippingCost { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether shipping is free
    /// </summary>
    public bool FreeShipping { get; set; }
    
    /// <summary>
    /// Estimated delivery time (e.g., "1-3 iþ günü")
    /// </summary>
    public string DeliveryTime { get; set; } = string.Empty;
    
    /// <summary>
    /// Stock availability status (e.g., "Stokta", "Tükendi")
    /// </summary>
    public string StockStatus { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether the product is in stock
    /// </summary>
    public bool InStock { get; set; } = true;
    
    /// <summary>
    /// Seller rating (e.g., "9.2", "4.5/5")
    /// </summary>
    public string SellerRating { get; set; } = string.Empty;
    
    /// <summary>
    /// Number of seller reviews/ratings
    /// </summary>
    public string SellerReviewCount { get; set; } = string.Empty;
    
    /// <summary>
    /// Product rating from this seller (if different from general)
    /// </summary>
    public string ProductRating { get; set; } = string.Empty;
    
    /// <summary>
    /// Any special offers or badges (e.g., "En Düþük Fiyat", "Kampanyalý")
    /// </summary>
    public List<string> Badges { get; set; } = new();
    
    /// <summary>
    /// Payment options available (e.g., "Kredi Kartý", "Havale/EFT")
    /// </summary>
    public List<string> PaymentOptions { get; set; } = new();
    
    /// <summary>
    /// Position/rank in the seller list (1 = cheapest)
    /// </summary>
    public int Rank { get; set; }
    
    /// <summary>
    /// Additional notes or comments about this listing
    /// </summary>
    public string Notes { get; set; } = string.Empty;
    
    /// <summary>
    /// Reference to parent product's Akakce URL
    /// </summary>
    public string ParentProductUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Reference to parent product's ID
    /// </summary>
    public string ParentProductId { get; set; } = string.Empty;
    
    /// <summary>
    /// Reference to parent product's name (for flat Excel export)
    /// </summary>
    public string ParentProductName { get; set; } = string.Empty;
}
