using OfficeOpenXml;
using OfficeOpenXml.Style;
using Scrapper.Models;
using System.Drawing;

namespace Scrapper.Services;

/// <summary>
/// Exports price comparison data to an Excel pivot-style report.
/// Columns: Product Name | My Price | [Marketplace…] | Best Price | Delta%
/// </summary>
public class AkakcePriceComparisonExcelExporter
{
    private static readonly Color HeaderBlue = Color.FromArgb(31, 78, 121);
    private static readonly Color HeaderGreen = Color.FromArgb(14, 100, 55);
    private static readonly Color RowAlt = Color.FromArgb(242, 242, 242);
    private static readonly Color DeltaPositive = Color.FromArgb(255, 199, 206);   // red tint – my price higher
    private static readonly Color DeltaNegative = Color.FromArgb(198, 239, 206);   // green tint – my price lower
    private static readonly Color StockOutColor = Color.FromArgb(255, 235, 156);   // yellow – stock out
    private static readonly Color BestPriceHighlight = Color.FromArgb(255, 165, 0); // orange – best price in row

    /// <summary>
    /// The fixed list of tracked marketplaces, in display order.
    /// </summary>
    private static readonly string[] TrackedMarketplaces =
    [
        "Amazon Türkiye",
        "ÇiçekSepeti",
        "Hepsiburada",
        "İdefix",
        "Media Markt",
        "Media Markt Pazar Yeri",
        "n11",
        "Pazarama",
        "Pttavm",
        "Teknosa",
        "Trendyol",
        "Turkcell"
    ];

    private static readonly string[] SourceHeaders =
    [
        "Offer Id",
        "Focus Category",
        "Category Label",
        "GTIN",
        "Product Id",
        "Product Brand",
        "Product Name",
        "Total Active Offers",
        "Stock",
        "Winner Assortment Type",
        "Offer Total Price",
        "Offer Score Rank",
        "Seller Name",
        "Product - Sold items (30d)",
        "Product - GMV incl. Shipping (30d)",
        "Sessions by Product with PDP (30d)",
        "Sessions by Product with Add to Cart in pdp (30d)"
    ];

    public void Export(List<PriceComparisonRow> rows, string filePath)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new InvalidOperationException("No rows to export.");

        using var package = new ExcelPackage();

        CreateSummarySheet(package, rows);
        CreateFocusedComparisonSheet(package, rows);
        CreateComparisonSheet(package, rows);
        CreateRawDataSheet(package, rows);

        package.SaveAs(new FileInfo(filePath));
    }

    // ??????????????????????????????????????????????????????????????????????????
    // Sheet 1 – Summary dashboard
    // ??????????????????????????????????????????????????????????????????????????

    private static void CreateSummarySheet(ExcelPackage package, List<PriceComparisonRow> rows)
    {
        var ws = package.Workbook.Worksheets.Add("Summary");

        // Only consider rows with at least one marketplace result and not stock-out
        var comparableRows = rows.Where(r => r.IsSuccess && r.MarketplaceBestPrices.Count > 0).ToList();

        // ?? LEFT SECTION: Best-price count per marketplace ?????????????????????
        // For each product, find which tracked marketplace(s) have the overall best price
        var bestPriceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mp in TrackedMarketplaces)
            bestPriceCounts[mp] = 0;

        foreach (var row in comparableRows)
        {
            if (row.BestPrice <= 0) continue;

            // Find which tracked marketplace(s) match the best price
            foreach (var mp in TrackedMarketplaces)
            {
                if (row.MarketplaceBestPrices.TryGetValue(mp, out var mpPrice) && mpPrice == row.BestPrice)
                    bestPriceCounts[mp]++;
            }
        }

        // Row 3, Col C header
        ws.Cells[3, 3].Value = "En uygun Fiyatlı Ürün sayısı";
        ws.Cells[3, 3].Style.Font.Bold = true;

        // Marketplace list starting at row 4
        int startRow = 4;
        for (int i = 0; i < TrackedMarketplaces.Length; i++)
        {
            int r = startRow + i;
            var mpCell = ws.Cells[r, 2]; // column B – marketplace name
            mpCell.Value = TrackedMarketplaces[i];
            mpCell.Style.Font.Bold = true;
            mpCell.Style.Font.Color.SetColor(Color.White);
            mpCell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            mpCell.Style.Fill.BackgroundColor.SetColor(HeaderBlue);
            mpCell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            var countCell = ws.Cells[r, 3]; // column C – count
            var count = bestPriceCounts[TrackedMarketplaces[i]];
            if (count > 0)
                countCell.Value = count;
        }

        // Total row
        int totalRow = startRow + TrackedMarketplaces.Length + 1;
        ws.Cells[totalRow, 3].Value = comparableRows.Count;
        ws.Cells[totalRow, 3].Style.Font.Bold = true;

        // ?? RIGHT SECTION: Delta % distribution buckets ?????????????????????????
        // Header in row 3
        ws.Cells[3, 5].Value = "En uygun ürüne göre pahalı olduğumuz sayı";
        ws.Cells[3, 5].Style.Font.Bold = true;

        // Compute buckets from comparableRows that also have MyPrice
        var withDelta = comparableRows.Where(r => r.DeltaPercent.HasValue).ToList();

        int enUygunCount = withDelta.Count(r => r.DeltaPercent!.Value <= 0); // my price <= best
        int pct0to10 = withDelta.Count(r => r.DeltaPercent!.Value > 0 && r.DeltaPercent.Value <= 10);
        int pct10to20 = withDelta.Count(r => r.DeltaPercent!.Value > 10 && r.DeltaPercent.Value <= 20);
        int pct20to50 = withDelta.Count(r => r.DeltaPercent!.Value > 20 && r.DeltaPercent.Value <= 50);
        int pct50plus = withDelta.Count(r => r.DeltaPercent!.Value > 50);

        var buckets = new (string Label, int Count)[]
        {
            ("%0-%10", pct0to10),
            ("%10-%20", pct10to20),
            ("%20-%50", pct20to50),
            ("%50+", pct50plus),
            ("En uygun", enUygunCount)
        };

        int bucketStartRow = 5;
        for (int i = 0; i < buckets.Length; i++)
        {
            int r = bucketStartRow + i;
            ws.Cells[r, 5].Value = buckets[i].Label;
            ws.Cells[r, 5].Style.Font.Bold = true;
            ws.Cells[r, 6].Value = buckets[i].Count;
        }

        // Total row for buckets
        int bucketTotalRow = bucketStartRow + buckets.Length;
        ws.Cells[bucketTotalRow, 5].Value = "Toplam";
        ws.Cells[bucketTotalRow, 5].Style.Font.Bold = true;
        ws.Cells[bucketTotalRow, 6].Value = withDelta.Count;
        ws.Cells[bucketTotalRow, 6].Style.Font.Bold = true;

        // ?? Column widths ?????????????????????????????????????????????????????
        ws.Column(2).Width = 24;
        ws.Column(3).Width = 28;
        ws.Column(5).Width = 12;
        ws.Column(6).Width = 10;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Sheet 2 – Focused comparison (tracked marketplaces only, best price highlighted)
    // ──────────────────────────────────────────────────────────────────────────

    private static void CreateFocusedComparisonSheet(ExcelPackage package, List<PriceComparisonRow> rows)
    {
        var ws = package.Workbook.Worksheets.Add("Price Comparison Focused");

        // ── Header row ────────────────────────────────────────────────────────
        int col = 1;
        WriteSourceHeaders(ws, 1, col);
        col += SourceHeaders.Length;
        int searchNameCol = col;
        ws.Cells[1, col++].Value = "Ürün Adı";
        int myPriceCol = col;
        ws.Cells[1, col++].Value = "Fiyatım";
        int akakceNameCol = col;
        ws.Cells[1, col++].Value = "Akakçe Ürün Adı";

        int mpStartCol = col;
        foreach (var mp in TrackedMarketplaces)
            ws.Cells[1, col++].Value = mp;

        int bestPriceCol = col++;
        int deltaCol = col;

        ws.Cells[1, bestPriceCol].Value = "En İyi Fiyat";
        ws.Cells[1, deltaCol].Value = "Delta %";

        int totalCols = deltaCol;

        StyleHeader(ws.Cells[1, 1, 1, totalCols], HeaderBlue);
        StyleHeader(ws.Cells[1, bestPriceCol, 1, deltaCol], HeaderGreen);

        // ── Data rows ─────────────────────────────────────────────────────────
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int r = i + 2;
            bool isAlt = i % 2 == 1;

            col = 1;
            WriteSourceValues(ws, r, col, row);
            col += SourceHeaders.Length;
            ws.Cells[r, col++].Value = row.SearchName;
            SetPriceCell(ws.Cells[r, col++], row.MyPrice, row.IsStockOut);
            ws.Cells[r, col++].Value = string.IsNullOrEmpty(row.AkakceName) ? row.SearchName : row.AkakceName;

            // Track which column holds the lowest tracked-marketplace price
            decimal rowBestPrice = decimal.MaxValue;
            int rowBestCol = -1;

            for (int m = 0; m < TrackedMarketplaces.Length; m++)
            {
                int c2 = mpStartCol + m;
                var cell = ws.Cells[r, c2];
                if (row.MarketplaceBestPrices.TryGetValue(TrackedMarketplaces[m], out var mpPrice))
                {
                    SetNumericPriceCell(cell, mpPrice);
                    if (mpPrice < rowBestPrice)
                    {
                        rowBestPrice = mpPrice;
                        rowBestCol = c2;
                    }
                }
                else
                {
                    cell.Value = "-";
                }
            }

            // Highlight the best-price cell in this row with orange
            if (rowBestCol > 0)
            {
                var best = ws.Cells[r, rowBestCol];
                best.Style.Fill.PatternType = ExcelFillStyle.Solid;
                best.Style.Fill.BackgroundColor.SetColor(BestPriceHighlight);
                best.Style.Font.Bold = true;
            }

            // Best price column
            if (row.BestPrice > 0)
                SetNumericPriceCell(ws.Cells[r, bestPriceCol], row.BestPrice);
            else
                ws.Cells[r, bestPriceCol].Value = "-";

            // Delta %
            if (row.DeltaPercent.HasValue)
            {
                var deltaCell = ws.Cells[r, deltaCol];
                deltaCell.Value = row.DeltaPercent.Value / 100;
                deltaCell.Style.Numberformat.Format = "+0.00%;-0.00%;0.00%";
                deltaCell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                deltaCell.Style.Fill.BackgroundColor.SetColor(row.DeltaPercent.Value > 0 ? DeltaPositive : DeltaNegative);
            }
            else if (row.IsStockOut)
            {
                var cell = ws.Cells[r, deltaCol];
                cell.Value = "Stock Out";
                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(StockOutColor);
            }
            else
            {
                ws.Cells[r, deltaCol].Value = "-";
            }

            // Alternate row shading (skip cells that already have a colour)
            if (isAlt)
            {
                for (int c2 = 1; c2 <= totalCols; c2++)
                {
                    var cell = ws.Cells[r, c2];
                    if (cell.Style.Fill.PatternType == ExcelFillStyle.None)
                    {
                        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                        cell.Style.Fill.BackgroundColor.SetColor(RowAlt);
                    }
                }
            }

            // Hyperlink on Akakçe name
            if (!string.IsNullOrEmpty(row.AkakceUrl))
            {
                ws.Cells[r, akakceNameCol].Hyperlink = new Uri(row.AkakceUrl);
                ws.Cells[r, akakceNameCol].Style.Font.UnderLine = true;
                ws.Cells[r, akakceNameCol].Style.Font.Color.SetColor(HeaderBlue);
            }

            if (!row.IsSuccess)
            {
                ws.Cells[r, searchNameCol].Style.Font.Color.SetColor(Color.FromArgb(156, 31, 31));
                ws.Cells[r, searchNameCol].Value = $"❌ {row.SearchName}";
            }
        }

        // ── Auto-fit ──────────────────────────────────────────────────────────
        if (ws.Dimension != null)
        {
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            for (int c2 = 1; c2 <= totalCols; c2++)
            {
                if (ws.Column(c2).Width > 45) ws.Column(c2).Width = 45;
                if (ws.Column(c2).Width < 10) ws.Column(c2).Width = 10;
            }
        }

        ws.View.FreezePanes(2, 1);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Sheet 3 – Full comparison pivot (all scraped marketplaces)
    // ──────────────────────────────────────────────────────────────────────────

    private static void CreateComparisonSheet(ExcelPackage package, List<PriceComparisonRow> rows)
    {
        var ws = package.Workbook.Worksheets.Add("Price Comparison");

        // Collect all marketplace names (sorted alphabetically)
        var marketplaces = rows
            .SelectMany(r => r.MarketplaceBestPrices.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m)
            .ToList();

        // ?? Build header row ??????????????????????????????????????????????????
        int col = 1;
        WriteSourceHeaders(ws, 1, col);
        col += SourceHeaders.Length;
        int searchNameCol = col;
        ws.Cells[1, col++].Value = "Ürün Adı";
        ws.Cells[1, col++].Value = "Fiyatım";
        int akakceNameCol = col;
        ws.Cells[1, col++].Value = "Akakçe Ürün Adı";

        int mpStartCol = col;
        foreach (var mp in marketplaces)
            ws.Cells[1, col++].Value = mp;

        int bestPriceCol = col++;
        int deltaCol = col;

        ws.Cells[1, bestPriceCol].Value = "En İyi Fiyat";
        ws.Cells[1, deltaCol].Value = "Delta %";

        int totalCols = deltaCol;

        // ?? Style header ??????????????????????????????????????????????????????
        StyleHeader(ws.Cells[1, 1, 1, totalCols], HeaderBlue);

        // Style "Best Price" + "Delta%" headers differently
        StyleHeader(ws.Cells[1, bestPriceCol, 1, deltaCol], HeaderGreen);

        // ?? Data rows ?????????????????????????????????????????????????????????
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int r = i + 2;
            bool isAlt = i % 2 == 1;

            col = 1;
            WriteSourceValues(ws, r, col, row);
            col += SourceHeaders.Length;
            ws.Cells[r, col++].Value = row.SearchName;
            SetPriceCell(ws.Cells[r, col++], row.MyPrice, row.IsStockOut);
            ws.Cells[r, col++].Value = string.IsNullOrEmpty(row.AkakceName) ? row.SearchName : row.AkakceName;

            foreach (var mp in marketplaces)
            {
                var cell = ws.Cells[r, col++];
                if (row.MarketplaceBestPrices.TryGetValue(mp, out var mpPrice))
                    SetNumericPriceCell(cell, mpPrice);
                else
                    cell.Value = "-";
            }

            // Best price
            if (row.BestPrice > 0)
                SetNumericPriceCell(ws.Cells[r, bestPriceCol], row.BestPrice);
            else
                ws.Cells[r, bestPriceCol].Value = "-";

            // Delta %
            if (row.DeltaPercent.HasValue)
            {
                var deltaCell = ws.Cells[r, deltaCol];
                deltaCell.Value = row.DeltaPercent.Value / 100;
                deltaCell.Style.Numberformat.Format = "+0.00%;-0.00%;0.00%";
                var fill = deltaCell.Style.Fill;
                fill.PatternType = ExcelFillStyle.Solid;
                fill.BackgroundColor.SetColor(row.DeltaPercent.Value > 0 ? DeltaPositive : DeltaNegative);
            }
            else if (row.IsStockOut)
            {
                var cell = ws.Cells[r, deltaCol];
                cell.Value = "Stock Out";
                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(StockOutColor);
            }
            else
            {
                ws.Cells[r, deltaCol].Value = "-";
            }

            // Alternate row shading
            if (isAlt)
            {
                using var rowRange = ws.Cells[r, 1, r, totalCols];
                // Only shade cells that don't already have a special fill
                for (int c2 = 1; c2 <= totalCols; c2++)
                {
                    var cell = ws.Cells[r, c2];
                    if (cell.Style.Fill.PatternType == ExcelFillStyle.None)
                    {
                        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                        cell.Style.Fill.BackgroundColor.SetColor(RowAlt);
                    }
                }
            }

            // Hyperlink on Akakçe name cell if we have a URL
            if (!string.IsNullOrEmpty(row.AkakceUrl))
            {
                ws.Cells[r, akakceNameCol].Hyperlink = new Uri(row.AkakceUrl);
                ws.Cells[r, akakceNameCol].Style.Font.UnderLine = true;
                ws.Cells[r, akakceNameCol].Style.Font.Color.SetColor(Color.FromArgb(31, 78, 121));
            }

            // Error row highlight
            if (!row.IsSuccess)
            {
                ws.Cells[r, searchNameCol].Style.Font.Color.SetColor(Color.FromArgb(156, 31, 31));
                ws.Cells[r, searchNameCol].Value = $"? {row.SearchName}";
            }
        }

        // ?? Auto-fit columns ??????????????????????????????????????????????????
        if (ws.Dimension != null)
        {
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            for (int c2 = 1; c2 <= totalCols; c2++)
            {
                if (ws.Column(c2).Width > 45) ws.Column(c2).Width = 45;
                if (ws.Column(c2).Width < 10) ws.Column(c2).Width = 10;
            }
        }

        ws.View.FreezePanes(2, 1);
    }

    // ??????????????????????????????????????????????????????????????????????????
    // Sheet 3 – Raw data (all sellers flat)
    // ??????????????????????????????????????????????????????????????????????????

    private static void CreateRawDataSheet(ExcelPackage package, List<PriceComparisonRow> rows)
    {
        var ws = package.Workbook.Worksheets.Add("Raw Data");

        var headers = SourceHeaders
            .Concat(["Ürün Adı", "Fiyatım", "Stock Out", "Akakçe Ürün Adı", "Marketplace", "En İyi Fiyat (Marketplace)", "En İyi Fiyat (Genel)", "Delta %", "Akakçe URL", "Durum"])
            .ToArray();
        for (int c2 = 0; c2 < headers.Length; c2++)
            ws.Cells[1, c2 + 1].Value = headers[c2];

        StyleHeader(ws.Cells[1, 1, 1, headers.Length], HeaderBlue);

        int row = 2;
        foreach (var r in rows)
        {
            if (r.MarketplaceBestPrices.Count == 0)
            {
                // Product with no marketplace data – one row
                WriteRawRow(ws, row++, r, null, null);
            }
            else
            {
                foreach (var (mp, price) in r.MarketplaceBestPrices.OrderBy(kv => kv.Value))
                    WriteRawRow(ws, row++, r, mp, price);
            }
        }

        if (ws.Dimension != null)
        {
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            for (int c2 = 1; c2 <= headers.Length; c2++)
            {
                if (ws.Column(c2).Width > 50) ws.Column(c2).Width = 50;
            }
        }

        ws.View.FreezePanes(2, 1);
    }

    private static void WriteRawRow(ExcelWorksheet ws, int row, PriceComparisonRow r, string? mp, decimal? mpPrice)
    {
        int col = 1;
        WriteSourceValues(ws, row, col, r);
        col += SourceHeaders.Length;

        ws.Cells[row, col++].Value = r.SearchName;
        SetPriceCell(ws.Cells[row, col++], r.MyPrice, r.IsStockOut);
        ws.Cells[row, col++].Value = r.IsStockOut ? "Yes" : "No";
        ws.Cells[row, col++].Value = string.IsNullOrEmpty(r.AkakceName) ? r.SearchName : r.AkakceName;
        ws.Cells[row, col++].Value = mp ?? "-";

        if (mpPrice.HasValue)
            SetNumericPriceCell(ws.Cells[row, col], mpPrice.Value);
        else
            ws.Cells[row, col].Value = "-";
        col++;

        if (r.BestPrice > 0)
            SetNumericPriceCell(ws.Cells[row, col], r.BestPrice);
        else
            ws.Cells[row, col].Value = "-";
        col++;

        if (r.DeltaPercent.HasValue)
        {
            ws.Cells[row, col].Value = r.DeltaPercent.Value / 100;
            ws.Cells[row, col].Style.Numberformat.Format = "+0.00%;-0.00%;0.00%";
        }
        else
        {
            ws.Cells[row, col].Value = r.IsStockOut ? "Stock Out" : "-";
        }
        col++;

        ws.Cells[row, col++].Value = r.AkakceUrl;
        ws.Cells[row, col].Value = r.IsSuccess ? "OK" : $"Error: {r.ErrorMessage}";
    }

    // ??????????????????????????????????????????????????????????????????????????
    // Helpers
    // ??????????????????????????????????????????????????????????????????????????

    private static void StyleHeader(ExcelRange range, Color bgColor)
    {
        range.Style.Font.Bold = true;
        range.Style.Font.Color.SetColor(Color.White);
        range.Style.Fill.PatternType = ExcelFillStyle.Solid;
        range.Style.Fill.BackgroundColor.SetColor(bgColor);
        range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
        range.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
    }

    private static void WriteSourceHeaders(ExcelWorksheet worksheet, int row, int startColumn)
    {
        for (int i = 0; i < SourceHeaders.Length; i++)
        {
            worksheet.Cells[row, startColumn + i].Value = SourceHeaders[i];
        }
    }

    private static void WriteSourceValues(ExcelWorksheet worksheet, int row, int startColumn, PriceComparisonRow sourceRow)
    {
        worksheet.Cells[row, startColumn + 0].Value = sourceRow.OfferId;
        worksheet.Cells[row, startColumn + 1].Value = sourceRow.FocusCategory;
        worksheet.Cells[row, startColumn + 2].Value = sourceRow.CategoryLabel;
        worksheet.Cells[row, startColumn + 3].Value = sourceRow.Gtin;
        worksheet.Cells[row, startColumn + 4].Value = sourceRow.SourceProductId;
        worksheet.Cells[row, startColumn + 5].Value = sourceRow.SourceProductBrand;
        worksheet.Cells[row, startColumn + 6].Value = sourceRow.SearchName;
        worksheet.Cells[row, startColumn + 7].Value = sourceRow.TotalActiveOffers;
        worksheet.Cells[row, startColumn + 8].Value = sourceRow.SourceStock;
        worksheet.Cells[row, startColumn + 9].Value = sourceRow.WinnerAssortmentType;
        SetPriceCell(worksheet.Cells[row, startColumn + 10], sourceRow.MyPrice, sourceRow.IsStockOut);
        worksheet.Cells[row, startColumn + 11].Value = sourceRow.OfferScoreRank;
        worksheet.Cells[row, startColumn + 12].Value = sourceRow.SourceSellerName;
        worksheet.Cells[row, startColumn + 13].Value = sourceRow.ProductSoldItems30d;
        worksheet.Cells[row, startColumn + 14].Value = sourceRow.ProductGmvInclShipping30d;
        worksheet.Cells[row, startColumn + 15].Value = sourceRow.SessionsByProductWithPdp30d;
        worksheet.Cells[row, startColumn + 16].Value = sourceRow.SessionsByProductWithAddToCartInPdp30d;
    }

    private static void SetPriceCell(ExcelRange cell, decimal price, bool isStockOut)
    {
        if (isStockOut)
        {
            cell.Value = "Stock Out";
            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            cell.Style.Fill.BackgroundColor.SetColor(StockOutColor);
        }
        else
        {
            SetNumericPriceCell(cell, price);
        }
    }

    private static void SetNumericPriceCell(ExcelRange cell, decimal price)
    {
        cell.Value = price;
        cell.Style.Numberformat.Format = "#,##0.00";
    }
}
