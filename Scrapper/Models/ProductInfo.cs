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
    
    /// <summary>
    /// Category ID hierarchy from Hepsiburada utagData (e.g., "2147483637 > 235604 > 234329")
    /// Used to group products by category in Excel export
    /// </summary>
    public string CategoryIdHierarchy { get; set; } = string.Empty;
    
    /// <summary>
    /// Category name hierarchy from Hepsiburada utagData (e.g., "Beyaz Esya / Mutfak > Beyaz Esya & Ankastre > Ankastre Setler")
    /// </summary>
    public string CategoryNameHierarchy { get; set; } = string.Empty;
    
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
    
    /// <summary>
    /// Gets the leaf category ID (last ID in hierarchy)
    /// Used as the key for grouping products in Excel sheets
    /// </summary>
    public string GetLeafCategoryId()
    {
        if (string.IsNullOrEmpty(CategoryIdHierarchy))
            return string.Empty;
        
        var parts = CategoryIdHierarchy.Split(new[] { " > " }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1].Trim() : string.Empty;
    }
    
    /// <summary>
    /// Gets the leaf category name (last name in hierarchy)
    /// Used as the sheet name in Excel export
    /// </summary>
    public string GetLeafCategoryName()
    {
        if (string.IsNullOrEmpty(CategoryNameHierarchy))
            return string.Empty;
        
        var parts = CategoryNameHierarchy.Split(new[] { " > " }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1].Trim() : string.Empty;
    }
}
