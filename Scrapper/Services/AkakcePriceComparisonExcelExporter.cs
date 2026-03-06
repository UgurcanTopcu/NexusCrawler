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
    private static readonly Color DeltaPositive = Color.FromArgb(255, 199, 206); // red tint – my price higher
    private static readonly Color DeltaNegative = Color.FromArgb(198, 239, 206); // green tint – my price lower
    private static readonly Color StockOutColor = Color.FromArgb(255, 235, 156); // yellow – stock out

    public void Export(List<PriceComparisonRow> rows, string filePath)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new InvalidOperationException("No rows to export.");

        using var package = new ExcelPackage();

        CreateComparisonSheet(package, rows);
        CreateRawDataSheet(package, rows);

        package.SaveAs(new FileInfo(filePath));
    }

    // ??????????????????????????????????????????????????????????????????????????
    // Sheet 1 – Comparison pivot
    // ??????????????????????????????????????????????????????????????????????????

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
        ws.Cells[1, col++].Value = "Ürün Adý";           // A
        ws.Cells[1, col++].Value = "Fiyatým";             // B – my price
        ws.Cells[1, col++].Value = "Akakçe Ürün Adý";    // C

        int mpStartCol = col;
        foreach (var mp in marketplaces)
            ws.Cells[1, col++].Value = mp;

        int bestPriceCol = col++;
        int deltaCol = col;

        ws.Cells[1, bestPriceCol].Value = "En Ýyi Fiyat";
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
                ws.Cells[r, 3].Hyperlink = new Uri(row.AkakceUrl);
                ws.Cells[r, 3].Style.Font.UnderLine = true;
                ws.Cells[r, 3].Style.Font.Color.SetColor(Color.FromArgb(31, 78, 121));
            }

            // Error row highlight
            if (!row.IsSuccess)
            {
                ws.Cells[r, 1].Style.Font.Color.SetColor(Color.FromArgb(156, 31, 31));
                ws.Cells[r, 1].Value = $"? {row.SearchName}";
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
    // Sheet 2 – Raw data (all sellers flat)
    // ??????????????????????????????????????????????????????????????????????????

    private static void CreateRawDataSheet(ExcelPackage package, List<PriceComparisonRow> rows)
    {
        var ws = package.Workbook.Worksheets.Add("Raw Data");

        var headers = new[] { "Ürün Adý", "Fiyatým", "Stock Out", "Akakçe Ürün Adý", "Marketplace", "En Ýyi Fiyat (Marketplace)", "En Ýyi Fiyat (Genel)", "Delta %", "Akakçe URL", "Durum" };
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
        ws.Cells[row, 1].Value = r.SearchName;
        SetPriceCell(ws.Cells[row, 2], r.MyPrice, r.IsStockOut);
        ws.Cells[row, 3].Value = r.IsStockOut ? "Yes" : "No";
        ws.Cells[row, 4].Value = string.IsNullOrEmpty(r.AkakceName) ? r.SearchName : r.AkakceName;
        ws.Cells[row, 5].Value = mp ?? "-";

        if (mpPrice.HasValue)
            SetNumericPriceCell(ws.Cells[row, 6], mpPrice.Value);
        else
            ws.Cells[row, 6].Value = "-";

        if (r.BestPrice > 0)
            SetNumericPriceCell(ws.Cells[row, 7], r.BestPrice);
        else
            ws.Cells[row, 7].Value = "-";

        if (r.DeltaPercent.HasValue)
        {
            ws.Cells[row, 8].Value = r.DeltaPercent.Value / 100;
            ws.Cells[row, 8].Style.Numberformat.Format = "+0.00%;-0.00%;0.00%";
        }
        else
        {
            ws.Cells[row, 8].Value = r.IsStockOut ? "Stock Out" : "-";
        }

        ws.Cells[row, 9].Value = r.AkakceUrl;
        ws.Cells[row, 10].Value = r.IsSuccess ? "OK" : $"Error: {r.ErrorMessage}";
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
