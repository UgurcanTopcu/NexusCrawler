using OfficeOpenXml;
using OfficeOpenXml.Style;
using Scrapper.Models;
using System.Drawing;

namespace Scrapper.Services;

/// <summary>
/// Excel report comparing MediaMarkt Retail (1P) prices on Akakce against the
/// cheapest MediaMarkt Marketplace (3P) offer from the Offer KPI export.
///
/// The headline column is "Retail vs Marketplace %": how much more (or less) the
/// retail listing costs than our own marketplace sellers charge for the same GTIN.
/// The market floor is carried alongside it for context.
/// </summary>
public class RetailVsMarketplaceExcelExporter
{
    private static readonly Color HeaderBlue = Color.FromArgb(31, 78, 121);
    private static readonly Color HeaderGreen = Color.FromArgb(14, 100, 55);
    private static readonly Color HeaderAmber = Color.FromArgb(146, 94, 4);
    private static readonly Color RowAlt = Color.FromArgb(242, 242, 242);
    private static readonly Color RetailExpensive = Color.FromArgb(255, 199, 206);  // red - retail costs more
    private static readonly Color RetailCheaper = Color.FromArgb(198, 239, 206);    // green - retail costs less
    private static readonly Color StockOutColor = Color.FromArgb(255, 235, 156);
    private static readonly Color MissingColor = Color.FromArgb(232, 232, 232);

    private const string PercentFormat = "+0.00%;-0.00%;0.00%";
    private const string PriceFormat = "#,##0.00";

    public void Export(List<PriceComparisonRow> rows, string filePath)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new InvalidOperationException("No rows to export.");

        using var package = new ExcelPackage();

        CreateSummarySheet(package, rows);
        CreateComparisonSheet(package, rows);
        CreateRawDataSheet(package, rows);

        package.SaveAs(new FileInfo(filePath));
    }

    // =====================================================================
    // Sheet 1 - Summary
    // =====================================================================

    private static void CreateSummarySheet(ExcelPackage package, List<PriceComparisonRow> rows)
    {
        var ws = package.Workbook.Worksheets.Add("Ozet");

        var matched = rows.Where(r => r.IsSuccess).ToList();
        var withRetail = matched.Where(r => r.RetailPrice > 0).ToList();
        var comparable = withRetail.Where(r => r.RetailVsMarketplacePercent.HasValue).ToList();

        int line = 2;

        ws.Cells[line, 2].Value = "Retail vs Marketplace - Ozet";
        ws.Cells[line, 2].Style.Font.Bold = true;
        ws.Cells[line, 2].Style.Font.Size = 14;
        line += 2;

        // -- Coverage ------------------------------------------------------
        WriteSectionHeader(ws, line, "Kapsam");
        line++;

        var coverage = new (string Label, int Count)[]
        {
            ("Girdideki benzersiz urun", rows.Count),
            ("Akakce'de eslesen", matched.Count),
            ("Eslesmeyen", rows.Count - matched.Count),
            ("MediaMarkt Retail fiyati bulunan", withRetail.Count),
            ("MediaMarkt Retail satmiyor", matched.Count - withRetail.Count),
            ("Karsilastirilabilir (iki fiyat da var)", comparable.Count)
        };

        foreach (var (label, count) in coverage)
        {
            ws.Cells[line, 2].Value = label;
            ws.Cells[line, 3].Value = count;
            line++;
        }

        line++;

        // -- Position ------------------------------------------------------
        WriteSectionHeader(ws, line, "Retail'in Marketplace'e gore konumu");
        line++;

        int retailMoreExpensive = comparable.Count(r => r.RetailVsMarketplacePercent!.Value > 0);
        int retailCheaper = comparable.Count(r => r.RetailVsMarketplacePercent!.Value < 0);
        int retailEqual = comparable.Count(r => r.RetailVsMarketplacePercent!.Value == 0);

        var position = new (string Label, int Count)[]
        {
            ("Retail daha pahali", retailMoreExpensive),
            ("Retail daha ucuz", retailCheaper),
            ("Esit", retailEqual)
        };

        foreach (var (label, count) in position)
        {
            ws.Cells[line, 2].Value = label;
            ws.Cells[line, 3].Value = count;
            line++;
        }

        if (comparable.Count > 0)
        {
            var deltas = comparable.Select(r => r.RetailVsMarketplacePercent!.Value).OrderBy(d => d).ToList();

            ws.Cells[line, 2].Value = "Ortalama fark";
            SetPercentCell(ws.Cells[line, 3], deltas.Average(), colour: false);
            line++;

            ws.Cells[line, 2].Value = "Medyan fark";
            SetPercentCell(ws.Cells[line, 3], deltas[deltas.Count / 2], colour: false);
            line++;
        }

        line++;

        // -- Distribution --------------------------------------------------
        WriteSectionHeader(ws, line, "Retail'in pahali oldugu urunlerin dagilimi");
        line++;

        var buckets = new (string Label, Func<decimal, bool> Test)[]
        {
            ("Retail ucuz veya esit (<= %0)", d => d <= 0),
            ("%0 - %10", d => d > 0 && d <= 10),
            ("%10 - %20", d => d > 10 && d <= 20),
            ("%20 - %50", d => d > 20 && d <= 50),
            ("%50+", d => d > 50)
        };

        foreach (var (label, test) in buckets)
        {
            ws.Cells[line, 2].Value = label;
            ws.Cells[line, 3].Value = comparable.Count(r => test(r.RetailVsMarketplacePercent!.Value));
            line++;
        }

        ws.Cells[line, 2].Value = "Toplam";
        ws.Cells[line, 2].Style.Font.Bold = true;
        ws.Cells[line, 3].Value = comparable.Count;
        ws.Cells[line, 3].Style.Font.Bold = true;
        line += 2;

        // -- Per category --------------------------------------------------
        WriteSectionHeader(ws, line, "Focus Category kirilimi");
        line++;

        ws.Cells[line, 2].Value = "Kategori";
        ws.Cells[line, 3].Value = "Urun";
        ws.Cells[line, 4].Value = "Retail fiyatli";
        ws.Cells[line, 5].Value = "Ort. fark";
        StyleHeader(ws.Cells[line, 2, line, 5], HeaderBlue);
        line++;

        foreach (var group in rows.GroupBy(r => r.FocusCategory).OrderByDescending(g => g.Count()))
        {
            var groupComparable = group.Where(r => r.RetailVsMarketplacePercent.HasValue).ToList();

            ws.Cells[line, 2].Value = string.IsNullOrWhiteSpace(group.Key) ? "(bos)" : group.Key;
            ws.Cells[line, 3].Value = group.Count();
            ws.Cells[line, 4].Value = group.Count(r => r.RetailPrice > 0);

            if (groupComparable.Count > 0)
                SetPercentCell(ws.Cells[line, 5],
                    groupComparable.Average(r => r.RetailVsMarketplacePercent!.Value), colour: false);
            else
                ws.Cells[line, 5].Value = "-";

            line++;
        }

        ws.Column(2).Width = 40;
        ws.Column(3).Width = 14;
        ws.Column(4).Width = 16;
        ws.Column(5).Width = 14;
    }

    private static void WriteSectionHeader(ExcelWorksheet ws, int line, string text)
    {
        ws.Cells[line, 2].Value = text;
        ws.Cells[line, 2].Style.Font.Bold = true;
        ws.Cells[line, 2].Style.Font.Color.SetColor(HeaderBlue);
    }

    // =====================================================================
    // Sheet 2 - Comparison
    // =====================================================================

    private static void CreateComparisonSheet(ExcelPackage package, List<PriceComparisonRow> rows)
    {
        var ws = package.Workbook.Worksheets.Add("Retail vs Marketplace");

        string[] headers =
        [
            "Focus Category", "Category Label", "Marka", "GTIN", "Product id", "Urun Adi",
            "CSV En Uygun Fiyat", "CSV Satici",
            "MediaMarkt Retail (Akakce)",
            "Retail vs Marketplace %",
            "Akakce En Ucuz", "En Ucuz Pazaryeri", "En Ucuz Satici",
            "Akakce En Ucuz (MM haric)",
            "Retail vs En Ucuz %", "Marketplace vs En Ucuz %",
            "Teklif Sayisi", "Akakce Urun Adi", "Eslesme Skoru", "Akakce URL", "Durum"
        ];

        for (int i = 0; i < headers.Length; i++)
            ws.Cells[1, i + 1].Value = headers[i];

        StyleHeader(ws.Cells[1, 1, 1, headers.Length], HeaderBlue);
        StyleHeader(ws.Cells[1, 7, 1, 8], HeaderGreen);      // our marketplace price
        StyleHeader(ws.Cells[1, 9, 1, 10], HeaderAmber);     // retail + headline metric

        // Order so the biggest retail premiums surface first; unmatched rows sink.
        var ordered = rows
            .OrderByDescending(r => r.RetailVsMarketplacePercent ?? decimal.MinValue)
            .ThenBy(r => r.SearchName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            int r = i + 2;
            int c = 1;

            ws.Cells[r, c++].Value = row.FocusCategory;
            ws.Cells[r, c++].Value = row.CategoryLabel;
            ws.Cells[r, c++].Value = row.SourceProductBrand;
            SetTextCell(ws.Cells[r, c++], row.Gtin);         // keep leading zeros
            SetTextCell(ws.Cells[r, c++], row.SourceProductId);
            ws.Cells[r, c++].Value = row.SearchName;

            SetPriceCell(ws.Cells[r, c++], row.MyPrice, row.IsStockOut);
            ws.Cells[r, c++].Value = row.CsvCheapestSeller;

            SetPriceCell(ws.Cells[r, c++], row.RetailPrice, isStockOut: false, missingLabel: "Satmiyor");

            SetPercentCell(ws.Cells[r, c++], row.RetailVsMarketplacePercent, colour: true);

            SetPriceCell(ws.Cells[r, c++], row.EffectiveMarketLowest, isStockOut: false);
            ws.Cells[r, c++].Value = Dash(row.CheapestMarketplace);
            ws.Cells[r, c++].Value = Dash(row.CheapestSeller);
            SetPriceCell(ws.Cells[r, c++], row.CheapestExcludingMediaMarkt, isStockOut: false);

            SetPercentCell(ws.Cells[r, c++], row.RetailVsMarketPercent, colour: false);
            SetPercentCell(ws.Cells[r, c++], row.MarketplaceVsMarketPercent, colour: false);

            ws.Cells[r, c++].Value = row.MarketOfferCount > 0 ? row.MarketOfferCount : (object)"-";
            ws.Cells[r, c++].Value = Dash(row.AkakceName);
            ws.Cells[r, c++].Value = row.MatchScore > 0 ? row.MatchScore : (object)"-";
            ws.Cells[r, c++].Value = Dash(row.AkakceUrl);
            ws.Cells[r, c++].Value = row.IsSuccess ? "Eslesti" : row.ErrorMessage ?? "-";

            if (i % 2 == 1)
                ShadeUncolouredCells(ws, r, headers.Length);
        }

        ws.View.FreezePanes(2, 7);
        ws.Cells[1, 1, ordered.Count + 1, headers.Length].AutoFilter = true;

        SetColumnWidths(ws, [16, 20, 14, 16, 14, 52, 18, 20, 22, 20, 16, 18, 20, 22, 18, 22, 12, 46, 14, 52, 40]);
    }

    // =====================================================================
    // Sheet 3 - Raw data
    // =====================================================================

    private static void CreateRawDataSheet(ExcelPackage package, List<PriceComparisonRow> rows)
    {
        var ws = package.Workbook.Worksheets.Add("Ham Veri");

        string[] headers =
        [
            "Offer id", "Focus Category", "Category Label", "gtin", "Product id",
            "Product Brand", "Product Name", "Total Active Offers", "Stock",
            "Winner Assortment Type", "Offer Total Price", "Offer Score Rank", "Seller Name",
            "Product - Sold items (30d)", "Product - GMV incl. Shipping (30d)",
            "Sessions by Product with PDP (30d)", "Sessions by Product with Add to Cart in pdp (30d)",
            "Akakce Name", "Akakce URL", "Market Lowest", "Market Offer Count",
            "Retail Price", "Cheapest Marketplace", "Cheapest Seller",
            "Cheapest Excl. MediaMarkt", "Retail vs Marketplace %", "Store Prices",
            "Match Score", "Match Notes", "Error"
        ];

        for (int i = 0; i < headers.Length; i++)
            ws.Cells[1, i + 1].Value = headers[i];

        StyleHeader(ws.Cells[1, 1, 1, headers.Length], HeaderBlue);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int r = i + 2;
            int c = 1;

            SetTextCell(ws.Cells[r, c++], row.OfferId);
            ws.Cells[r, c++].Value = row.FocusCategory;
            ws.Cells[r, c++].Value = row.CategoryLabel;
            SetTextCell(ws.Cells[r, c++], row.Gtin);
            SetTextCell(ws.Cells[r, c++], row.SourceProductId);
            ws.Cells[r, c++].Value = row.SourceProductBrand;
            ws.Cells[r, c++].Value = row.SearchName;
            ws.Cells[r, c++].Value = row.TotalActiveOffers;
            ws.Cells[r, c++].Value = row.SourceStock;
            ws.Cells[r, c++].Value = row.WinnerAssortmentType;
            SetPriceCell(ws.Cells[r, c++], row.MyPrice, row.IsStockOut);
            ws.Cells[r, c++].Value = row.OfferScoreRank;
            ws.Cells[r, c++].Value = row.SourceSellerName;
            ws.Cells[r, c++].Value = row.ProductSoldItems30d;
            ws.Cells[r, c++].Value = row.ProductGmvInclShipping30d;
            ws.Cells[r, c++].Value = row.SessionsByProductWithPdp30d;
            ws.Cells[r, c++].Value = row.SessionsByProductWithAddToCartInPdp30d;
            ws.Cells[r, c++].Value = row.AkakceName;
            ws.Cells[r, c++].Value = row.AkakceUrl;
            SetPriceCell(ws.Cells[r, c++], row.MarketLowestPrice, isStockOut: false);
            ws.Cells[r, c++].Value = row.MarketOfferCount;
            SetPriceCell(ws.Cells[r, c++], row.RetailPrice, isStockOut: false);
            ws.Cells[r, c++].Value = row.CheapestMarketplace;
            ws.Cells[r, c++].Value = row.CheapestSeller;
            SetPriceCell(ws.Cells[r, c++], row.CheapestExcludingMediaMarkt, isStockOut: false);
            SetPercentCell(ws.Cells[r, c++], row.RetailVsMarketplacePercent, colour: false);
            ws.Cells[r, c++].Value = FormatStorePrices(row.StorePrices);
            ws.Cells[r, c++].Value = row.MatchScore;
            ws.Cells[r, c++].Value = row.MatchNotes;
            ws.Cells[r, c++].Value = row.ErrorMessage ?? string.Empty;
        }

        ws.View.FreezePanes(2, 1);
        ws.Cells[1, 1, rows.Count + 1, headers.Length].AutoFilter = true;
    }

    private static string FormatStorePrices(Dictionary<string, decimal> storePrices) =>
        storePrices.Count == 0
            ? string.Empty
            : string.Join("; ", storePrices
                .OrderBy(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value:#,##0.00}"));

    // =====================================================================
    // Cell helpers
    // =====================================================================

    private static void StyleHeader(ExcelRange range, Color colour)
    {
        range.Style.Font.Bold = true;
        range.Style.Font.Color.SetColor(Color.White);
        range.Style.Fill.PatternType = ExcelFillStyle.Solid;
        range.Style.Fill.BackgroundColor.SetColor(colour);
        range.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        range.Style.WrapText = true;
    }

    /// <summary>
    /// Write a price, or a marker when there is none. A missing retail price means
    /// MediaMarkt does not list the product, which is a real finding rather than an
    /// error, so it gets its own label instead of being left blank.
    /// </summary>
    private static void SetPriceCell(
        ExcelRange cell,
        decimal price,
        bool isStockOut,
        string missingLabel = "-")
    {
        if (isStockOut)
        {
            cell.Value = "Stock Out";
            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            cell.Style.Fill.BackgroundColor.SetColor(StockOutColor);
            return;
        }

        if (price <= 0)
        {
            cell.Value = missingLabel;
            if (missingLabel != "-")
            {
                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(MissingColor);
            }
            return;
        }

        cell.Value = price;
        cell.Style.Numberformat.Format = PriceFormat;
    }

    private static void SetPercentCell(ExcelRange cell, decimal? percent, bool colour)
    {
        if (!percent.HasValue)
        {
            cell.Value = "-";
            return;
        }

        // Stored as a fraction so Excel's percent format renders it correctly.
        cell.Value = percent.Value / 100m;
        cell.Style.Numberformat.Format = PercentFormat;

        if (!colour) return;

        cell.Style.Font.Bold = true;
        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
        cell.Style.Fill.BackgroundColor.SetColor(percent.Value > 0 ? RetailExpensive : RetailCheaper);
    }

    /// <summary>
    /// Write as text so Excel keeps leading zeros and does not turn a 13-digit GTIN
    /// into scientific notation.
    /// </summary>
    private static void SetTextCell(ExcelRange cell, string value)
    {
        cell.Style.Numberformat.Format = "@";
        cell.Value = value;
    }

    private static string Dash(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static void ShadeUncolouredCells(ExcelWorksheet ws, int row, int columnCount)
    {
        for (int c = 1; c <= columnCount; c++)
        {
            var cell = ws.Cells[row, c];
            if (cell.Style.Fill.PatternType == ExcelFillStyle.Solid) continue;

            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            cell.Style.Fill.BackgroundColor.SetColor(RowAlt);
        }
    }

    private static void SetColumnWidths(ExcelWorksheet ws, int[] widths)
    {
        for (int i = 0; i < widths.Length; i++)
            ws.Column(i + 1).Width = widths[i];
    }
}
