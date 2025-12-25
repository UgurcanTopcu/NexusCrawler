using OfficeOpenXml;
using Scrapper.Models;

namespace Scrapper.Services;

public class TemplateExcelExporter
{
    public void ExportWithTemplate(
        List<ProductInfo> products, 
        string filePath, 
        ExportTemplate template,
        bool useCdnUrls = false)
    {
        try
        {
            // EPPlus license already set in Program.cs
            Console.WriteLine($"\n[Template Export] Using template: {template.Name}");
            Console.WriteLine($"[Template Export] Columns: {template.Columns.Count}");
            Console.WriteLine($"[Template Export] Products: {products.Count}");
            
            // Analyze which attributes are missing from the template
            if (products.Count > 0)
            {
                Console.WriteLine($"\n[Template Export] Analyzing attribute coverage...");
                var allScrapedAttributes = new HashSet<string>();
                foreach (var product in products)
                {
                    foreach (var key in product.Attributes.Keys)
                    {
                        allScrapedAttributes.Add(key);
                    }
                }
                
                var templateAttributeKeys = template.Columns
                    .Where(c => c.Mapping?.Field == ProductField.DynamicAttribute && !string.IsNullOrEmpty(c.Mapping.AttributeKey))
                    .Select(c => c.Mapping!.AttributeKey!)
                    .ToList();
                
                var unmappedAttributes = allScrapedAttributes
                    .Where(scrapedKey => !templateAttributeKeys.Any(templateKey =>
                        scrapedKey.Equals(templateKey, StringComparison.OrdinalIgnoreCase) ||
                        scrapedKey.Contains(templateKey, StringComparison.OrdinalIgnoreCase) ||
                        templateKey.Contains(scrapedKey, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(k => k)
                    .ToList();
                
                if (unmappedAttributes.Any())
                {
                    Console.WriteLine($"\n??  WARNING: {unmappedAttributes.Count} scraped attributes are NOT mapped in the template:");
                    foreach (var attr in unmappedAttributes)
                    {
                        // Show example value from first product
                        var exampleProduct = products.FirstOrDefault(p => p.Attributes.ContainsKey(attr));
                        var exampleValue = exampleProduct?.Attributes[attr] ?? "";
                        if (exampleValue.Length > 50) exampleValue = exampleValue.Substring(0, 50) + "...";
                        Console.WriteLine($"   ? '{attr}' (example: '{exampleValue}')");
                    }
                    Console.WriteLine($"\n?? TIP: Add these attributes to your template to avoid data loss!\n");
                }
                else
                {
                    Console.WriteLine($"? All scraped attributes are mapped in the template!");
                }
            }

            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("Products");

            // ROW 1: Turkish Display Names
            for (int col = 0; col < template.Columns.Count; col++)
            {
                worksheet.Cells[1, col + 1].Value = template.Columns[col].DisplayName;
            }

            // ROW 2: Technical Field Codes
            for (int col = 0; col < template.Columns.Count; col++)
            {
                worksheet.Cells[2, col + 1].Value = template.Columns[col].TechnicalName;
            }

            // Style header rows
            using (var headerRange = worksheet.Cells[1, 1, 2, template.Columns.Count])
            {
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                headerRange.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightBlue);
                headerRange.Style.Border.BorderAround(OfficeOpenXml.Style.ExcelBorderStyle.Thin);
            }

            // DATA ROWS (starting from row 3)
            for (int i = 0; i < products.Count; i++)
            {
                var product = products[i];
                int row = i + 3; // Start from row 3

                for (int col = 0; col < template.Columns.Count; col++)
                {
                    var column = template.Columns[col];
                    var value = GetMappedValue(product, column, useCdnUrls);
                    
                    SetCellValue(worksheet, row, col + 1, value);
                }
            }

            // Auto-fit columns
            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

            // Set maximum column width
            for (int i = 1; i <= template.Columns.Count; i++)
            {
                if (worksheet.Column(i).Width > 50)
                    worksheet.Column(i).Width = 50;
            }

            // Save file
            var file = new FileInfo(filePath);
            package.SaveAs(file);

            Console.WriteLine($"\n? [Template Export] File saved: {filePath}");
            Console.WriteLine($"   Products exported: {products.Count}");
            Console.WriteLine($"   Template: {template.Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n? [Template Export] Error: {ex.Message}");
            Console.WriteLine($"   Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private string GetMappedValue(ProductInfo product, TemplateColumn column, bool useCdnUrls)
    {
        // If no mapping, use default value
        if (column.Mapping == null)
        {
            return column.DefaultValue ?? "";
        }

        switch (column.Mapping.Field)
        {
            case ProductField.StaticValue:
                return column.DefaultValue ?? "";

            case ProductField.Name:
                return product.Name ?? "";

            case ProductField.Brand:
                return product.Brand ?? "";

            case ProductField.Price:
                return product.Price ?? "";

            case ProductField.DiscountedPrice:
                return product.DiscountedPrice ?? product.Price ?? "";

            case ProductField.Seller:
                return product.Seller ?? "";

            case ProductField.Category:
                return product.Category ?? "";

            case ProductField.Barcode:
                return product.Barcode ?? "";

            case ProductField.Description:
                var desc = product.Description ?? "";
                // Limit description length
                if (desc.Length > 5000)
                    desc = desc.Substring(0, 5000) + "...";
                return desc;

            case ProductField.ProductUrl:
                return product.ProductUrl ?? "";

            case ProductField.ImageUrl:
                return useCdnUrls && !string.IsNullOrEmpty(product.CdnImageUrl)
                    ? product.CdnImageUrl
                    : product.ImageUrl ?? "";

            case ProductField.CdnImageUrl:
                // Use CDN URL if available, otherwise fallback to original
                var mainImageUrl = useCdnUrls && !string.IsNullOrEmpty(product.CdnImageUrl)
                    ? product.CdnImageUrl
                    : product.ImageUrl ?? "";
                    
                Console.WriteLine($"[Template] Main Image for '{product.Name}':");
                Console.WriteLine($"   useCdnUrls: {useCdnUrls}");
                Console.WriteLine($"   CdnImageUrl: {(string.IsNullOrEmpty(product.CdnImageUrl) ? "NULL" : product.CdnImageUrl)}");
                Console.WriteLine($"   ImageUrl (fallback): {(string.IsNullOrEmpty(product.ImageUrl) ? "NULL" : product.ImageUrl)}");
                Console.WriteLine($"   Final value: {(string.IsNullOrEmpty(mainImageUrl) ? "EMPTY" : mainImageUrl)}");
                return mainImageUrl;

            // Additional images
            case ProductField.AdditionalImage1:
            case ProductField.CdnAdditionalImage1:
                return GetAdditionalImage(product, 0, useCdnUrls);

            case ProductField.AdditionalImage2:
            case ProductField.CdnAdditionalImage2:
                return GetAdditionalImage(product, 1, useCdnUrls);

            case ProductField.AdditionalImage3:
            case ProductField.CdnAdditionalImage3:
                return GetAdditionalImage(product, 2, useCdnUrls);

            case ProductField.AdditionalImage4:
            case ProductField.CdnAdditionalImage4:
                return GetAdditionalImage(product, 3, useCdnUrls);

            case ProductField.AdditionalImage5:
            case ProductField.CdnAdditionalImage5:
                return GetAdditionalImage(product, 4, useCdnUrls);

            // Dynamic attribute
            case ProductField.DynamicAttribute:
                if (!string.IsNullOrEmpty(column.Mapping.AttributeKey))
                {
                    // Try exact match first
                    if (product.Attributes.TryGetValue(column.Mapping.AttributeKey, out var value))
                    {
                        return value;
                    }

                    // Try case-insensitive match
                    var key = product.Attributes.Keys.FirstOrDefault(k => 
                        k.Equals(column.Mapping.AttributeKey, StringComparison.OrdinalIgnoreCase));
                    
                    if (key != null)
                    {
                        return product.Attributes[key];
                    }

                    // Try partial match (contains)
                    key = product.Attributes.Keys.FirstOrDefault(k => 
                        k.Contains(column.Mapping.AttributeKey, StringComparison.OrdinalIgnoreCase) ||
                        column.Mapping.AttributeKey.Contains(k, StringComparison.OrdinalIgnoreCase));
                    
                    if (key != null)
                    {
                        return product.Attributes[key];
                    }
                }
                return "";

            default:
                return "";
        }
    }

    private string GetAdditionalImage(ProductInfo product, int index, bool useCdnUrls)
    {
        string imageUrl = "";
        
        // When using CDN URLs (processImages = true)
        if (useCdnUrls)
        {
            // Try CDN additional images first
            if (product.CdnAdditionalImages.Count > index)
            {
                imageUrl = product.CdnAdditionalImages[index];
                Console.WriteLine($"[Template] Additional Image {index + 1} for '{product.Name}': CDN URL = {imageUrl}");
                return imageUrl;
            }
            
            // Fallback to original additional images
            if (product.AdditionalImages.Count > index)
            {
                imageUrl = product.AdditionalImages[index];
                Console.WriteLine($"[Template] Additional Image {index + 1} for '{product.Name}': CDN fallback to original = {imageUrl}");
                return imageUrl;
            }
            
            Console.WriteLine($"[Template] Additional Image {index + 1} for '{product.Name}': NOT FOUND (CDN={product.CdnAdditionalImages.Count}, Original={product.AdditionalImages.Count})");
        }
        else
        {
            // When NOT using CDN (processImages = false), use original additional images
            if (product.AdditionalImages.Count > index)
            {
                imageUrl = product.AdditionalImages[index];
                Console.WriteLine($"[Template] Additional Image {index + 1} for '{product.Name}': Original URL = {imageUrl}");
                return imageUrl;
            }
            
            Console.WriteLine($"[Template] Additional Image {index + 1} for '{product.Name}': NOT FOUND (Original={product.AdditionalImages.Count} total)");
        }

        return "";
    }

    private void SetCellValue(ExcelWorksheet worksheet, int row, int col, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            worksheet.Cells[row, col].Value = "";
            return;
        }

        // Limit cell value length to Excel's limit
        const int maxLength = 32767;
        if (value.Length > maxLength)
        {
            value = value.Substring(0, maxLength - 10) + "...";
        }

        worksheet.Cells[row, col].Value = value;
    }
}
