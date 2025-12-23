namespace Scrapper.Models;

public class ExportTemplate
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<TemplateColumn> Columns { get; set; } = new();
}

public class TemplateColumn
{
    public string DisplayName { get; set; } = "";  // Row 1: Turkish name
    public string TechnicalName { get; set; } = ""; // Row 2: Field code
    public string MappingHint { get; set; } = "";   // Row 3: Your mapping hint
    public string? DefaultValue { get; set; }       // Optional default value
    
    // Mapping to ProductInfo fields
    public ProductFieldMapping? Mapping { get; set; }
}

public class ProductFieldMapping
{
    public ProductField Field { get; set; }
    public string? AttributeKey { get; set; } // For dynamic attributes
}

public enum ProductField
{
    // Basic fields
    Name,
    Brand,
    Price,
    DiscountedPrice,
    Seller,
    Category,
    Barcode,
    Description,
    ProductUrl,
    ImageUrl,
    
    // Image fields
    AdditionalImage1,
    AdditionalImage2,
    AdditionalImage3,
    AdditionalImage4,
    AdditionalImage5,
    
    // CDN Images
    CdnImageUrl,
    CdnAdditionalImage1,
    CdnAdditionalImage2,
    CdnAdditionalImage3,
    CdnAdditionalImage4,
    CdnAdditionalImage5,
    
    // Dynamic attribute (use AttributeKey)
    DynamicAttribute,
    
    // Static value
    StaticValue
}
