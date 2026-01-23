using OfficeOpenXml;
using OfficeOpenXml.Style;
using Scrapper.Models;
using System.Drawing;

namespace Scrapper.Services;

/// <summary>
/// Excel exporter that groups products by category hierarchy into separate sheets.
/// Each category gets its own sheet with attributes specific to that category.
/// </summary>
public class ExcelExporterWithCategories
{
    /// <summary>
    /// Export products to Excel with separate sheets for each category.
    /// Products are grouped by their leaf category (last category in hierarchy).
    /// Each sheet has attributes relevant only to that category.
    /// </summary>
    public void ExportToExcel(List<ProductInfo> products, string filePath, bool excludePrice = false, bool useCdnUrls = false, bool requireBarcode = false)
    {
        try
        {
            Console.WriteLine($"\n[Excel Export] ========================================");
            Console.WriteLine($"[Excel Export] Starting category-based export...");
            Console.WriteLine($"[Excel Export] Total products: {products.Count}");

            // Filter products based on barcode requirement
            var productsToExport = requireBarcode 
                ? products.Where(p => !string.IsNullOrWhiteSpace(p.Barcode)).ToList()
                : products;
            
            if (requireBarcode && products.Count != productsToExport.Count)
            {
                Console.WriteLine($"[Excel Export] Skipped {products.Count - productsToExport.Count} products without barcode");
            }

            // Group products by leaf category ID
            var productsByCategory = productsToExport
                .GroupBy(p => 
                {
                    var leafId = p.GetLeafCategoryId();
                    return string.IsNullOrEmpty(leafId) ? "Uncategorized" : leafId;
                })
                .ToDictionary(g => g.Key, g => g.ToList());

            Console.WriteLine($"[Excel Export] Found {productsByCategory.Count} unique categories:");
            foreach (var kvp in productsByCategory)
            {
                var sampleProduct = kvp.Value.First();
                var categoryNameHierarchy = sampleProduct.CategoryNameHierarchy;
                Console.WriteLine($"  - {kvp.Key}: {kvp.Value.Count} products ({categoryNameHierarchy})");
            }

            using var package = new ExcelPackage();

            // Create a sheet for each category
            int sheetIndex = 0;
            foreach (var categoryGroup in productsByCategory.OrderBy(g => g.Key))
            {
                sheetIndex++;
                var categoryId = categoryGroup.Key;
                var categoryProducts = categoryGroup.Value;
                
                // Get full category name hierarchy for sheet title
                var sampleProduct = categoryProducts.First();
                var categoryNameHierarchy = sampleProduct.CategoryNameHierarchy;
                
                // Create safe sheet name from the full category name hierarchy
                var sheetName = CreateSafeSheetName(categoryNameHierarchy, categoryId, sheetIndex);
                
                Console.WriteLine($"[Excel Export] Creating sheet: {sheetName} ({categoryProducts.Count} products)");
                
                CreateCategorySheet(package, sheetName, categoryProducts, excludePrice, useCdnUrls);
            }

            // Create a summary sheet as the first sheet
            CreateSummarySheet(package, productsByCategory, productsToExport.Count);
            
            // Move summary to first position
            var summarySheet = package.Workbook.Worksheets["Summary"];
            if (summarySheet != null)
            {
                package.Workbook.Worksheets.MoveToStart(summarySheet.Index);
            }

            // Save to file
            var file = new FileInfo(filePath);
            package.SaveAs(file);

            Console.WriteLine($"\n[Excel Export] SUCCESS!");
            Console.WriteLine($"[Excel Export] File saved: {filePath}");
            Console.WriteLine($"[Excel Export] Total sheets: {package.Workbook.Worksheets.Count}");
            Console.WriteLine($"[Excel Export] Total products: {productsToExport.Count}");
            Console.WriteLine($"[Excel Export] ========================================\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[Excel Export] ERROR: {ex.Message}");
            Console.WriteLine($"[Excel Export] Stack: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// Creates a sheet for a specific category with only the attributes relevant to that category
    /// </summary>
    private void CreateCategorySheet(ExcelPackage package, string sheetName, List<ProductInfo> products, bool excludePrice, bool useCdnUrls)
    {
        var worksheet = package.Workbook.Worksheets.Add(sheetName);

        // Collect all unique attribute keys for THIS category only
        var categoryAttributeKeys = products
            .SelectMany(p => p.Attributes.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        // Build headers
        int col = 1;
        worksheet.Cells[1, col++].Value = "Product Name";
        worksheet.Cells[1, col++].Value = "Brand";
        
        if (!excludePrice)
        {
            worksheet.Cells[1, col++].Value = "Price";
        }
        
        worksheet.Cells[1, col++].Value = "Seller";
        worksheet.Cells[1, col++].Value = "Category";
        worksheet.Cells[1, col++].Value = "Category ID";
        worksheet.Cells[1, col++].Value = "Barcode";

        // Add dynamic attribute headers specific to this category
        int firstAttributeCol = col;
        foreach (var attrKey in categoryAttributeKeys)
        {
            worksheet.Cells[1, col++].Value = attrKey;
        }

        // Add remaining standard headers
        worksheet.Cells[1, col++].Value = "Product URL";
        worksheet.Cells[1, col++].Value = "Image URL" + (useCdnUrls ? " (CDN)" : "");
        worksheet.Cells[1, col++].Value = "Additional Images" + (useCdnUrls ? " (CDN)" : "");
        worksheet.Cells[1, col++].Value = "Description";

        int totalColumns = col - 1;

        // Style headers
        using (var range = worksheet.Cells[1, 1, 1, totalColumns])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(68, 114, 196));
            range.Style.Font.Color.SetColor(Color.White);
            range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
        }

        // Add data rows
        for (int i = 0; i < products.Count; i++)
        {
            var product = products[i];
            int row = i + 2;
            col = 1;

            SafeSetCellValue(worksheet, row, col++, product.Name, 32760);
            SafeSetCellValue(worksheet, row, col++, product.Brand);
            
            if (!excludePrice)
            {
                var price = !string.IsNullOrEmpty(product.DiscountedPrice) ? product.DiscountedPrice : product.Price;
                SafeSetCellValue(worksheet, row, col++, price);
            }
            
            SafeSetCellValue(worksheet, row, col++, product.Seller);
            SafeSetCellValue(worksheet, row, col++, product.CategoryNameHierarchy);
            SafeSetCellValue(worksheet, row, col++, product.CategoryIdHierarchy);
            SafeSetCellValue(worksheet, row, col++, product.Barcode);

            // Add attribute values
            foreach (var attrKey in categoryAttributeKeys)
            {
                SafeSetCellValue(worksheet, row, col++, product.GetAttribute(attrKey));
            }

            // Add remaining data
            SafeSetCellValue(worksheet, row, col++, product.ProductUrl);
            
            string imageUrl;
            string additionalImagesStr;
            
            if (useCdnUrls && product.HasCdnImages())
            {
                imageUrl = product.CdnImageUrl;
                additionalImagesStr = product.CdnAdditionalImages.Count > 0 
                    ? string.Join(", ", product.CdnAdditionalImages) 
                    : "";
            }
            else
            {
                imageUrl = product.ImageUrl;
                additionalImagesStr = product.AdditionalImages.Count > 0 
                    ? string.Join(", ", product.AdditionalImages) 
                    : "";
            }
            
            SafeSetCellValue(worksheet, row, col++, imageUrl);
            SafeSetCellValue(worksheet, row, col++, additionalImagesStr, 10000);
            SafeSetCellValue(worksheet, row, col++, product.Description, 5000);
        }

        // Auto-fit columns with max width
        if (worksheet.Dimension != null)
        {
            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
            for (int i = 1; i <= totalColumns; i++)
            {
                if (worksheet.Column(i).Width > 50)
                    worksheet.Column(i).Width = 50;
            }
        }

        // Freeze header row
        worksheet.View.FreezePanes(2, 1);

        Console.WriteLine($"[Excel Export]   Sheet '{sheetName}': {products.Count} rows, {categoryAttributeKeys.Count} attributes");
    }

    /// <summary>
    /// Creates a summary sheet showing category distribution
    /// </summary>
    private void CreateSummarySheet(ExcelPackage package, Dictionary<string, List<ProductInfo>> productsByCategory, int totalProducts)
    {
        var worksheet = package.Workbook.Worksheets.Add("Summary");

        // Title
        worksheet.Cells[1, 1].Value = "Category Summary";
        worksheet.Cells[1, 1].Style.Font.Bold = true;
        worksheet.Cells[1, 1].Style.Font.Size = 14;

        // Headers
        worksheet.Cells[3, 1].Value = "Category ID";
        worksheet.Cells[3, 2].Value = "Category Name Hierarchy";
        worksheet.Cells[3, 3].Value = "Product Count";
        worksheet.Cells[3, 4].Value = "Unique Attributes";
        worksheet.Cells[3, 5].Value = "Sheet Name";

        using (var range = worksheet.Cells[3, 1, 3, 5])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(112, 173, 71));
            range.Style.Font.Color.SetColor(Color.White);
        }

        int row = 4;
        int sheetIndex = 0;
        foreach (var kvp in productsByCategory.OrderBy(g => g.Key))
        {
            sheetIndex++;
            var categoryId = kvp.Key;
            var categoryProducts = kvp.Value;
            var sampleProduct = categoryProducts.First();
            var categoryNameHierarchy = sampleProduct.CategoryNameHierarchy;
            var sheetName = CreateSafeSheetName(categoryNameHierarchy, categoryId, sheetIndex);
            
            var uniqueAttributes = categoryProducts
                .SelectMany(p => p.Attributes.Keys)
                .Distinct()
                .Count();

            worksheet.Cells[row, 1].Value = categoryId;
            worksheet.Cells[row, 2].Value = categoryNameHierarchy;
            worksheet.Cells[row, 3].Value = categoryProducts.Count;
            worksheet.Cells[row, 4].Value = uniqueAttributes;
            worksheet.Cells[row, 5].Value = sheetName;

            // Make sheet name a hyperlink
            worksheet.Cells[row, 5].Style.Font.Color.SetColor(Color.Blue);
            worksheet.Cells[row, 5].Style.Font.UnderLine = true;

            row++;
        }

        // Total row
        worksheet.Cells[row + 1, 1].Value = "TOTAL";
        worksheet.Cells[row + 1, 1].Style.Font.Bold = true;
        worksheet.Cells[row + 1, 3].Value = totalProducts;
        worksheet.Cells[row + 1, 3].Style.Font.Bold = true;

        // Auto-fit
        if (worksheet.Dimension != null)
        {
            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
            
            // Limit column widths
            for (int i = 1; i <= 5; i++)
            {
                if (worksheet.Column(i).Width > 60)
                    worksheet.Column(i).Width = 60;
            }
        }
    }

    /// <summary>
    /// Creates a safe Excel sheet name from the full category name hierarchy.
    /// Uses the full hierarchy name like "Beyaz Esya / Mutfak > Beyaz Esya & Ankastre > Ankastre Setler"
    /// but truncates and sanitizes for Excel's requirements (max 31 chars, no special chars).
    /// </summary>
    private string CreateSafeSheetName(string categoryNameHierarchy, string categoryId, int sheetIndex)
    {
        string baseName;
        
        if (string.IsNullOrEmpty(categoryNameHierarchy))
        {
            baseName = categoryId == "Uncategorized" ? "Uncategorized" : $"Category_{categoryId}";
        }
        else
        {
            // Use the full category name hierarchy
            baseName = categoryNameHierarchy;
        }

        // Remove invalid characters for Excel sheet names: : \ / ? * [ ]
        var invalidChars = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var safeName = baseName;
        foreach (var c in invalidChars)
        {
            safeName = safeName.Replace(c, '-');
        }
        
        // Replace " > " with " - " for cleaner appearance
        safeName = safeName.Replace(" > ", " - ");
        
        // Replace " & " with " and " to avoid issues
        safeName = safeName.Replace(" & ", " ve ");

        // Add index prefix to ensure uniqueness and ordering
        var prefix = $"{sheetIndex}_";
        var maxNameLength = 31 - prefix.Length; // Excel limit is 31 chars
        
        // Truncate if necessary
        if (safeName.Length > maxNameLength)
        {
            safeName = safeName.Substring(0, maxNameLength - 3) + "...";
        }

        var finalName = $"{prefix}{safeName}";

        // Final safety check
        if (finalName.Length > 31)
        {
            finalName = finalName.Substring(0, 31);
        }

        return finalName;
    }

    /// <summary>
    /// Safely sets a cell value with length limit
    /// </summary>
    private void SafeSetCellValue(ExcelWorksheet worksheet, int row, int column, string? value, int maxLength = 32767)
    {
        if (string.IsNullOrEmpty(value))
        {
            worksheet.Cells[row, column].Value = "";
            return;
        }
        
        if (value.Length > maxLength)
        {
            value = value.Substring(0, maxLength - 10) + "...";
        }
        
        worksheet.Cells[row, column].Value = value;
    }
}
