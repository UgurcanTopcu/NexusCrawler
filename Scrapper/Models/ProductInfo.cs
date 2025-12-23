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
    
    // Product identification
    public string ProductId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // "trendyol" or "hepsiburada"
    
    // Original image URLs (from scraping)
    public string ImageUrl { get; set; } = string.Empty;
    public List<string> AdditionalImages { get; set; } = new List<string>();
    
    // CDN URLs (processed and uploaded)
    public string CdnImageUrl { get; set; } = string.Empty;
    public List<string> CdnAdditionalImages { get; set; } = new List<string>();
    
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
    
    // Helper method to get all image URLs (main + additional)
    public List<string> GetAllImages()
    {
        var allImages = new List<string>();
        if (!string.IsNullOrEmpty(ImageUrl))
            allImages.Add(ImageUrl);
        allImages.AddRange(AdditionalImages);
        return allImages;
    }
    
    // Helper method to get all CDN URLs (main + additional)
    public List<string> GetAllCdnImages()
    {
        var allImages = new List<string>();
        if (!string.IsNullOrEmpty(CdnImageUrl))
            allImages.Add(CdnImageUrl);
        allImages.AddRange(CdnAdditionalImages);
        return allImages;
    }
    
    // Check if images have been processed to CDN
    public bool HasCdnImages()
    {
        return !string.IsNullOrEmpty(CdnImageUrl);
    }
}
