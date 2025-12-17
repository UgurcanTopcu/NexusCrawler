using OfficeOpenXml;
using Scrapper.Models;

namespace Scrapper.Services;

public class ExcelExporter
{
    // Static constructor - runs once when the class is first used
    // EPPlus 8.x: LicenseContext is obsolete, use EPPlusLicense.SetNonCommercialPersonal or SetNonCommercialOrganization
    static ExcelExporter()
    {
        // Set the license for non-commercial use (update with your name or organization as needed)
        ExcelPackage.License.SetNonCommercialPersonal("Your Name");
    }

    public void ExportToExcel(List<ProductInfo> products, string filePath, bool excludePrice = false)
    {
        using var package = new ExcelPackage();
        var worksheet = package.Workbook.Worksheets.Add("Products");

        // Collect all unique attribute keys
        var allAttributeKeys = products
            .SelectMany(p => p.Attributes.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        // Add standard headers
        int col = 1;
        worksheet.Cells[1, col++].Value = "Product Name";
        worksheet.Cells[1, col++].Value = "Brand";
        
        // Conditionally add Price column
        if (!excludePrice)
        {
            worksheet.Cells[1, col++].Value = "Price";
        }
        
        // Remove Rating and Review Count columns
        worksheet.Cells[1, col++].Value = "Seller";
        worksheet.Cells[1, col++].Value = "Category";
        worksheet.Cells[1, col++].Value = "Barcode";

        // Add dynamic attribute headers (Product Features)
        int firstAttributeCol = col;
        foreach (var attrKey in allAttributeKeys)
        {
            worksheet.Cells[1, col++].Value = attrKey;
        }

        // Add remaining standard headers
        worksheet.Cells[1, col++].Value = "Product URL";
        worksheet.Cells[1, col++].Value = "Image URL";
        worksheet.Cells[1, col++].Value = "Description";

        int totalColumns = col - 1;

        // Style headers
        using (var range = worksheet.Cells[1, 1, 1, totalColumns])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightBlue);
            range.Style.Border.BorderAround(OfficeOpenXml.Style.ExcelBorderStyle.Thin);
        }

        // Add data
        for (int i = 0; i < products.Count; i++)
        {
            var product = products[i];
            int row = i + 2;
            col = 1;

            worksheet.Cells[row, col++].Value = product.Name;
            worksheet.Cells[row, col++].Value = product.Brand;
            
            // Conditionally add Price data
            if (!excludePrice)
            {
                var price = !string.IsNullOrEmpty(product.DiscountedPrice) ? product.DiscountedPrice : product.Price;
                worksheet.Cells[row, col++].Value = price;
            }
            
            // Removed Rating and Review Count
            worksheet.Cells[row, col++].Value = product.Seller;
            worksheet.Cells[row, col++].Value = product.Category;
            worksheet.Cells[row, col++].Value = product.Barcode;

            // Add attribute values (Product Features)
            foreach (var attrKey in allAttributeKeys)
            {
                worksheet.Cells[row, col++].Value = product.GetAttribute(attrKey);
            }

            // Add remaining data
            worksheet.Cells[row, col++].Value = product.ProductUrl;
            worksheet.Cells[row, col++].Value = product.ImageUrl;
            worksheet.Cells[row, col++].Value = product.Description;
        }

        // Auto-fit columns
        worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

        // Set maximum column width
        for (int i = 1; i <= totalColumns; i++)
        {
            if (worksheet.Column(i).Width > 50)
                worksheet.Column(i).Width = 50;
        }

        // Save to file
        var file = new FileInfo(filePath);
        package.SaveAs(file);

        Console.WriteLine($"\n? Excel file saved successfully: {filePath}");
        Console.WriteLine($"? Total products exported: {products.Count}");
        Console.WriteLine($"? Product features found: {allAttributeKeys.Count}");
        if (!excludePrice)
        {
            Console.WriteLine($"  Price column: Included");
        }
        else
        {
            Console.WriteLine($"  Price column: Excluded");
        }
        if (allAttributeKeys.Count > 0)
        {
            Console.WriteLine($"  Features: {string.Join(", ", allAttributeKeys)}");
        }
    }
}
