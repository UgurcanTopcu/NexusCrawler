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


            
            // Check if any products have variants
            int productsWithVariants = products.Count(p => p.HasVariants);
            if (productsWithVariants > 0)
            {

            }

            if (products.Count == 0)
            {
                throw new Exception("No products data to export");
            }

            // Debug: Show first product details
            if (products.Count > 0)
            {
                var first = products[0];



            }

            // EPPlus license already set in Program.cs
            using var package = new ExcelPackage();

            // Sheet 1: Product Summary
            CreateProductSummarySheet(package, products);

            // Sheet 2: Seller performance summary
            CreateSellerSummarySheet(package, products);

            // Sheet 3: Seller × Category cross-tab matrix
            CreateSellerBrandPivotSheet(package, products);

            // Sheet 4: Brand × Category cross-tab matrix
            CreateBrandSummarySheet(package, products);

            // Sheet 5: Category ? Brand ? Seller drill-down
            CreateCategoryDrillDownSheet(package, products);

            // Sheet 6: Variants (if any products have variants)
            if (productsWithVariants > 0)
            {
                CreateVariantsSheet(package, products);
            }

            // Sheet 7: All Sellers (flat list)
            CreateSellersSheet(package, products);

            // Sheet 8: Detailed view (one row per seller with product info)
            CreateDetailedSheet(package, products);

            // Save
            var file = new FileInfo(filePath);
            package.SaveAs(file);

        }
        catch (Exception ex)
        {

            if (ex.InnerException != null)
            {
            }
            throw;
        }
    }

    private void CreateSellerSummarySheet(ExcelPackage package, List<AkakceProductInfo> products)
    {
        var ws = package.Workbook.Worksheets.Add("Seller Summary");
        var sellerRows = GetSellerRows(products);
        var successfulProducts = products
            .Where(product => product.IsSuccess)
            .Select(product => product.ProductId)
            .Where(productId => !string.IsNullOrWhiteSpace(productId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int totalSuccessfulProducts = successfulProducts.Count;
        int totalSellerRows = sellerRows.Count;
        int totalUniqueSellers = sellerRows
            .Select(row => GetSellerIdentityKey(row.Seller))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int totalMarketplaces = sellerRows
            .Select(row => row.Seller.Marketplace)
            .Where(marketplace => !string.IsNullOrWhiteSpace(marketplace))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // Title
        ws.Cells[1, 1].Value = "Akakce Seller Performance Summary";
        ws.Cells[1, 1, 1, 8].Merge = true;
        using (var titleRange = ws.Cells[1, 1, 1, 8])
        {
            titleRange.Style.Font.Bold = true;
            titleRange.Style.Font.Size = 16;
            titleRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
            titleRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(68, 114, 196));
            titleRange.Style.Font.Color.SetColor(Color.White);
        }

        ws.Cells[2, 1].Value = "Use this sheet to see which sellers appear most often, how often they were rank 1, and how consistently they reached the top 5 or top 10 across the scraped category products.";
        ws.Cells[2, 1, 2, 10].Merge = true;
        ws.Cells[2, 1].Style.Font.Italic = true;

        // Overview
        var overviewList = new List<(string Label, object Value)>
        {
            ("Successful Products", totalSuccessfulProducts),
            ("Seller Rows", totalSellerRows),
            ("Unique Sellers", totalUniqueSellers),
            ("Marketplaces", totalMarketplaces),
            ("Products With Rank 1 Seller", sellerRows.Where(row => row.Seller.Rank == 1).Select(row => row.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count())
        };

        var distinctCategories = products
            .Where(p => p.IsSuccess && !string.IsNullOrWhiteSpace(p.CategoryName))
            .Select(p => p.CategoryName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctCategories.Count > 1)
        {
            overviewList.Insert(0, ("Categories", distinctCategories.Count));
        }

        var overviewData = overviewList.ToArray();

        const int overviewStartRow = 4;
        ws.Cells[overviewStartRow, 1].Value = "Metric";
        ws.Cells[overviewStartRow, 2].Value = "Value";
        using (var headerRange = ws.Cells[overviewStartRow, 1, overviewStartRow, 2])
        {
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
            headerRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(91, 155, 213));
            headerRange.Style.Font.Color.SetColor(Color.White);
        }

        for (int i = 0; i < overviewData.Length; i++)
        {
            ws.Cells[overviewStartRow + i + 1, 1].Value = overviewData[i].Label;
            ws.Cells[overviewStartRow + i + 1, 2].Value = overviewData[i].Value;
        }

        // --- Marketplace Summary ---
        var marketplaceSummaries = sellerRows
            .GroupBy(row => NormalizeSummaryKey(row.Seller.Marketplace))
            .Select(group =>
            {
                var entries = group.ToList();
                var first = entries[0];
                return new MarketplaceSummary(
                    string.IsNullOrWhiteSpace(first.Seller.Marketplace) ? "Unknown" : first.Seller.Marketplace,
                    entries.Select(row => GetSellerIdentityKey(row.Seller)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    entries.Select(row => row.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    entries.Count,
                    entries.Where(row => row.Seller.Rank == 1).Select(row => row.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    entries.Where(row => row.Seller.Rank > 0 && row.Seller.Rank <= 5).Select(row => row.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    GetAverageRank(entries.Select(row => row.Seller.Rank)),
                    GetAveragePrice(entries.Select(row => row.Seller.Price)));
            })
            .OrderByDescending(summary => summary.BestPriceProducts)
            .ThenByDescending(summary => summary.Top5Products)
            .ThenByDescending(summary => summary.ProductsCovered)
            .ThenBy(summary => summary.AverageRank == 0 ? double.MaxValue : summary.AverageRank)
            .ToList();

        int marketplaceTitleRow = overviewStartRow + overviewData.Length + 3;
        ws.Cells[marketplaceTitleRow, 1].Value = "Marketplace Summary";
        ws.Cells[marketplaceTitleRow, 1].Style.Font.Bold = true;
        ws.Cells[marketplaceTitleRow, 1].Style.Font.Size = 13;

        var marketplaceHeaders = new[]
        {
            "Marketplace", "Unique Sellers", "Products Covered", "Seller Rows",
            "Best Price Products", "Top 5 Products", "Avg Rank", "Avg Price (TL)"
        };
        int marketplaceHeaderRow = marketplaceTitleRow + 1;
        for (int i = 0; i < marketplaceHeaders.Length; i++)
        {
            ws.Cells[marketplaceHeaderRow, i + 1].Value = marketplaceHeaders[i];
        }

        using (var headerRange = ws.Cells[marketplaceHeaderRow, 1, marketplaceHeaderRow, marketplaceHeaders.Length])
        {
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
            headerRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(255, 192, 0));
            headerRange.Style.Font.Color.SetColor(Color.Black);
        }

        int marketplaceRow = marketplaceHeaderRow + 1;
        foreach (var summary in marketplaceSummaries)
        {
            ws.Cells[marketplaceRow, 1].Value = summary.Marketplace;
            ws.Cells[marketplaceRow, 2].Value = summary.UniqueSellers;
            ws.Cells[marketplaceRow, 3].Value = summary.ProductsCovered;
            ws.Cells[marketplaceRow, 4].Value = summary.SellerRows;
            ws.Cells[marketplaceRow, 5].Value = summary.BestPriceProducts;
            ws.Cells[marketplaceRow, 6].Value = summary.Top5Products;
            ws.Cells[marketplaceRow, 7].Value = summary.AverageRank == 0 ? null : summary.AverageRank;
            ws.Cells[marketplaceRow, 8].Value = summary.AveragePrice == 0 ? null : summary.AveragePrice;
            marketplaceRow++;
        }

        if (marketplaceSummaries.Count > 0)
        {
            ws.Cells[marketplaceHeaderRow, 1, marketplaceRow - 1, marketplaceHeaders.Length].AutoFilter = true;
            ws.Cells[marketplaceHeaderRow + 1, 7, marketplaceRow - 1, 7].Style.Numberformat.Format = "0.00";
            ws.Cells[marketplaceHeaderRow + 1, 8, marketplaceRow - 1, 8].Style.Numberformat.Format = "#,##0.00";
        }

        // --- Seller Performance ---
        var sellerSummaries = sellerRows
            .GroupBy(row => GetSellerIdentityKey(row.Seller))
            .Select(group =>
            {
                var entries = group.ToList();
                var first = entries[0];
                int productsCovered = entries.Select(row => row.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                int bestPriceProducts = entries.Where(row => row.Seller.Rank == 1).Select(row => row.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var validRanks = entries.Select(row => row.Seller.Rank).Where(rank => rank > 0).ToList();
                var validPrices = entries.Select(row => row.Seller.Price).Where(price => price > 0).ToList();
                var brands = entries.Select(row => row.Brand).Where(b => !string.IsNullOrWhiteSpace(b)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b).ToList();
                var categories = entries.Select(row => row.CategoryName).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();

                return new SellerSummary(
                    string.IsNullOrWhiteSpace(first.Seller.Marketplace) ? "Unknown" : first.Seller.Marketplace,
                    string.IsNullOrWhiteSpace(first.Seller.SellerName) ? "Unknown Seller" : first.Seller.SellerName,
                    productsCovered,
                    bestPriceProducts,
                    validRanks.Count == 0 ? 0 : validRanks.Average(),
                    validRanks.Count == 0 ? 0 : validRanks.Min(),
                    validRanks.Count == 0 ? 0 : validRanks.Max(),
                    validPrices.Count == 0 ? 0 : validPrices.Average(),
                    validPrices.Count == 0 ? 0 : validPrices.Min(),
                    validPrices.Count == 0 ? 0 : validPrices.Max(),
                    brands.Count,
                    string.Join(", ", brands),
                    string.Join(", ", categories));
            })
            .OrderByDescending(summary => summary.BestPriceCount)
            .ThenByDescending(summary => summary.ProductsCovered)
            .ThenBy(summary => summary.AverageRank == 0 ? double.MaxValue : summary.AverageRank)
            .ToList();

        int sellerTitleRow = marketplaceRow + 2;
        ws.Cells[sellerTitleRow, 1].Value = "Seller Performance";
        ws.Cells[sellerTitleRow, 1].Style.Font.Bold = true;
        ws.Cells[sellerTitleRow, 1].Style.Font.Size = 13;

        var sellerHeaders = new[]
        {
            "Marketplace", "Seller Name", "Products Covered", "Best Price Count",
            "Avg Rank", "Best Rank", "Worst Rank",
            "Avg Price (TL)", "Min Price (TL)", "Max Price (TL)",
            "Unique Brands", "Brands", "Categories"
        };
        int sellerHeaderRow = sellerTitleRow + 1;
        for (int i = 0; i < sellerHeaders.Length; i++)
        {
            ws.Cells[sellerHeaderRow, i + 1].Value = sellerHeaders[i];
        }

        using (var headerRange = ws.Cells[sellerHeaderRow, 1, sellerHeaderRow, sellerHeaders.Length])
        {
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
            headerRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(112, 173, 71));
            headerRange.Style.Font.Color.SetColor(Color.White);
        }

        int sellerRow = sellerHeaderRow + 1;
        foreach (var summary in sellerSummaries)
        {
            ws.Cells[sellerRow, 1].Value = summary.Marketplace;
            ws.Cells[sellerRow, 2].Value = summary.SellerName;
            ws.Cells[sellerRow, 3].Value = summary.ProductsCovered;
            ws.Cells[sellerRow, 4].Value = summary.BestPriceCount;
            ws.Cells[sellerRow, 5].Value = summary.AverageRank == 0 ? null : summary.AverageRank;
            ws.Cells[sellerRow, 6].Value = summary.BestRank == 0 ? null : summary.BestRank;
            ws.Cells[sellerRow, 7].Value = summary.WorstRank == 0 ? null : summary.WorstRank;
            ws.Cells[sellerRow, 8].Value = summary.AveragePrice == 0 ? null : summary.AveragePrice;
            ws.Cells[sellerRow, 9].Value = summary.MinPrice == 0 ? null : summary.MinPrice;
            ws.Cells[sellerRow, 10].Value = summary.MaxPrice == 0 ? null : summary.MaxPrice;
            ws.Cells[sellerRow, 11].Value = summary.UniqueBrands;
            ws.Cells[sellerRow, 12].Value = summary.Brands;
            ws.Cells[sellerRow, 13].Value = summary.Categories;

            if (summary.BestPriceCount > 0)
            {
                using var highlightRange = ws.Cells[sellerRow, 1, sellerRow, sellerHeaders.Length];
                highlightRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
                highlightRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(226, 239, 218));
            }

            sellerRow++;
        }

        if (sellerSummaries.Count > 0)
        {
            ws.Cells[sellerHeaderRow, 1, sellerRow - 1, sellerHeaders.Length].AutoFilter = true;
            ws.Cells[sellerHeaderRow + 1, 5, sellerRow - 1, 5].Style.Numberformat.Format = "0.00";
            ws.Cells[sellerHeaderRow + 1, 8, sellerRow - 1, 10].Style.Numberformat.Format = "#,##0.00";
        }

        if (ws.Dimension != null)
        {
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            for (int i = 1; i <= sellerHeaders.Length; i++)
            {
                if (ws.Column(i).Width > 40)
                {
                    ws.Column(i).Width = 40;
                }
            }
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
                "Category Name", "Source Category URL", "Product ID", "Product Name", "Brand", "Category",
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

                ws.Cells[row, 1].Value = p.CategoryName;
                ws.Cells[row, 2].Value = p.SourceCategoryUrl;
                ws.Cells[row, 3].Value = p.ProductId;
                ws.Cells[row, 4].Value = p.Name;
                ws.Cells[row, 5].Value = p.Brand;
                ws.Cells[row, 6].Value = p.Category;
                ws.Cells[row, 7].Value = p.LowestPrice;
                ws.Cells[row, 8].Value = p.HighestPrice;
                ws.Cells[row, 9].Value = p.SellerCount;
                ws.Cells[row, 10].Value = p.ImageUrl;
                ws.Cells[row, 11].Value = p.ProductUrl;
                ws.Cells[row, 12].Value = p.ScrapedAt.ToString("yyyy-MM-dd HH:mm");
                ws.Cells[row, 13].Value = p.IsSuccess ? "Success" : $"Error: {p.ErrorMessage}";

                // Color code status
                if (!p.IsSuccess)
                {
                    ws.Cells[row, 13].Style.Font.Color.SetColor(Color.Red);
                }
                else
                {
                    ws.Cells[row, 13].Style.Font.Color.SetColor(Color.Green);
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
        }
        catch (Exception ex)
        {
            throw;
        }
    }

    private void CreateVariantsSheet(ExcelPackage package, List<AkakceProductInfo> products)
    {
        try
        {
            var ws = package.Workbook.Worksheets.Add("Variants");

            // Headers
            var headers = new[]
            {
                "Category Name", "Brand", "Product ID", "Product Name", "Variant Name", "Variant Options",
                "Variant URL", "Lowest Price", "Highest Price", "Seller Count",
                "Scraped At"
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
                range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(255, 192, 0)); // Orange
                range.Style.Font.Color.SetColor(Color.White);
                range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
            }

            // Data rows
            int row = 2;
            int variantCount = 0;
            foreach (var product in products)
            {
                if (product.HasVariants)
                {
                    foreach (var variant in product.Variants)
                    {
                        ws.Cells[row, 1].Value = product.CategoryName;
                        ws.Cells[row, 2].Value = product.Brand;
                        ws.Cells[row, 3].Value = product.ProductId;
                        ws.Cells[row, 4].Value = product.Name;
                        ws.Cells[row, 5].Value = variant.VariantName;
                        
                        // Format options as "Key1: Value1, Key2: Value2"
                        var optionsStr = string.Join(", ", variant.Options.Select(kv => $"{kv.Key}: {kv.Value}"));
                        ws.Cells[row, 6].Value = optionsStr;
                        
                        ws.Cells[row, 7].Value = variant.VariantUrl;
                        ws.Cells[row, 8].Value = variant.LowestPrice;
                        ws.Cells[row, 9].Value = variant.HighestPrice;
                        ws.Cells[row, 10].Value = variant.SellerCount;
                        ws.Cells[row, 11].Value = variant.ScrapedAt.ToString("yyyy-MM-dd HH:mm");

                        row++;
                        variantCount++;
                    }
                }
            }

            // Auto-fit and set max width
            if (ws.Dimension != null)
            {
                ws.Cells[ws.Dimension.Address].AutoFitColumns();
                for (int i = 1; i <= headers.Length; i++)
                {
                    if (ws.Column(i).Width > 60)
                        ws.Column(i).Width = 60;
                }
            }

            // Freeze header row
            ws.View.FreezePanes(2, 1);
        }
        catch (Exception ex)
        {
            throw;
        }
    }

    private static List<SellerRow> GetSellerRows(List<AkakceProductInfo> products)
    {
        var sellerRows = new List<SellerRow>();

        foreach (var product in products.Where(product => product.IsSuccess && !string.IsNullOrWhiteSpace(product.ProductId)))
        {
            if (product.HasVariants)
            {
                foreach (var variant in product.Variants)
                {
                    foreach (var seller in variant.Sellers)
                    {
                        sellerRows.Add(new SellerRow(
                            product.ProductId,
                            product.Name,
                            variant.VariantName,
                            GetListingKey(product.ProductId, variant.VariantName),
                            product.Brand,
                            product.CategoryName,
                            seller));
                    }
                }

                continue;
            }

            foreach (var seller in product.Sellers)
            {
                sellerRows.Add(new SellerRow(
                    product.ProductId,
                    product.Name,
                    string.Empty,
                    GetListingKey(product.ProductId, null),
                    product.Brand,
                    product.CategoryName,
                    seller));
            }
        }

        return sellerRows;
    }

    private static string GetListingKey(string productId, string? variantName)
    {
        return string.IsNullOrWhiteSpace(variantName)
            ? productId.Trim()
            : $"{productId.Trim()}|{variantName.Trim()}";
    }

    private static string GetSellerIdentityKey(AkakceSellerInfo seller)
    {
        return $"{NormalizeSummaryKey(seller.Marketplace)}|{NormalizeSummaryKey(seller.SellerName)}";
    }

    private static string NormalizeSummaryKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static double GetAverageRank(IEnumerable<int> ranks)
    {
        var validRanks = ranks.Where(rank => rank > 0).ToList();
        return validRanks.Count == 0 ? 0 : validRanks.Average();
    }

    private static decimal GetAveragePrice(IEnumerable<decimal> prices)
    {
        var validPrices = prices.Where(price => price > 0).ToList();
        return validPrices.Count == 0 ? 0 : validPrices.Average();
    }

    private sealed record SellerRow(
        string ProductId,
        string ProductName,
        string VariantName,
        string ListingKey,
        string Brand,
        string CategoryName,
        AkakceSellerInfo Seller);

    private sealed record MarketplaceSummary(
        string Marketplace,
        int UniqueSellers,
        int ProductsCovered,
        int SellerRows,
        int BestPriceProducts,
        int Top5Products,
        double AverageRank,
        decimal AveragePrice);

    private sealed record SellerSummary(
        string Marketplace,
        string SellerName,
        int ProductsCovered,
        int BestPriceCount,
        double AverageRank,
        int BestRank,
        int WorstRank,
        decimal AveragePrice,
        decimal MinPrice,
        decimal MaxPrice,
        int UniqueBrands,
        string Brands,
        string Categories);

    /// <summary>
    /// Seller × Category cross-tab matrix: rows = sellers, columns = categories, values = product counts.
    /// Instantly shows which sellers span which categories.
    /// </summary>
    private void CreateSellerBrandPivotSheet(ExcelPackage package, List<AkakceProductInfo> products)
    {
        var ws = package.Workbook.Worksheets.Add("Seller × Category");
        var sellerRows = GetSellerRows(products);

        var categoryNames = sellerRows
            .Select(r => string.IsNullOrWhiteSpace(r.CategoryName) ? "Unknown" : r.CategoryName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        // Group by seller identity
        var sellerGroups = sellerRows
            .GroupBy(r => GetSellerIdentityKey(r.Seller))
            .Select(group =>
            {
                var entries = group.ToList();
                var first = entries[0];
                var perCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var cat in categoryNames)
                    perCategory[cat] = 0;

                foreach (var catGroup in entries.GroupBy(e => string.IsNullOrWhiteSpace(e.CategoryName) ? "Unknown" : e.CategoryName, StringComparer.OrdinalIgnoreCase))
                {
                    perCategory[catGroup.Key] = catGroup.Select(e => e.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                }

                var brands = entries.Select(e => e.Brand).Where(b => !string.IsNullOrWhiteSpace(b)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b).ToList();
                int totalProducts = entries.Select(e => e.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                int bestPriceCount = entries.Where(e => e.Seller.Rank == 1).Select(e => e.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

                return new
                {
                    Marketplace = string.IsNullOrWhiteSpace(first.Seller.Marketplace) ? "Unknown" : first.Seller.Marketplace,
                    SellerName = string.IsNullOrWhiteSpace(first.Seller.SellerName) ? "Unknown" : first.Seller.SellerName,
                    TotalProducts = totalProducts,
                    BestPriceCount = bestPriceCount,
                    CategoryCount = perCategory.Count(kv => kv.Value > 0),
                    BrandCount = brands.Count,
                    Brands = string.Join(", ", brands),
                    PerCategory = perCategory
                };
            })
            .OrderByDescending(s => s.TotalProducts)
            .ThenByDescending(s => s.CategoryCount)
            .ThenByDescending(s => s.BestPriceCount)
            .ToList();

        // Fixed columns: Marketplace, Seller, Total Products, Best Price, Categories, Brands, Brand List
        int fixedCols = 7;
        int col = 1;
        ws.Cells[1, col++].Value = "Marketplace";
        ws.Cells[1, col++].Value = "Seller Name";
        ws.Cells[1, col++].Value = "Total Products";
        ws.Cells[1, col++].Value = "Best Price Count";
        ws.Cells[1, col++].Value = "Categories";
        ws.Cells[1, col++].Value = "Brands";
        ws.Cells[1, col++].Value = "Brand List";

        // Dynamic columns: one per category
        foreach (var cat in categoryNames)
        {
            ws.Cells[1, col++].Value = cat;
        }

        int totalCols = fixedCols + categoryNames.Count;

        // Style fixed headers
        using (var range = ws.Cells[1, 1, 1, fixedCols])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(68, 114, 196));
            range.Style.Font.Color.SetColor(Color.White);
        }

        // Style category headers
        if (categoryNames.Count > 0)
        {
            using var catRange = ws.Cells[1, fixedCols + 1, 1, totalCols];
            catRange.Style.Font.Bold = true;
            catRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
            catRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(155, 89, 182));
            catRange.Style.Font.Color.SetColor(Color.White);
        }

        int row = 2;
        foreach (var seller in sellerGroups)
        {
            ws.Cells[row, 1].Value = seller.Marketplace;
            ws.Cells[row, 2].Value = seller.SellerName;
            ws.Cells[row, 3].Value = seller.TotalProducts;
            ws.Cells[row, 4].Value = seller.BestPriceCount;
            ws.Cells[row, 5].Value = seller.CategoryCount;
            ws.Cells[row, 6].Value = seller.BrandCount;
            ws.Cells[row, 7].Value = seller.Brands;

            for (int i = 0; i < categoryNames.Count; i++)
            {
                int count = seller.PerCategory[categoryNames[i]];
                if (count > 0)
                    ws.Cells[row, fixedCols + 1 + i].Value = count;
            }

            if (seller.BestPriceCount > 0)
            {
                using var hl = ws.Cells[row, 1, row, fixedCols];
                hl.Style.Fill.PatternType = ExcelFillStyle.Solid;
                hl.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(226, 239, 218));
            }

            row++;
        }

        if (sellerGroups.Count > 0)
        {
            ws.Cells[1, 1, row - 1, totalCols].AutoFilter = true;
        }

        if (ws.Dimension != null)
        {
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            for (int i = 1; i <= totalCols; i++)
            {
                if (ws.Column(i).Width > 30) ws.Column(i).Width = 30;
            }
        }

        ws.View.FreezePanes(2, 3);
    }

    /// <summary>
    /// Brand × Category cross-tab matrix: rows = brands, columns = categories, values = product counts.
    /// Shows brand coverage across all scraped categories at a glance.
    /// </summary>
    private void CreateBrandSummarySheet(ExcelPackage package, List<AkakceProductInfo> products)
    {
        var ws = package.Workbook.Worksheets.Add("Brand × Category");
        var sellerRows = GetSellerRows(products);

        var categoryNames = sellerRows
            .Select(r => string.IsNullOrWhiteSpace(r.CategoryName) ? "Unknown" : r.CategoryName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        var brandGroups = sellerRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Brand))
            .GroupBy(r => NormalizeSummaryKey(r.Brand))
            .Select(group =>
            {
                var entries = group.ToList();
                var first = entries[0];
                var perCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var cat in categoryNames)
                    perCategory[cat] = 0;

                foreach (var catGroup in entries.GroupBy(e => string.IsNullOrWhiteSpace(e.CategoryName) ? "Unknown" : e.CategoryName, StringComparer.OrdinalIgnoreCase))
                {
                    perCategory[catGroup.Key] = catGroup.Select(e => e.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                }

                var validPrices = entries.Select(e => e.Seller.Price).Where(p => p > 0).ToList();

                return new
                {
                    Brand = string.IsNullOrWhiteSpace(first.Brand) ? "Unknown" : first.Brand,
                    TotalProducts = entries.Select(e => e.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    SellerCount = entries.Select(e => GetSellerIdentityKey(e.Seller)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    CategoryCount = perCategory.Count(kv => kv.Value > 0),
                    AvgPrice = validPrices.Count == 0 ? 0m : validPrices.Average(),
                    MinPrice = validPrices.Count == 0 ? 0m : validPrices.Min(),
                    MaxPrice = validPrices.Count == 0 ? 0m : validPrices.Max(),
                    PerCategory = perCategory
                };
            })
            .OrderByDescending(b => b.TotalProducts)
            .ThenByDescending(b => b.SellerCount)
            .ToList();

        // Fixed columns
        int fixedCols = 7;
        int col = 1;
        ws.Cells[1, col++].Value = "Brand";
        ws.Cells[1, col++].Value = "Total Products";
        ws.Cells[1, col++].Value = "Sellers";
        ws.Cells[1, col++].Value = "Categories";
        ws.Cells[1, col++].Value = "Avg Price (TL)";
        ws.Cells[1, col++].Value = "Min Price (TL)";
        ws.Cells[1, col++].Value = "Max Price (TL)";

        foreach (var cat in categoryNames)
        {
            ws.Cells[1, col++].Value = cat;
        }

        int totalCols = fixedCols + categoryNames.Count;

        using (var range = ws.Cells[1, 1, 1, fixedCols])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(41, 128, 185));
            range.Style.Font.Color.SetColor(Color.White);
        }

        if (categoryNames.Count > 0)
        {
            using var catRange = ws.Cells[1, fixedCols + 1, 1, totalCols];
            catRange.Style.Font.Bold = true;
            catRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
            catRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(155, 89, 182));
            catRange.Style.Font.Color.SetColor(Color.White);
        }

        int row = 2;
        foreach (var brand in brandGroups)
        {
            ws.Cells[row, 1].Value = brand.Brand;
            ws.Cells[row, 2].Value = brand.TotalProducts;
            ws.Cells[row, 3].Value = brand.SellerCount;
            ws.Cells[row, 4].Value = brand.CategoryCount;
            ws.Cells[row, 5].Value = brand.AvgPrice == 0 ? null : brand.AvgPrice;
            ws.Cells[row, 6].Value = brand.MinPrice == 0 ? null : brand.MinPrice;
            ws.Cells[row, 7].Value = brand.MaxPrice == 0 ? null : brand.MaxPrice;

            for (int i = 0; i < categoryNames.Count; i++)
            {
                int count = brand.PerCategory[categoryNames[i]];
                if (count > 0)
                    ws.Cells[row, fixedCols + 1 + i].Value = count;
            }

            row++;
        }

        if (brandGroups.Count > 0)
        {
            ws.Cells[1, 1, row - 1, totalCols].AutoFilter = true;
            ws.Cells[2, 5, row - 1, 7].Style.Numberformat.Format = "#,##0.00";
        }

        if (ws.Dimension != null)
        {
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            for (int i = 1; i <= totalCols; i++)
            {
                if (ws.Column(i).Width > 30) ws.Column(i).Width = 30;
            }
        }

        ws.View.FreezePanes(2, 2);
    }

    /// <summary>
    /// Flat drill-down table: Category ? Brand ? Seller with aggregated metrics.
    /// The ultimate filterable detail — filter any column to slice data any way you want.
    /// </summary>
    private void CreateCategoryDrillDownSheet(ExcelPackage package, List<AkakceProductInfo> products)
    {
        var ws = package.Workbook.Worksheets.Add("Category Drill-Down");
        var sellerRows = GetSellerRows(products);

        var drillData = sellerRows
            .GroupBy(r => (
                Category: NormalizeSummaryKey(r.CategoryName),
                Brand: NormalizeSummaryKey(r.Brand),
                Marketplace: NormalizeSummaryKey(r.Seller.Marketplace),
                Seller: NormalizeSummaryKey(r.Seller.SellerName)))
            .Select(group =>
            {
                var entries = group.ToList();
                var first = entries[0];
                var validPrices = entries.Select(e => e.Seller.Price).Where(p => p > 0).ToList();
                var validRanks = entries.Select(e => e.Seller.Rank).Where(r => r > 0).ToList();
                return new
                {
                    CategoryName = string.IsNullOrWhiteSpace(first.CategoryName) ? "Unknown" : first.CategoryName,
                    Brand = string.IsNullOrWhiteSpace(first.Brand) ? "Unknown" : first.Brand,
                    Marketplace = string.IsNullOrWhiteSpace(first.Seller.Marketplace) ? "Unknown" : first.Seller.Marketplace,
                    SellerName = string.IsNullOrWhiteSpace(first.Seller.SellerName) ? "Unknown" : first.Seller.SellerName,
                    ProductCount = entries.Select(e => e.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    BestPriceCount = entries.Where(e => e.Seller.Rank == 1).Select(e => e.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    AvgRank = validRanks.Count == 0 ? 0.0 : validRanks.Average(),
                    BestRank = validRanks.Count == 0 ? 0 : validRanks.Min(),
                    AvgPrice = validPrices.Count == 0 ? 0m : validPrices.Average(),
                    MinPrice = validPrices.Count == 0 ? 0m : validPrices.Min(),
                    MaxPrice = validPrices.Count == 0 ? 0m : validPrices.Max()
                };
            })
            .OrderBy(d => d.CategoryName)
            .ThenBy(d => d.Brand)
            .ThenByDescending(d => d.ProductCount)
            .ToList();

        var headers = new[]
        {
            "Category", "Brand", "Marketplace", "Seller Name",
            "Products", "Best Price", "Avg Rank", "Best Rank",
            "Avg Price (TL)", "Min Price (TL)", "Max Price (TL)"
        };

        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cells[1, i + 1].Value = headers[i];
        }

        using (var range = ws.Cells[1, 1, 1, headers.Length])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(192, 80, 77));
            range.Style.Font.Color.SetColor(Color.White);
        }

        int row = 2;
        foreach (var item in drillData)
        {
            ws.Cells[row, 1].Value = item.CategoryName;
            ws.Cells[row, 2].Value = item.Brand;
            ws.Cells[row, 3].Value = item.Marketplace;
            ws.Cells[row, 4].Value = item.SellerName;
            ws.Cells[row, 5].Value = item.ProductCount;
            ws.Cells[row, 6].Value = item.BestPriceCount;
            ws.Cells[row, 7].Value = item.AvgRank == 0 ? null : item.AvgRank;
            ws.Cells[row, 8].Value = item.BestRank == 0 ? null : item.BestRank;
            ws.Cells[row, 9].Value = item.AvgPrice == 0 ? null : item.AvgPrice;
            ws.Cells[row, 10].Value = item.MinPrice == 0 ? null : item.MinPrice;
            ws.Cells[row, 11].Value = item.MaxPrice == 0 ? null : item.MaxPrice;

            if (item.BestPriceCount > 0)
            {
                using var hl = ws.Cells[row, 5, row, 6];
                hl.Style.Fill.PatternType = ExcelFillStyle.Solid;
                hl.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(226, 239, 218));
            }

            row++;
        }

        if (drillData.Count > 0)
        {
            ws.Cells[1, 1, row - 1, headers.Length].AutoFilter = true;
            ws.Cells[2, 7, row - 1, 7].Style.Numberformat.Format = "0.00";
            ws.Cells[2, 9, row - 1, 11].Style.Numberformat.Format = "#,##0.00";
        }

        if (ws.Dimension != null)
        {
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            for (int i = 1; i <= headers.Length; i++)
            {
                if (ws.Column(i).Width > 35) ws.Column(i).Width = 35;
            }
        }

        ws.View.FreezePanes(2, 1);
    }

    private void CreateSellersSheet(ExcelPackage package, List<AkakceProductInfo> products)
    {
        try
        {
            var ws = package.Workbook.Worksheets.Add("All Sellers");

            // Headers
            var headers = new[]
            {
                "Category Name", "Brand", "Product ID", "Product Name", "Variant Name", "Rank", "Marketplace", "Seller Name", 
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

            // Data rows - flatten all sellers (including variant sellers)
            int row = 2;
            int sellerCount = 0;
            foreach (var product in products)
            {
                // If product has variants, export variant sellers
                if (product.HasVariants)
                {
                    foreach (var variant in product.Variants)
                    {
                        foreach (var seller in variant.Sellers)
                        {
                            ws.Cells[row, 1].Value = product.CategoryName;
                            ws.Cells[row, 2].Value = product.Brand;
                            ws.Cells[row, 3].Value = product.ProductId;
                            ws.Cells[row, 4].Value = product.Name;
                            ws.Cells[row, 5].Value = variant.VariantName;
                            ws.Cells[row, 6].Value = seller.Rank;
                            ws.Cells[row, 7].Value = seller.Marketplace;
                            ws.Cells[row, 8].Value = seller.SellerName;
                            ws.Cells[row, 9].Value = seller.PriceFormatted;
                            ws.Cells[row, 10].Value = seller.Price;
                            ws.Cells[row, 11].Value = seller.OriginalPrice;
                            ws.Cells[row, 12].Value = seller.DiscountPercentage;
                            ws.Cells[row, 13].Value = seller.ShippingCost;
                            ws.Cells[row, 14].Value = seller.FreeShipping ? "Yes" : "No";
                            ws.Cells[row, 15].Value = seller.DeliveryTime;
                            ws.Cells[row, 16].Value = seller.StockStatus;
                            ws.Cells[row, 17].Value = seller.InStock ? "Yes" : "No";
                            ws.Cells[row, 18].Value = seller.SellerRating;
                            ws.Cells[row, 19].Value = seller.ProductLink;
                            ws.Cells[row, 20].Value = string.Join(", ", seller.Badges);
                            ws.Cells[row, 21].Value = seller.Notes;

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
                }
                else
                {
                    // Export regular product sellers
                    foreach (var seller in product.Sellers)
                    {
                        ws.Cells[row, 1].Value = product.CategoryName;
                        ws.Cells[row, 2].Value = product.Brand;
                        ws.Cells[row, 3].Value = product.ProductId;
                        ws.Cells[row, 4].Value = product.Name;
                        ws.Cells[row, 5].Value = "-"; // No variant
                        ws.Cells[row, 6].Value = seller.Rank;
                        ws.Cells[row, 7].Value = seller.Marketplace;
                        ws.Cells[row, 8].Value = seller.SellerName;
                        ws.Cells[row, 9].Value = seller.PriceFormatted;
                        ws.Cells[row, 10].Value = seller.Price;
                        ws.Cells[row, 11].Value = seller.OriginalPrice;
                        ws.Cells[row, 12].Value = seller.DiscountPercentage;
                        ws.Cells[row, 13].Value = seller.ShippingCost;
                        ws.Cells[row, 14].Value = seller.FreeShipping ? "Yes" : "No";
                        ws.Cells[row, 15].Value = seller.DeliveryTime;
                        ws.Cells[row, 16].Value = seller.StockStatus;
                        ws.Cells[row, 17].Value = seller.InStock ? "Yes" : "No";
                        ws.Cells[row, 18].Value = seller.SellerRating;
                        ws.Cells[row, 19].Value = seller.ProductLink;
                        ws.Cells[row, 20].Value = string.Join(", ", seller.Badges);
                        ws.Cells[row, 21].Value = seller.Notes;

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
        }
        catch (Exception ex)
        {
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
                "Category Name", "Source Category URL", "Product ID", "Product Name", "Brand", "Category",
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
                    ws.Cells[row, 1].Value = product.CategoryName;
                    ws.Cells[row, 2].Value = product.SourceCategoryUrl;
                    ws.Cells[row, 3].Value = product.ProductId;
                    ws.Cells[row, 4].Value = product.Name;
                    ws.Cells[row, 5].Value = product.Brand;
                    ws.Cells[row, 6].Value = product.Category;
                    ws.Cells[row, 7].Value = "-";
                    ws.Cells[row, 8].Value = "-";
                    ws.Cells[row, 9].Value = product.IsSuccess ? "No sellers found" : product.ErrorMessage;
                    ws.Cells[row, 17].Value = product.LowestPrice;
                    ws.Cells[row, 18].Value = product.HighestPrice;
                    ws.Cells[row, 19].Value = 0;
                    ws.Cells[row, 20].Value = product.ImageUrl;
                    ws.Cells[row, 21].Value = product.ProductUrl;
                    row++;
                    detailCount++;
                }
                else
                {
                    foreach (var seller in product.Sellers)
                    {
                        ws.Cells[row, 1].Value = product.CategoryName;
                        ws.Cells[row, 2].Value = product.SourceCategoryUrl;
                        ws.Cells[row, 3].Value = product.ProductId;
                        ws.Cells[row, 4].Value = product.Name;
                        ws.Cells[row, 5].Value = product.Brand;
                        ws.Cells[row, 6].Value = product.Category;
                        ws.Cells[row, 7].Value = seller.Rank;
                        ws.Cells[row, 8].Value = seller.Marketplace;
                        ws.Cells[row, 9].Value = seller.SellerName;
                        ws.Cells[row, 10].Value = seller.PriceFormatted;
                        ws.Cells[row, 11].Value = seller.Price;
                        ws.Cells[row, 12].Value = seller.ShippingCost;
                        ws.Cells[row, 13].Value = seller.FreeShipping ? "Yes" : "No";
                        ws.Cells[row, 14].Value = seller.InStock ? "Yes" : "No";
                        ws.Cells[row, 15].Value = seller.SellerRating;
                        ws.Cells[row, 16].Value = seller.ProductLink;
                        ws.Cells[row, 17].Value = product.LowestPrice;
                        ws.Cells[row, 18].Value = product.HighestPrice;
                        ws.Cells[row, 19].Value = product.SellerCount;
                        ws.Cells[row, 20].Value = product.ImageUrl;
                        ws.Cells[row, 21].Value = product.ProductUrl;

                        // Bold rank 1
                        if (seller.Rank == 1)
                        {
                            ws.Cells[row, 7].Style.Font.Bold = true;
                            ws.Cells[row, 8].Style.Font.Bold = true;
                            ws.Cells[row, 9].Style.Font.Bold = true;
                            ws.Cells[row, 10].Style.Font.Bold = true;
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
        }
        catch (Exception ex)
        {
            throw;
        }
    }
}
