namespace Scrapper.Models;

public class ProductInfo
{
    public string Name { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string Price { get; set; } = string.Empty;
    public string DiscountedPrice { get; set; } = string.Empty;
    public string Rating { get; set; } = string.Empty;
    public string ReviewCount { get; set; } = string.Empty;
    public string ProductUrl { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Seller { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    
    // Product Attributes (Öne Çýkan Özellikler)
    public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
    
    // Helper method to get attribute value
    public string GetAttribute(string key)
    {
        return Attributes.TryGetValue(key, out var value) ? value : string.Empty;
    }
}
