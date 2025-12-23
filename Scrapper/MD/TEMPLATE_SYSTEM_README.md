# Template-Based Export System

## Overview

The scraper now supports **template-based Excel exports** that format your scraped data to match specific platform requirements (e.g., MediaMarkt, Amazon, etc.).

## Features

- ? **Pre-defined Templates**: Ready-to-use templates for popular platforms
- ? **Automatic Mapping**: Scraped Trendyol attributes automatically map to template columns
- ? **Multi-row Headers**: Supports templates with Turkish names + technical codes (like MediaMarkt)
- ? **CDN Image Support**: Automatically uses CDN URLs when images are processed
- ? **Flexible Mapping**: Exact match, case-insensitive, and partial matching for attributes

## Available Templates

### 1. `trendyol_kettle` - MediaMarkt Kettle Template

Maps Trendyol kettle products to MediaMarkt's upload format.

**Mapped Fields:**
- Basic Info: Name, Brand, Barcode, Description
- Images: Main image + 2 additional images (CDN URLs)
- Technical Specs: Power (Watt), Hidden Resistor, Frequency, Voltage, Capacity, Auto Shutoff, Color, Material, Origin

## Usage

### Web UI

1. Go to http://localhost:5000
2. Fill in the scraping form
3. **Select a template** from the "Export Template" dropdown:
   - "Default (No Template)" - Standard export with all scraped data
   - "Trendyol Kettle (MediaMarkt)" - Format for MediaMarkt kettle uploads
4. Check "Process & Upload Images to CDN" if you want CDN URLs in the template
5. Click "Start Scraping"

### Result

The exported Excel file will have:
- **Row 1**: Turkish column names (e.g., "Kategori", "Baþlýk", "Marka")
- **Row 2**: Technical field codes (e.g., "CATEGORY", "TITLE__TR_TR", "BRAND")
- **Row 3+**: Your product data, mapped to the template columns

## Adding New Templates

### Step 1: Define the Template

Edit `Scrapper\Services\TemplateService.cs`:

```csharp
private void InitializeTemplates()
{
    // Add your new template
    var myTemplate = new ExportTemplate
    {
        Name = "trendyol_myproduct",
        Description = "My Product Template for Platform X",
        Columns = new List<TemplateColumn>
        {
            // Basic field mapping
            new() { 
                DisplayName = "Product Name",           // Row 1 (display)
                TechnicalName = "PRODUCT_NAME",         // Row 2 (technical)
                MappingHint = "Name",                   // Row 3 (hint)
                Mapping = new() { 
                    Field = ProductField.Name           // Maps to product.Name
                } 
            },
            
            // Static value
            new() { 
                DisplayName = "Category",
                TechnicalName = "CATEGORY",
                DefaultValue = "MY_CATEGORY",           // Fixed value
                Mapping = new() { 
                    Field = ProductField.StaticValue 
                } 
            },
            
            // Dynamic attribute from Trendyol
            new() { 
                DisplayName = "Power",
                TechnicalName = "PROD_FEAT_POWER",
                Mapping = new() { 
                    Field = ProductField.DynamicAttribute,
                    AttributeKey = "Güç"                // Maps to attribute "Güç"
                } 
            },
        }
    };
    
    _templates["trendyol_myproduct"] = myTemplate;
}
```

### Step 2: Add to UI

Edit `Scrapper\Program.cs` HTML section:

```html
<select id="template">
    <option value="">Default (No Template)</option>
    <option value="trendyol_kettle">?? Trendyol Kettle (MediaMarkt)</option>
    <option value="trendyol_myproduct">?? My Product Template</option>
</select>
```

## Field Mapping Options

### Basic Product Fields

```csharp
Mapping = new() { Field = ProductField.Name }           // Product name
Mapping = new() { Field = ProductField.Brand }          // Brand
Mapping = new() { Field = ProductField.Barcode }        // Barcode/EAN
Mapping = new() { Field = ProductField.Description }    // Description
Mapping = new() { Field = ProductField.Price }          // Original price
Mapping = new() { Field = ProductField.DiscountedPrice } // Discounted price
Mapping = new() { Field = ProductField.Category }       // Category breadcrumb
Mapping = new() { Field = ProductField.Seller }         // Seller name
Mapping = new() { Field = ProductField.ProductUrl }     // Product URL
```

### Image Fields

```csharp
Mapping = new() { Field = ProductField.ImageUrl }              // Original main image
Mapping = new() { Field = ProductField.CdnImageUrl }           // CDN main image
Mapping = new() { Field = ProductField.AdditionalImage1 }      // Original image 1
Mapping = new() { Field = ProductField.CdnAdditionalImage1 }   // CDN image 1
// ... up to AdditionalImage5 / CdnAdditionalImage5
```

### Dynamic Attributes

Maps to scraped Trendyol attributes:

```csharp
Mapping = new() { 
    Field = ProductField.DynamicAttribute,
    AttributeKey = "Güç"              // Exact match
}

// Matching logic:
// 1. Exact match: "Güç" == "Güç"
// 2. Case-insensitive: "güç" == "Güç"
// 3. Partial match: "Güç" matches "Güç (Watt)"
```

### Static Values

Fixed values for all products:

```csharp
DefaultValue = "KETTLE",
Mapping = new() { Field = ProductField.StaticValue }
```

## Example: Scraped Data ? Template Mapping

### Trendyol Scraped Data:
```
Name: "Tefal Su Isýtýcýsý 1.7L"
Brand: "Tefal"
Barcode: "8690798162013"
Attributes: {
    "Güç": "2200 W",
    "Renk": "Siyah",
    "Hacim": "1.7 L",
    "Otomatik Kapanma": "Var"
}
```

### Template Export Result (Row 3):
```
Kategori: "KETTLE"                              (static value)
SHOP_SKU: "8690798162013"                       (barcode)
Baþlýk: "Tefal Su Isýtýcýsý 1.7L"              (name)
EAN: "8690798162013"                            (barcode)
Marka: "Tefal"                                  (brand)
Maksimum güç: "2200 W"                          (dynamic: Güç)
Hacimsel kapasite: "1.7 L"                      (dynamic: Hacim)
Otomatik Kapama: "Var"                          (dynamic: Otomatik Kapanma)
Renk (temel): "Siyah"                           (dynamic: Renk)
Ana Ürün Görseli: "https://cdn.../image.jpg"   (CDN URL)
```

## Template Structure

```
Row 1: [Turkish Display Names]
??? "Kategori" | "SHOP_SKU" | "Baþlýk" | "Marka" | ...

Row 2: [Technical Field Codes]
??? "CATEGORY" | "SHOP_SKU" | "TITLE__TR_TR" | "BRAND" | ...

Row 3+: [Product Data]
??? "KETTLE" | "8690798..." | "Tefal Su Isýtýcýsý" | "Tefal" | ...
```

## Benefits

? **Save Time**: No manual copy-paste or Excel formatting  
? **Reduce Errors**: Automatic mapping reduces human error  
? **Platform-Ready**: Direct upload to MediaMarkt, Amazon, etc.  
? **Consistent**: Same format every time  
? **Flexible**: Easy to add new templates for different platforms/categories

## Troubleshooting

### "Template not found"
- Check the template name matches exactly (case-sensitive)
- Verify template is registered in `TemplateService.InitializeTemplates()`

### Missing Attribute Data
- Check the `AttributeKey` matches Trendyol's attribute name
- Use `Console.WriteLine` to see what attributes were scraped
- Try partial matching (the system automatically tries this)

### Images Not Showing
- Make sure "Process & Upload Images to CDN" is checked
- Verify FTP credentials are correct
- Check CDN URLs are accessible

## Next Steps

1. **Add More Templates**: Create templates for other product categories (phones, laptops, etc.)
2. **Add More Platforms**: Create templates for Amazon, N11, Hepsiburada upload formats
3. **Template Library**: Build a collection of reusable templates
4. **Dynamic Templates**: Allow users to upload their own CSV templates

---

**Need help?** Check the console output for detailed mapping information during export!
