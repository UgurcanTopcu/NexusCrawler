using OfficeOpenXml;
using OfficeOpenXml.Style;
using Scrapper.Models;
using System.Drawing;

namespace Scrapper.Services;

/// <summary>
/// Export Akakce scraped data to Excel with multiple sheets
/// </summary>
public class AkakceExcelExporter
{
    /// <summary>
    /// Export products and sellers to Excel with separate sheets
    /// </summary>
    public void Export(List<AkakceProductInfo> products, string filePath)
    {
        try
        {
            Console.WriteLine($"\n[Akakce Export] ========== EXPORT START ==========");
            Console.WriteLine($"[Akakce Export] Creating Excel file: {filePath}");
            Console.WriteLine($"[Akakce Export] Products to export: {products.Count}");
            Console.WriteLine($"[Akakce Export] Total sellers across all products: {products.Sum(p => p.Sellers.Count)}");

            if (products.Count == 0)
            {
                Console.WriteLine($"[Akakce Export] WARNING: No products to export!");
                throw new Exception("No products data to export");
            }

            // Debug: Show first product details
            if (products.Count > 0)
            {
                var first = products[0];
                Console.WriteLine($"[Akakce Export] First product: {first.Name}");
                Console.WriteLine($"[Akakce Export] First product sellers: {first.Sellers.Count}");
                Console.WriteLine($"[Akakce Export] First product URL: {first.ProductUrl}");
            }

            // EPPlus license already set in Program.cs
            using var package = new ExcelPackage();

            // Sheet 1: Product Summary
            Console.WriteLine($"[Akakce Export] Creating Products Summary sheet...");
            CreateProductSummarySheet(package, products);

            // Sheet 2: All Sellers (flat list)
            Console.WriteLine($"[Akakce Export] Creating All Sellers sheet...");
            CreateSellersSheet(package, products);

            // Sheet 3: Detailed view (one row per seller with product info)
            Console.WriteLine($"[Akakce Export] Creating Detailed View sheet...");
            CreateDetailedSheet(package, products);

            // Save
            var file = new FileInfo(filePath);
            Console.WriteLine($"[Akakce Export] Saving to: {file.FullName}");
            package.SaveAs(file);

            Console.WriteLine($"[Akakce Export] SUCCESS: File saved with {package.Workbook.Worksheets.Count} sheets");
            Console.WriteLine($"[Akakce Export] ========== EXPORT END ==========\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[Akakce Export] ERROR: {ex.Message}");
            Console.WriteLine($"[Akakce Export] Stack: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"[Akakce Export] Inner Exception: {ex.InnerException.Message}");
            }
            throw;
        }
    }

    private void CreateProductSummarySheet(ExcelPackage package, List<AkakceProductInfo> products)
    {
        try
        {
            var ws = package.Workbook.Worksheets.Add("Products Summary");

            // Headers
            var headers = new[]
            {
                "Product ID", "Product Name", "Brand", "Category",
                "Lowest Price", "Highest Price", "Seller Count",
                "Image URL", "Product URL", "Scraped At", "Status"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cells[1, i + 1].Value = headers[i];
            }

            // Style header
            using (var range = ws.Cells[1, 1, 1, headers.Length])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(68, 114, 196));
                range.Style.Font.Color.SetColor(Color.White);
                range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
            }

            // Data rows
            for (int i = 0; i < products.Count; i++)
            {
                var p = products[i];
                int row = i + 2;

                ws.Cells[row, 1].Value = p.ProductId;
                ws.Cells[row, 2].Value = p.Name;
                ws.Cells[row, 3].Value = p.Brand;
                ws.Cells[row, 4].Value = p.Category;
                ws.Cells[row, 5].Value = p.LowestPrice;
                ws.Cells[row, 6].Value = p.HighestPrice;
                ws.Cells[row, 7].Value = p.SellerCount;
                ws.Cells[row, 8].Value = p.ImageUrl;
                ws.Cells[row, 9].Value = p.ProductUrl;
                ws.Cells[row, 10].Value = p.ScrapedAt.ToString("yyyy-MM-dd HH:mm");
                ws.Cells[row, 11].Value = p.IsSuccess ? "Success" : $"Error: {p.ErrorMessage}";

                // Color code status
                if (!p.IsSuccess)
                {
                    ws.Cells[row, 11].Style.Font.Color.SetColor(Color.Red);
                }
                else
                {
                    ws.Cells[row, 11].Style.Font.Color.SetColor(Color.Green);
                }
            }

            // Auto-fit and set max width
            if (ws.Dimension != null)
            {
                ws.Cells[ws.Dimension.Address].AutoFitColumns();
                for (int i = 1; i <= headers.Length; i++)
                {
                    if (ws.Column(i).Width > 50)
                        ws.Column(i).Width = 50;
                }
            }

            // Freeze header row
            ws.View.FreezePanes(2, 1);
            
            Console.WriteLine($"[Akakce Export] Products Summary: {products.Count} rows created");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce Export] Error in CreateProductSummarySheet: {ex.Message}");
            throw;
        }
    }

    private void CreateSellersSheet(ExcelPackage package, List<AkakceProductInfo> products)
    {
        try
        {
            var ws = package.Workbook.Worksheets.Add("All Sellers");

            // Headers - added Marketplace column
            var headers = new[]
            {
                "Product ID", "Product Name", "Rank", "Marketplace", "Seller Name", 
                "Price", "Price (Numeric)", "Original Price", "Discount",
                "Shipping", "Free Shipping", "Delivery Time", "Stock Status", "In Stock",
                "Seller Rating", "Product Link", "Badges", "Notes"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cells[1, i + 1].Value = headers[i];
            }

            // Style header
            using (var range = ws.Cells[1, 1, 1, headers.Length])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(112, 173, 71));
                range.Style.Font.Color.SetColor(Color.White);
                range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
            }

            // Data rows - flatten all sellers
            int row = 2;
            int sellerCount = 0;
            foreach (var product in products)
            {
                foreach (var seller in product.Sellers)
                {
                    ws.Cells[row, 1].Value = product.ProductId;
                    ws.Cells[row, 2].Value = product.Name;
                    ws.Cells[row, 3].Value = seller.Rank;
                    ws.Cells[row, 4].Value = seller.Marketplace; // NEW: Marketplace column
                    ws.Cells[row, 5].Value = seller.SellerName;  // Actual seller name
                    ws.Cells[row, 6].Value = seller.PriceFormatted;
                    ws.Cells[row, 7].Value = seller.Price;
                    ws.Cells[row, 8].Value = seller.OriginalPrice;
                    ws.Cells[row, 9].Value = seller.DiscountPercentage;
                    ws.Cells[row, 10].Value = seller.ShippingCost;
                    ws.Cells[row, 11].Value = seller.FreeShipping ? "Yes" : "No";
                    ws.Cells[row, 12].Value = seller.DeliveryTime;
                    ws.Cells[row, 13].Value = seller.StockStatus;
                    ws.Cells[row, 14].Value = seller.InStock ? "Yes" : "No";
                    ws.Cells[row, 15].Value = seller.SellerRating;
                    ws.Cells[row, 16].Value = seller.ProductLink;
                    ws.Cells[row, 17].Value = string.Join(", ", seller.Badges);
                    ws.Cells[row, 18].Value = seller.Notes;

                    // Highlight lowest price (rank 1)
                    if (seller.Rank == 1)
                    {
                        using (var rankRange = ws.Cells[row, 1, row, headers.Length])
                        {
                            rankRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
                            rankRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(226, 239, 218));
                        }
                    }

                    row++;
                    sellerCount++;
                }
            }

            // Auto-fit
            if (ws.Dimension != null)
            {
                ws.Cells[ws.Dimension.Address].AutoFitColumns();
                for (int i = 1; i <= headers.Length; i++)
                {
                    if (ws.Column(i).Width > 50)
                        ws.Column(i).Width = 50;
                }
            }

            ws.View.FreezePanes(2, 1);
            
            Console.WriteLine($"[Akakce Export] All Sellers: {sellerCount} rows created");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce Export] Error in CreateSellersSheet: {ex.Message}");
            throw;
        }
    }

    private void CreateDetailedSheet(ExcelPackage package, List<AkakceProductInfo> products)
    {
        try
        {
            var ws = package.Workbook.Worksheets.Add("Detailed View");

            // Headers - comprehensive view with Marketplace
            var headers = new[]
            {
                "Product ID", "Product Name", "Brand", "Category",
                "Seller Rank", "Marketplace", "Seller Name", "Price", "Price (TL)",
                "Shipping", "Free Shipping", "In Stock",
                "Seller Rating", "Product Link",
                "Lowest Price", "Highest Price", "Total Sellers",
                "Image URL", "Akakce URL"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cells[1, i + 1].Value = headers[i];
            }

            // Style header
            using (var range = ws.Cells[1, 1, 1, headers.Length])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(91, 155, 213));
                range.Style.Font.Color.SetColor(Color.White);
                range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
            }

            // Data rows
            int row = 2;
            int detailCount = 0;
            foreach (var product in products)
            {
                if (product.Sellers.Count == 0)
                {
                    // Product with no sellers - still show one row
                    ws.Cells[row, 1].Value = product.ProductId;
                    ws.Cells[row, 2].Value = product.Name;
                    ws.Cells[row, 3].Value = product.Brand;
                    ws.Cells[row, 4].Value = product.Category;
                    ws.Cells[row, 5].Value = "-";
                    ws.Cells[row, 6].Value = "-";
                    ws.Cells[row, 7].Value = product.IsSuccess ? "No sellers found" : product.ErrorMessage;
                    ws.Cells[row, 15].Value = product.LowestPrice;
                    ws.Cells[row, 16].Value = product.HighestPrice;
                    ws.Cells[row, 17].Value = 0;
                    ws.Cells[row, 18].Value = product.ImageUrl;
                    ws.Cells[row, 19].Value = product.ProductUrl;
                    row++;
                    detailCount++;
                }
                else
                {
                    foreach (var seller in product.Sellers)
                    {
                        ws.Cells[row, 1].Value = product.ProductId;
                        ws.Cells[row, 2].Value = product.Name;
                        ws.Cells[row, 3].Value = product.Brand;
                        ws.Cells[row, 4].Value = product.Category;
                        ws.Cells[row, 5].Value = seller.Rank;
                        ws.Cells[row, 6].Value = seller.Marketplace;  // NEW: Marketplace
                        ws.Cells[row, 7].Value = seller.SellerName;   // Actual seller name
                        ws.Cells[row, 8].Value = seller.PriceFormatted;
                        ws.Cells[row, 9].Value = seller.Price;
                        ws.Cells[row, 10].Value = seller.ShippingCost;
                        ws.Cells[row, 11].Value = seller.FreeShipping ? "Yes" : "No";
                        ws.Cells[row, 12].Value = seller.InStock ? "Yes" : "No";
                        ws.Cells[row, 13].Value = seller.SellerRating;
                        ws.Cells[row, 14].Value = seller.ProductLink;
                        ws.Cells[row, 15].Value = product.LowestPrice;
                        ws.Cells[row, 16].Value = product.HighestPrice;
                        ws.Cells[row, 17].Value = product.SellerCount;
                        ws.Cells[row, 18].Value = product.ImageUrl;
                        ws.Cells[row, 19].Value = product.ProductUrl;

                        // Bold rank 1
                        if (seller.Rank == 1)
                        {
                            ws.Cells[row, 5].Style.Font.Bold = true;
                            ws.Cells[row, 6].Style.Font.Bold = true;
                            ws.Cells[row, 7].Style.Font.Bold = true;
                            ws.Cells[row, 8].Style.Font.Bold = true;
                        }

                        row++;
                        detailCount++;
                    }
                }
            }

            // Auto-fit
            if (ws.Dimension != null)
            {
                ws.Cells[ws.Dimension.Address].AutoFitColumns();
                for (int i = 1; i <= headers.Length; i++)
                {
                    if (ws.Column(i).Width > 50)
                        ws.Column(i).Width = 50;
                }
            }

            ws.View.FreezePanes(2, 1);
            
            Console.WriteLine($"[Akakce Export] Detailed View: {detailCount} rows created");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Akakce Export] Error in CreateDetailedSheet: {ex.Message}");
            throw;
        }
    }
}
