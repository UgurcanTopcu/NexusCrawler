using OfficeOpenXml;
using OfficeOpenXml.Style;
using Scrapper.Models;
using System.Drawing;
using System.Globalization;
using System.Text.Json;

namespace Scrapper.Services;

/// <summary>
/// Price Index: reads a daily Mirakl export, merges prices into a JSON history file,
/// and generates an Excel trend report (daily delta, 7d, 30d, all-time low/high, history columns).
/// </summary>
public class PriceIndexService
{
    private static readonly string DataFolder =
        Path.Combine(Directory.GetCurrentDirectory(), "PriceIndexData");

    private static readonly string HistoryFilePath =
        Path.Combine(DataFolder, "price-index-history.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private const int RECENT_HISTORY_DAYS = 30;

    // ─── Public API ───────────────────────────────────────────────────────────

    public async Task ProcessAsync(
        Stream excelStream,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null,
        string? dateOverride = null,
        string? fileName = null)
    {
        try
        {
            EnsureDataFolder();

            var isCsv = !string.IsNullOrEmpty(fileName) &&
                        fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

            await onProgress(1, $"Reading {(isCsv ? "CSV" : "Excel")} file...", "info");

            List<InputProduct> inputProducts;
            DateTime snapshotDate;

            if (isCsv)
            {
                inputProducts = ReadInputProductsFromCsv(excelStream);

                if (!string.IsNullOrWhiteSpace(dateOverride) &&
                    DateTime.TryParseExact(dateOverride, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var csvParsed))
                {
                    snapshotDate = csvParsed;
                    await onProgress(5, $"Snapshot date: {snapshotDate:yyyy-MM-dd} (manual override)", "info");
                }
                else
                {
                    snapshotDate = DateTime.Today;
                    await onProgress(5, $"Snapshot date: {snapshotDate:yyyy-MM-dd} (today \u2014 CSV has no date metadata; set manually if needed)", "warning");
                }
            }
            else
            {
                using var package = new ExcelPackage(excelStream);

                if (!string.IsNullOrWhiteSpace(dateOverride) &&
                    DateTime.TryParseExact(dateOverride, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var xlsParsed))
                {
                    snapshotDate = xlsParsed;
                    await onProgress(5, $"Snapshot date: {snapshotDate:yyyy-MM-dd} (manual override)", "info");
                }
                else
                {
                    snapshotDate = ExtractDate(package);
                    await onProgress(5, $"Snapshot date: {snapshotDate:yyyy-MM-dd} (from file metadata)", "info");
                }

                inputProducts = ReadInputProducts(package);
            }

            var dateKey = snapshotDate.ToString("yyyy-MM-dd");
            if (inputProducts.Count == 0)
            {
                await onProgress(100, "No products found in the file (expected headers: Product Name, Offer Total Price)", "warning");
                await SendComplete(onProgress, null, 0);
                return;
            }

            await onProgress(10, $"Found {inputProducts.Count} products in upload", "success");

            var history = await LoadHistoryAsync();
            int newProducts = 0;
            int updatedProducts = 0;

            // One-time migration: re-key rank-based entries to stable identifiers
            if (MigrateRankKeysToIdentifierKeys(history))
                await onProgress(12, "Migrated history from rank-based to identifier-based keys", "info");

            await onProgress(15, $"History currently has {history.Products.Count} tracked product(s)", "info");

            foreach (var input in inputProducts)
            {
                // Use stable identifier (OfferId > Gtin > ProductId > rank) to prevent
                // cross-contamination when products shift GMV ranking positions between uploads.
                var key = GetProductKey(input.OfferId, input.ProductId, input.Gtin, input.Rank);

                if (history.Products.TryGetValue(key, out var existing))
                {
                    existing.PriceHistory[dateKey] = input.Price;
                    // Always refresh rank (changes with every upload) and metadata
                    existing.Rank = input.Rank;
                    existing.Name = input.Name;
                    existing.Brand = input.Brand;
                    existing.Category = input.Category;
                    existing.Gtin = input.Gtin;
                    existing.OfferId = input.OfferId;
                    existing.ProductId = input.ProductId;
                    existing.SellerName = input.SellerName;
                existing.SoldItems30Days = input.SoldItems30Days;
                    updatedProducts++;
                }
                else
                {
                    history.Products[key] = new PriceIndexProduct
                    {
                        Rank = input.Rank,
                        Name = input.Name,
                        Brand = input.Brand,
                        Category = input.Category,
                        Gtin = input.Gtin,
                        OfferId = input.OfferId,
                        ProductId = input.ProductId,
                        SellerName = input.SellerName,
                        SoldItems30Days = input.SoldItems30Days,
                        PriceHistory = new Dictionary<string, decimal>(StringComparer.Ordinal)
                        {
                            [dateKey] = input.Price
                        }
                    };
                    newProducts++;
                }
            }

            await onProgress(30, $"{updatedProducts} product(s) updated, {newProducts} new product(s) added", "success");

            await SaveHistoryAsync(history);
            await onProgress(40, "History saved to disk", "success");

            await onProgress(50, "Building price index report...", "info");
            var reportRows = BuildReport(history, dateKey);

            await onProgress(80, "Creating Excel report...", "info");
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var reportFileName = $"PriceIndex_{dateKey}_{timestamp}.xlsx";
            var filePath = Path.Combine(DataFolder, reportFileName);
            ExportReport(reportRows, filePath, dateKey);

            int increases = reportRows.Count(r => r.DailyChangePct > 0);
            int decreases = reportRows.Count(r => r.DailyChangePct < 0);
            int unchanged = reportRows.Count(r => r.DailyChangePct == 0);
            int noHistory = reportRows.Count(r => r.DailyChangePct == null);

            await onProgress(100,
                $"{reportRows.Count} products — {increases} up | {decreases} down | {unchanged} flat | {noHistory} new",
                "success");

            await SendComplete(onProgress, reportFileName, reportRows.Count);
        }
        catch (Exception ex)
        {
            await onProgress(100, $"Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, 0);
        }
    }

    public static async Task ClearHistoryAsync()
    {
        if (File.Exists(HistoryFilePath))
            File.Delete(HistoryFilePath);

        await Task.CompletedTask;
    }

    public static async Task<List<string>> GetSnapshotDatesAsync()
    {
        var history = await LoadHistoryAsync();
        return history.Products.Values
            .SelectMany(p => p.PriceHistory.Keys)
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>Removes a single snapshot date from every product's price history.</summary>
    public static async Task<int> DeleteSnapshotDateAsync(string dateKey)
    {
        var history = await LoadHistoryAsync();
        int removed = 0;
        foreach (var product in history.Products.Values)
            if (product.PriceHistory.Remove(dateKey)) removed++;

        await SaveHistoryAsync(history);
        return removed;
    }

    /// <summary>Returns the path to the JSON history file for download.</summary>
    public static string GetHistoryFilePath() => HistoryFilePath;

    /// <summary>Replaces the entire history with the content of the supplied JSON stream.</summary>
    public static async Task ImportHistoryAsync(Stream jsonStream)
    {
        using var reader = new StreamReader(jsonStream);
        var json = await reader.ReadToEndAsync();
        var imported = JsonSerializer.Deserialize<PriceIndexHistory>(json, JsonOpts)
            ?? throw new InvalidOperationException("Invalid history JSON — could not deserialize.");

        EnsureDataFolder();
        await SaveHistoryAsync(imported);
    }

    /// <summary>
    /// Reads an "offers" format Excel (Offer SKU, Price, EAN columns) and injects prices into
    /// existing history entries at the specified date. Only updates already-tracked products —
    /// matches by OfferId (Offer SKU) first, then Gtin (EAN).
    /// </summary>
    public async Task BackfillAsync(
        Stream excelStream,
        Func<int, string, string, Task> onProgress,
        string dateOverride)
    {
        try
        {
            EnsureDataFolder();

            if (!DateTime.TryParseExact(dateOverride, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedDate))
            {
                await onProgress(100, "A valid date is required for backfill", "error");
                await SendComplete(onProgress, null, 0);
                return;
            }

            var dateKey = parsedDate.ToString("yyyy-MM-dd");
            await onProgress(5, $"Backfilling data for: {dateKey}", "info");

            await onProgress(10, "Reading offers file...", "info");
            using var package = new ExcelPackage(excelStream);

            var offers = ReadOffersFile(package);
            if (offers.Count == 0)
            {
                await onProgress(100, "No rows found — expected columns: Offer SKU, Price, EAN", "warning");
                await SendComplete(onProgress, null, 0);
                return;
            }

            await onProgress(20, $"Found {offers.Count} offer rows in file", "info");

            var history = await LoadHistoryAsync();
            await onProgress(25, $"History has {history.Products.Count} tracked product(s)", "info");

            // Build lookup maps for fast matching
            var byOfferId = history.Products.Values
                .Where(p => !string.IsNullOrWhiteSpace(p.OfferId))
                .ToLookup(p => p.OfferId, StringComparer.OrdinalIgnoreCase);

            var byGtin = history.Products.Values
                .Where(p => !string.IsNullOrWhiteSpace(p.Gtin))
                .ToLookup(p => p.Gtin, StringComparer.OrdinalIgnoreCase);

            int injected = 0;
            int overwritten = 0;
            int unmatched = 0;

            foreach (var offer in offers)
            {
                // Prefer OfferId match; fall back to Gtin
                IEnumerable<PriceIndexProduct> candidates =
                    !string.IsNullOrWhiteSpace(offer.OfferSku) ? byOfferId[offer.OfferSku] :
                    !string.IsNullOrWhiteSpace(offer.Ean)      ? byGtin[offer.Ean]         :
                    [];

                // If OfferId matched nothing, try Gtin as secondary
                if (!candidates.Any() && !string.IsNullOrWhiteSpace(offer.Ean))
                    candidates = byGtin[offer.Ean];

                var list = candidates.ToList();
                if (list.Count == 0) { unmatched++; continue; }

                foreach (var product in list)
                {
                    if (product.PriceHistory.ContainsKey(dateKey)) overwritten++;
                    else injected++;
                    product.PriceHistory[dateKey] = offer.Price;
                }
            }

            await onProgress(80, $"Matched: {injected} injected, {overwritten} overwritten, {unmatched} unmatched", "info");

            await SaveHistoryAsync(history);
            await onProgress(95, "History saved to disk", "success");
            await onProgress(100,
                $"Backfill complete for {dateKey} — {injected + overwritten} products updated, {unmatched} not in current history",
                "success");

            await SendComplete(onProgress, null, injected + overwritten);
        }
        catch (Exception ex)
        {
            await onProgress(100, $"Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, 0);
        }
    }

    // ─── Product identity ─────────────────────────────────────────────────────

    /// <summary>
    /// Generates a stable product key using the best available identifier.
    /// Rank-based keys caused cross-contamination when products shifted positions between uploads.
    /// </summary>
    private static string GetProductKey(string offerId, string productId, string gtin, int rank)
    {
        if (!string.IsNullOrWhiteSpace(offerId))   return $"offer:{offerId.Trim()}";
        if (!string.IsNullOrWhiteSpace(gtin))      return $"gtin:{gtin.Trim()}";
        if (!string.IsNullOrWhiteSpace(productId)) return $"product:{productId.Trim()}";
        return $"rank:{rank:D5}";
    }

    /// <summary>
    /// One-time migration: re-keys rank-based history entries to stable identifier keys.
    /// Returns true if any entries were migrated (caller should save).
    /// </summary>
    private static bool MigrateRankKeysToIdentifierKeys(PriceIndexHistory history)
    {
        var rankEntries = history.Products
            .Where(kv => kv.Key.StartsWith("rank:", StringComparison.Ordinal))
            .ToList();

        if (rankEntries.Count == 0) return false;

        int migrated = 0;
        foreach (var (oldKey, product) in rankEntries)
        {
            var newKey = GetProductKey(product.OfferId, product.ProductId, product.Gtin, product.Rank);
            if (newKey == oldKey) continue; // no stable identifier available

            history.Products.Remove(oldKey);

            if (history.Products.TryGetValue(newKey, out var existing))
            {
                // Merge: add date entries that don't already exist in the target
                foreach (var (date, price) in product.PriceHistory)
                    existing.PriceHistory.TryAdd(date, price);
            }
            else
            {
                history.Products[newKey] = product;
            }

            migrated++;
        }

        return migrated > 0;
    }

    private static void EnsureDataFolder()
    {
        if (!Directory.Exists(DataFolder))
            Directory.CreateDirectory(DataFolder);
    }

    // ─── Date extraction ──────────────────────────────────────────────────────

    private static DateTime ExtractDate(ExcelPackage package)
    {
        var created = package.Workbook.Properties.Created;
        if (created != default && created.Year > 2020)
            return created.Date;

        var modified = package.Workbook.Properties.Modified;
        if (modified != default && modified.Year > 2020)
            return modified.Date;

        return DateTime.Today;
    }

    // ─── Input parsing ────────────────────────────────────────────────────────

    private sealed record InputProduct(int Rank, string Name, string Brand, string Category,
        string Gtin, string OfferId, string ProductId, string SellerName, decimal Price, int SoldItems30Days);

    private sealed record OfferRow(string OfferSku, string Ean, string SellerName, decimal Price);

    private static List<InputProduct> ReadInputProducts(ExcelPackage package)
    {
        var results = new List<InputProduct>();
        var ws = package.Workbook.Worksheets.FirstOrDefault();
        if (ws?.Dimension == null) return results;

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 1; c <= ws.Dimension.Columns; c++)
        {
            var h = ws.Cells[1, c].Value?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(h) && !headers.ContainsKey(h))
                headers[h] = c;
        }

        int Col(string name) => headers.TryGetValue(name, out var c) ? c : 0;
        string Cell(int r, int c) => c > 0 ? ws.Cells[r, c].Value?.ToString()?.Trim() ?? "" : "";

        int nameCol = Col("Product Name");
        int priceCol = Col("Offer Total Price");
        if (nameCol == 0 || priceCol == 0) return results;

        int brandCol = Col("Product Brand");
        int catCol = Col("Category Label");
        int gtinCol = Col("gtin");
        int offerIdCol = Col("Offer id");
        int productIdCol = Col("Product id");
        int sellerCol = Col("Seller Name");
        int soldItems30DaysCol = Col("Product - Sold items (30d)");

        int rank = 0;

        for (int r = 2; r <= ws.Dimension.Rows; r++)
        {
            var name = Cell(r, nameCol);
            if (string.IsNullOrWhiteSpace(name)) continue;

            rank++;
            var (price, _) = ParsePrice(Cell(r, priceCol));

            results.Add(new InputProduct(
                Rank: rank,
                Name: name,
                Brand: Cell(r, brandCol),
                Category: Cell(r, catCol),
                Gtin: Cell(r, gtinCol),
                OfferId: Cell(r, offerIdCol),
                ProductId: Cell(r, productIdCol),
                SellerName: Cell(r, sellerCol),
                Price: price,
                SoldItems30Days: ParseWholeNumber(Cell(r, soldItems30DaysCol))));
        }

        return results;
    }

    private static List<InputProduct> ReadInputProductsFromCsv(Stream csvStream)
    {
        var results = new List<InputProduct>();

        using var reader = new StreamReader(csvStream, leaveOpen: true);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);

        if (lines.Count < 2) return results;

        var delimiter    = DetectCsvDelimiter(lines[0]);
        var headerFields = ParseCsvLine(lines[0], delimiter);

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headerFields.Length; i++)
        {
            var h = headerFields[i].Trim();
            if (!string.IsNullOrWhiteSpace(h) && !headers.ContainsKey(h))
                headers[h] = i;
        }

        int Col(string name) => headers.TryGetValue(name, out var c) ? c : -1;
        string Field(string[] f, int idx) => idx >= 0 && idx < f.Length ? f[idx].Trim() : "";

        int nameCol      = Col("Product Name");
        int priceCol     = Col("Offer Total Price");
        if (nameCol < 0 || priceCol < 0) return results;

        int brandCol     = Col("Product Brand");
        int catCol       = Col("Category Label");
        int gtinCol      = Col("gtin");
        int offerIdCol   = Col("Offer id");
        int productIdCol = Col("Product id");
        int sellerCol    = Col("Seller Name");
        int soldItems30DaysCol = Col("Product - Sold items (30d)");

        int rank = 0;
        for (int i = 1; i < lines.Count; i++)
        {
            var fields = ParseCsvLine(lines[i], delimiter);
            var name   = Field(fields, nameCol);
            if (string.IsNullOrWhiteSpace(name)) continue;

            rank++;
            var (price, _) = ParsePrice(Field(fields, priceCol));

            results.Add(new InputProduct(
                Rank: rank,
                Name: name,
                Brand: Field(fields, brandCol),
                Category: Field(fields, catCol),
                Gtin: Field(fields, gtinCol),
                OfferId: Field(fields, offerIdCol),
                ProductId: Field(fields, productIdCol),
                SellerName: Field(fields, sellerCol),
                Price: price,
                SoldItems30Days: ParseWholeNumber(Field(fields, soldItems30DaysCol))));
        }

        return results;
    }

    // Picks the delimiter with the most occurrences — covers comma, semicolon, tab
    private static char DetectCsvDelimiter(string headerLine)
    {
        char[] candidates = [',', ';', '\t'];
        return candidates.OrderByDescending(d => headerLine.Count(c => c == d)).First();
    }

    private static string[] ParseCsvLine(string line, char delimiter)
    {
        var fields  = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == delimiter) { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return [.. fields];
    }

    /// <summary>
    /// Parses an "offers" export with columns: Seller, Seller ID, Offer SKU, Product SKU,
    /// Price, Discount price, EAN.
    /// Uses Discount price when it is a valid non-zero value lower than Price; otherwise uses Price.
    /// </summary>
    private static List<OfferRow> ReadOffersFile(ExcelPackage package)
    {
        var results = new List<OfferRow>();
        var ws = package.Workbook.Worksheets.FirstOrDefault();
        if (ws?.Dimension == null) return results;

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 1; c <= ws.Dimension.Columns; c++)
        {
            var h = ws.Cells[1, c].Value?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(h) && !headers.ContainsKey(h))
                headers[h] = c;
        }

        int Col(string name) => headers.TryGetValue(name, out var c) ? c : 0;
        string Cell(int r, int c) => c > 0 ? ws.Cells[r, c].Value?.ToString()?.Trim() ?? "" : "";

        int offerSkuCol     = Col("Offer SKU");
        int eanCol          = Col("EAN");
        int priceCol        = Col("Price");
        int discountPriceCol = Col("Discount price");
        int sellerCol       = Col("Seller");

        // Need at least one identifier column and a price column
        if ((offerSkuCol == 0 && eanCol == 0) || priceCol == 0) return results;

        for (int r = 2; r <= ws.Dimension.Rows; r++)
        {
            var offerSku = Cell(r, offerSkuCol);
            var ean      = Cell(r, eanCol);
            if (string.IsNullOrWhiteSpace(offerSku) && string.IsNullOrWhiteSpace(ean)) continue;

            var (basePrice, _)     = ParsePrice(Cell(r, priceCol));
            var (discountPrice, _) = ParsePrice(Cell(r, discountPriceCol));

            // Prefer discount price when it is valid and lower than the base price
            decimal price = discountPrice > 0 && discountPrice < basePrice ? discountPrice : basePrice;
            if (price <= 0) continue;

            results.Add(new OfferRow(offerSku, ean, Cell(r, sellerCol), price));
        }

        return results;
    }

    private static (decimal Price, bool IsStockOut) ParsePrice(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (0, true);

        var t = raw.Trim();
        if (t.Equals("stock out", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("stok yok", StringComparison.OrdinalIgnoreCase) || t == "-")
            return (0, true);

        // Strip currency symbols and whitespace first.
        var c = t.Replace("TL", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("\u20ba", "")
                 .Replace(" ", "")
                 .Trim();

        // CSV imports often contain locale-specific price strings. If we parse with
        // InvariantCulture first, values like "743,40" are interpreted as 74340
        // because comma is treated as a thousands separator. Detect the decimal separator
        // pattern first and normalize before parsing.
        if (TryParseLocalizedPrice(c, out var parsed) && parsed > 0)
            return (Math.Truncate(parsed), false);

        if (decimal.TryParse(c, NumberStyles.Any, new CultureInfo("tr-TR"), out var pTr) && pTr > 0)
            return (Math.Truncate(pTr), false);

        if (decimal.TryParse(c, NumberStyles.Any, CultureInfo.InvariantCulture, out var pInv) && pInv > 0)
            return (Math.Truncate(pInv), false);

        return (0, true);
    }

    private static bool TryParseLocalizedPrice(string value, out decimal parsed)
    {
        parsed = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var lastComma = value.LastIndexOf(',');
        var lastDot = value.LastIndexOf('.');

        // Both separators exist -> whichever appears last is almost certainly the decimal separator.
        if (lastComma >= 0 && lastDot >= 0)
        {
            if (lastComma > lastDot)
            {
                // Example: 1.234,56 -> 1234.56
                var normalized = value.Replace(".", "").Replace(',', '.');
                return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
            }

            // Example: 1,234.56 -> 1234.56
            var invariant = value.Replace(",", "");
            return decimal.TryParse(invariant, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        // Only comma exists.
        if (lastComma >= 0)
        {
            var digitsAfterComma = value.Length - lastComma - 1;

            // Prices almost always have 1-2 decimal digits in these files.
            if (digitsAfterComma is > 0 and <= 2)
            {
                var normalized = value.Replace(',', '.');
                return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
            }

            // Otherwise treat comma as grouping.
            var invariant = value.Replace(",", "");
            return decimal.TryParse(invariant, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        // Only dot exists.
        if (lastDot >= 0)
        {
            var digitsAfterDot = value.Length - lastDot - 1;

            if (digitsAfterDot is > 0 and <= 2)
                return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);

            // Otherwise treat dot as grouping.
            var normalized = value.Replace(".", "");
            return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
    }

    private static int ParseWholeNumber(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;

        var normalized = raw.Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);

        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, value)
            : 0;
    }

    // ─── History persistence ──────────────────────────────────────────────────

    private static async Task<PriceIndexHistory> LoadHistoryAsync()
    {
        EnsureDataFolder();
        if (!File.Exists(HistoryFilePath)) return new PriceIndexHistory();

        try
        {
            var json = await File.ReadAllTextAsync(HistoryFilePath);
            return JsonSerializer.Deserialize<PriceIndexHistory>(json, JsonOpts) ?? new PriceIndexHistory();
        }
        catch
        {
            return new PriceIndexHistory();
        }
    }

    private static async Task SaveHistoryAsync(PriceIndexHistory history)
    {
        var json = JsonSerializer.Serialize(history, JsonOpts);
        await File.WriteAllTextAsync(HistoryFilePath, json);
    }

    // ─── Report building ──────────────────────────────────────────────────────

    private static List<PriceIndexReportRow> BuildReport(PriceIndexHistory history, string todayKey)
    {
        var today = DateOnly.ParseExact(todayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = new List<PriceIndexReportRow>();

        foreach (var (_, product) in history.Products)
        {
            if (!product.PriceHistory.TryGetValue(todayKey, out var todayPrice))
                continue; // Only include products present in today's upload

            bool isStockOut = todayPrice == 0;

            var previousSnapshot = GetLatestSnapshotBefore(product, todayKey);
            var snapshot7 = FindNearestSnapshot(product, today, -7);
            var snapshot30 = FindNearestSnapshot(product, today, -30);

            var inStockPrices = product.PriceHistory.Values.Where(p => p > 0).ToList();
            decimal minPrice = inStockPrices.Count > 0 ? inStockPrices.Min() : 0;
            decimal maxPrice = inStockPrices.Count > 0 ? inStockPrices.Max() : 0;

            // Last 30 snapshots chronologically
            var recentHistory = product.PriceHistory
                .Where(kv => string.Compare(kv.Key, todayKey, StringComparison.Ordinal) <= 0)
                .OrderBy(kv => kv.Key)
                .TakeLast(RECENT_HISTORY_DAYS)
                .Select(kv => (Date: kv.Key, Price: kv.Value))
                .ToList();

            rows.Add(new PriceIndexReportRow
            {
                Rank = product.Rank,
                Name = product.Name,
                Brand = product.Brand,
                Category = product.Category,
                Gtin = product.Gtin,
                OfferId = product.OfferId,
                ProductId = product.ProductId,
                SellerName = product.SellerName,
                SoldItems30Days = product.SoldItems30Days,
                TodayPrice = todayPrice,
                IsStockOut = isStockOut,
                PreviousSnapshotDate = previousSnapshot.DateKey,
                PreviousPrice = previousSnapshot.Price,
                DailyChange = CalcDelta(todayPrice, previousSnapshot.Price),
                DailyChangePct = CalcDeltaPct(todayPrice, previousSnapshot.Price),
                Price7DaysAgoDate = snapshot7.DateKey,
                Price7DaysAgo = snapshot7.Price,
                Change7DayPct = CalcDeltaPct(todayPrice, snapshot7.Price),
                Price30DaysAgoDate = snapshot30.DateKey,
                Price30DaysAgo = snapshot30.Price,
                Change30DayPct = CalcDeltaPct(todayPrice, snapshot30.Price),
                MinPrice = minPrice,
                MaxPrice = maxPrice,
                SnapshotCount = product.PriceHistory.Count,
                RecentHistory = recentHistory
            });
        }

        // Sort by GMV rank (ascending = highest GMV first)
        return rows.OrderBy(r => r.Rank).ToList();
    }

    private static (string DateKey, decimal? Price) GetLatestSnapshotBefore(PriceIndexProduct product, string todayKey)
    {
        var found = product.PriceHistory.Keys
            .Where(d => string.Compare(d, todayKey, StringComparison.Ordinal) < 0)
            .OrderByDescending(d => d)
            .FirstOrDefault();

        if (found == null) return (string.Empty, null);

        var price = product.PriceHistory[found];
        return (found, price == 0 ? null : price);
    }

    private static (string DateKey, decimal? Price) FindNearestSnapshot(PriceIndexProduct product, DateOnly today, int offsetDays)
    {
        var todayKey = today.ToString("yyyy-MM-dd");
        var target = today.AddDays(offsetDays).ToString("yyyy-MM-dd");
        var found = product.PriceHistory.Keys
            .Where(d => string.Compare(d, target, StringComparison.Ordinal) <= 0 &&
                        string.Compare(d, todayKey, StringComparison.Ordinal) < 0)
            .OrderByDescending(d => d)
            .FirstOrDefault();

        if (found == null) return (string.Empty, null);

        var price = product.PriceHistory[found];
        return (found, price == 0 ? null : price); // stock-out = no usable price
    }

    private static decimal? CalcDelta(decimal current, decimal? previous)
    {
        if (previous is null or 0 || current == 0) return null;
        return current - previous.Value;
    }

    private static decimal? CalcDeltaPct(decimal current, decimal? previous)
    {
        if (previous is null or 0 || current == 0) return null;
        return Math.Round((current - previous.Value) / previous.Value * 100, 2);
    }

    // ─── Excel export ─────────────────────────────────────────────────────────

    private static readonly Color ColHeaderDark  = Color.FromArgb(31, 78, 121);
    private static readonly Color ColHeaderGreen = Color.FromArgb(14, 100, 55);
    private static readonly Color RowAlt         = Color.FromArgb(242, 242, 242);
    private static readonly Color RedBg          = Color.FromArgb(255, 199, 206);
    private static readonly Color RedFg          = Color.FromArgb(156, 0, 6);
    private static readonly Color GreenBg        = Color.FromArgb(198, 239, 206);
    private static readonly Color GreenFg        = Color.FromArgb(0, 97, 0);

    private static void ExportReport(List<PriceIndexReportRow> rows, string filePath, string dateKey)
    {
        using var package = new ExcelPackage();

        WriteDetailedReportSheet(package.Workbook.Worksheets.Add("Price Index"), rows, dateKey);

        var soldRows = rows
            .Where(r => r.SoldItems30Days > 0)
            .OrderBy(r => r.Rank)
            .ToList();

        if (soldRows.Count > 0)
            WriteDetailedReportSheet(package.Workbook.Worksheets.Add("📦 Sold 30d"), soldRows, dateKey);

        WriteExecutiveSummarySheet(package, rows, dateKey);
        WritePriceMoversSheet(package, "📈 Price Increases",
            rows.Where(r => r.DailyChangePct > 0).OrderByDescending(r => r.DailyChangePct).ToList(), RedBg, dateKey);
        WritePriceMoversSheet(package, "📉 Price Decreases",
            rows.Where(r => r.DailyChangePct < 0).OrderBy(r => r.DailyChangePct).ToList(), GreenBg, dateKey);
        WriteCategorySummarySheet(package, rows);
        WriteSellerSummarySheet(package, rows);

        package.SaveAs(new FileInfo(filePath));
    }

    private static void WriteDetailedReportSheet(ExcelWorksheet ws, List<PriceIndexReportRow> rows, string dateKey)
    {
        string[] fixedHeaders =
        [
            "Rank", "Offer Id", "Product Id", "Product Name", "Brand", "Category", "GTIN", "Seller", "Sold items (30d)",
            $"Today Price ({dateKey})", "Previous Snapshot Date", "Previous Price", $"Today vs Previous Δ ({dateKey})", $"Today vs Previous Δ% ({dateKey})",
            "7d Baseline Date", "7d Baseline Price", $"Today vs 7d Δ% ({dateKey})",
            "30d Baseline Date", "30d Baseline Price", $"Today vs 30d Δ% ({dateKey})",
            "All-Time Low", "All-Time High", "Snapshots"
        ];

        int col = 1;
        foreach (var header in fixedHeaders)
            ws.Cells[1, col++].Value = header;

        var allDates = rows
            .SelectMany(r => r.RecentHistory.Select(h => h.Date))
            .Distinct()
            .Order()
            .ToList();

        int firstDateCol = col;
        foreach (var date in allDates)
            ws.Cells[1, col++].Value = date;

        int totalCols = col - 1;

        using (var range = ws.Cells[1, 1, 1, totalCols])
        {
            range.Style.Font.Bold = true;
            range.Style.Font.Color.SetColor(Color.White);
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(ColHeaderDark);
            range.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            range.Style.Border.Bottom.Style = ExcelBorderStyle.Medium;
            range.Style.Border.Bottom.Color.SetColor(Color.White);
        }

        if (allDates.Count > 0)
        {
            using var dateHeaderRange = ws.Cells[1, firstDateCol, 1, totalCols];
            dateHeaderRange.Style.Fill.BackgroundColor.SetColor(ColHeaderGreen);
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var reportRow = rows[i];
            int row = i + 2;
            bool altRow = i % 2 == 1;

            void StyleCell(int column)
            {
                if (!altRow) return;

                ws.Cells[row, column].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[row, column].Style.Fill.BackgroundColor.SetColor(RowAlt);
            }

            ws.Cells[row, 1].Value = reportRow.Rank;
            ws.Cells[row, 2].Value = reportRow.OfferId;
            ws.Cells[row, 3].Value = reportRow.ProductId;
            ws.Cells[row, 4].Value = reportRow.Name;
            ws.Cells[row, 5].Value = reportRow.Brand;
            ws.Cells[row, 6].Value = reportRow.Category;
            ws.Cells[row, 7].Value = reportRow.Gtin;
            ws.Cells[row, 8].Value = reportRow.SellerName;
            ws.Cells[row, 9].Value = reportRow.SoldItems30Days;

            for (int fixedCol = 1; fixedCol <= 9; fixedCol++) StyleCell(fixedCol);

            ws.Cells[row, 10].Value = reportRow.TodayPrice;
            ws.Cells[row, 10].Style.Numberformat.Format = "#,##0.00";
            StyleCell(10);

            ws.Cells[row, 11].Value = FormatDateOrDash(reportRow.PreviousSnapshotDate);
            StyleCell(11);

            if (reportRow.PreviousPrice.HasValue)
            {
                ws.Cells[row, 12].Value = reportRow.PreviousPrice.Value;
                ws.Cells[row, 12].Style.Numberformat.Format = "#,##0.00";
            }
            StyleCell(12);

            SetDeltaAmountCell(ws, row, 13, reportRow.DailyChange, altRow);
            SetDeltaPctCell(ws, row, 14, reportRow.DailyChangePct, altRow);

            ws.Cells[row, 15].Value = FormatDateOrDash(reportRow.Price7DaysAgoDate);
            StyleCell(15);

            if (reportRow.Price7DaysAgo.HasValue)
            {
                ws.Cells[row, 16].Value = reportRow.Price7DaysAgo.Value;
                ws.Cells[row, 16].Style.Numberformat.Format = "#,##0.00";
            }
            StyleCell(16);

            SetDeltaPctCell(ws, row, 17, reportRow.Change7DayPct, altRow);

            ws.Cells[row, 18].Value = FormatDateOrDash(reportRow.Price30DaysAgoDate);
            StyleCell(18);

            if (reportRow.Price30DaysAgo.HasValue)
            {
                ws.Cells[row, 19].Value = reportRow.Price30DaysAgo.Value;
                ws.Cells[row, 19].Style.Numberformat.Format = "#,##0.00";
            }
            StyleCell(19);

            SetDeltaPctCell(ws, row, 20, reportRow.Change30DayPct, altRow);

            if (reportRow.MinPrice > 0)
            {
                ws.Cells[row, 21].Value = reportRow.MinPrice;
                ws.Cells[row, 21].Style.Numberformat.Format = "#,##0.00";
                ws.Cells[row, 21].Style.Font.Color.SetColor(GreenFg);
            }
            StyleCell(21);

            if (reportRow.MaxPrice > 0)
            {
                ws.Cells[row, 22].Value = reportRow.MaxPrice;
                ws.Cells[row, 22].Style.Numberformat.Format = "#,##0.00";
                ws.Cells[row, 22].Style.Font.Color.SetColor(RedFg);
            }
            StyleCell(22);

            ws.Cells[row, 23].Value = reportRow.SnapshotCount;
            StyleCell(23);

            var historyMap = reportRow.RecentHistory.ToDictionary(h => h.Date, h => h.Price);
            for (int d = 0; d < allDates.Count; d++)
            {
                int historyCol = firstDateCol + d;
                if (!historyMap.TryGetValue(allDates[d], out var historicalPrice))
                    continue;

                if (historicalPrice == 0)
                {
                    ws.Cells[row, historyCol].Value = "S/O";
                    ws.Cells[row, historyCol].Style.Font.Color.SetColor(Color.Gray);
                    StyleCell(historyCol);
                    continue;
                }

                ws.Cells[row, historyCol].Value = historicalPrice;
                ws.Cells[row, historyCol].Style.Numberformat.Format = "#,##0.00";
                StyleCell(historyCol);
            }
        }

        ws.Cells.AutoFitColumns(6, 35);
        for (int d = 0; d < allDates.Count; d++)
            ws.Column(firstDateCol + d).Width = 12;
    }

    private static string FormatDateOrDash(string dateKey) =>
        string.IsNullOrWhiteSpace(dateKey) ? "-" : dateKey;

    private sealed record GroupInsightRow(
        string Name,
        int ProductCount,
        int CoverageCount,
        int InStockCount,
        int UpCount,
        int DownCount,
        int FlatCount,
        int NewCount,
        int ChangedCount,
        decimal AvgPrice,
        decimal MinPrice,
        decimal MaxPrice,
        decimal AvgRank,
        decimal? MedianMoverPct,
        string TopIncrease,
        decimal? TopIncreasePct,
        string TopDecrease,
        decimal? TopDecreasePct);

    private static void WritePriceMoversSheet(ExcelPackage package,
        string sheetName, List<PriceIndexReportRow> movers, Color accentBg, string dateKey)
    {
        var ws = package.Workbook.Worksheets.Add(sheetName);
        string[] headers = ["Rank", "Product Name", "Brand", "Category", "GTIN", "Seller", "Sold items (30d)",
            $"Today ({dateKey})", "Prev Date", "Previous", $"Δ ({dateKey})", $"Δ% ({dateKey})",
            "7d Date", $"7d Δ% ({dateKey})", "30d Date", $"30d Δ% ({dateKey})", "All-Time Low", "All-Time High"];

        for (int c = 0; c < headers.Length; c++)
            ws.Cells[1, c + 1].Value = headers[c];
        StyleInsightHeader(ws, headers.Length, accentBg);

        for (int i = 0; i < movers.Count; i++)
        {
            var r = movers[i];
            int row = i + 2;
            ws.Cells[row, 1].Value = r.Rank;
            ws.Cells[row, 2].Value = r.Name;
            ws.Cells[row, 3].Value = r.Brand;
            ws.Cells[row, 4].Value = r.Category;
            ws.Cells[row, 5].Value = r.Gtin;
            ws.Cells[row, 6].Value = r.SellerName;
            ws.Cells[row, 7].Value = r.SoldItems30Days;
            ws.Cells[row, 8].Value = r.TodayPrice;
            ws.Cells[row, 8].Style.Numberformat.Format = "#,##0.00";
            ws.Cells[row, 9].Value = FormatDateOrDash(r.PreviousSnapshotDate);
            if (r.PreviousPrice.HasValue) { ws.Cells[row, 10].Value = r.PreviousPrice.Value; ws.Cells[row, 10].Style.Numberformat.Format = "#,##0.00"; }
            SetDeltaAmountCell(ws, row, 11, r.DailyChange, i % 2 == 1);
            SetDeltaPctCell(ws, row, 12, r.DailyChangePct, i % 2 == 1);
            ws.Cells[row, 13].Value = FormatDateOrDash(r.Price7DaysAgoDate);
            SetDeltaPctCell(ws, row, 14, r.Change7DayPct, i % 2 == 1);
            ws.Cells[row, 15].Value = FormatDateOrDash(r.Price30DaysAgoDate);
            SetDeltaPctCell(ws, row, 16, r.Change30DayPct, i % 2 == 1);
            if (r.MinPrice > 0) { ws.Cells[row, 17].Value = r.MinPrice; ws.Cells[row, 17].Style.Numberformat.Format = "#,##0.00"; }
            if (r.MaxPrice > 0) { ws.Cells[row, 18].Value = r.MaxPrice; ws.Cells[row, 18].Style.Numberformat.Format = "#,##0.00"; }
        }

        ws.Cells.AutoFitColumns(6, 40);
    }

    private static void WriteCategorySummarySheet(ExcelPackage package, List<PriceIndexReportRow> rows)
    {
        var ws = package.Workbook.Worksheets.Add("📁 By Category");
        string[] headers = [
            "Category", "Products", "Catalog Share", "Sellers", "In Stock %",
            "⬆ Up", "⬇ Down", "Changed %", "Median Mover Δ%", "Avg Price",
            "Lead Increase", "Lead Decrease"
        ];
        for (int c = 0; c < headers.Length; c++)
            ws.Cells[1, c + 1].Value = headers[c];
        StyleInsightHeader(ws, headers.Length, ColHeaderGreen);

        var groups = BuildGroupInsightRows(
            rows,
            r => r.Category,
            "(uncategorized)",
            group => group.Select(r => r.SellerName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            int row = i + 2;
            if (i % 2 == 1)
                ApplyAltRow(ws, row, 1, headers.Length);

            ws.Cells[row, 1].Value = g.Name;
            ws.Cells[row, 2].Value = g.ProductCount;
            SetRatioCell(ws, row, 3, g.ProductCount, rows.Count);
            ws.Cells[row, 4].Value = g.CoverageCount;
            SetRatioCell(ws, row, 5, g.InStockCount, g.ProductCount);
            ws.Cells[row, 6].Value = g.UpCount;
            ws.Cells[row, 7].Value = g.DownCount;
            SetRatioCell(ws, row, 8, g.ChangedCount, g.ProductCount);
            SetSignedPctSummaryCell(ws, row, 9, g.MedianMoverPct);
            SetPriceCell(ws, row, 10, g.AvgPrice);
            ws.Cells[row, 11].Value = FormatLeadSignal(g.TopIncrease, g.TopIncreasePct);
            ws.Cells[row, 12].Value = FormatLeadSignal(g.TopDecrease, g.TopDecreasePct);
        }

        ws.Cells.AutoFitColumns(8, 45);
    }

    private static void WriteSellerSummarySheet(ExcelPackage package, List<PriceIndexReportRow> rows)
    {
        var ws = package.Workbook.Worksheets.Add("🏪 By Seller");
        string[] headers = [
            "Seller", "Products", "Catalog Share", "Categories", "In Stock %",
            "⬆ Up", "⬇ Down", "Changed %", "Median Mover Δ%", "Avg Price",
            "Lead Increase", "Lead Decrease"
        ];
        for (int c = 0; c < headers.Length; c++)
            ws.Cells[1, c + 1].Value = headers[c];
        StyleInsightHeader(ws, headers.Length, Color.FromArgb(89, 89, 89));

        var groups = BuildGroupInsightRows(
            rows,
            r => r.SellerName,
            "(unknown)",
            group => group.Select(r => r.Category).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            int row = i + 2;
            if (i % 2 == 1)
                ApplyAltRow(ws, row, 1, headers.Length);

            ws.Cells[row, 1].Value = g.Name;
            ws.Cells[row, 2].Value = g.ProductCount;
            SetRatioCell(ws, row, 3, g.ProductCount, rows.Count);
            ws.Cells[row, 4].Value = g.CoverageCount;
            SetRatioCell(ws, row, 5, g.InStockCount, g.ProductCount);
            ws.Cells[row, 6].Value = g.UpCount;
            ws.Cells[row, 7].Value = g.DownCount;
            SetRatioCell(ws, row, 8, g.ChangedCount, g.ProductCount);
            SetSignedPctSummaryCell(ws, row, 9, g.MedianMoverPct);
            SetPriceCell(ws, row, 10, g.AvgPrice);
            ws.Cells[row, 11].Value = FormatLeadSignal(g.TopIncrease, g.TopIncreasePct);
            ws.Cells[row, 12].Value = FormatLeadSignal(g.TopDecrease, g.TopDecreasePct);
        }

        ws.Cells.AutoFitColumns(8, 45);
    }

    /// <summary>Executive Summary — top-down view of catalog coverage and movement concentration.</summary>
    private static void WriteExecutiveSummarySheet(ExcelPackage package, List<PriceIndexReportRow> rows, string dateKey)
    {
        var ws = package.Workbook.Worksheets.Add("📊 Summary");

        var inStock = rows.Where(r => !r.IsStockOut).ToList();
        var withHistory = rows.Where(r => r.DailyChangePct.HasValue).ToList();
        var movers = withHistory.Where(r => r.DailyChangePct.HasValue && r.DailyChangePct.Value != 0).ToList();
        int increases = rows.Count(r => r.DailyChangePct > 0);
        int decreases = rows.Count(r => r.DailyChangePct < 0);
        int flat = rows.Count(r => r.DailyChangePct == 0);
        int newProducts = rows.Count(r => r.DailyChangePct is null);
        int stockOuts = rows.Count - inStock.Count;
        int uniqueSellers = rows.Select(r => r.SellerName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int uniqueCategories = rows.Select(r => r.Category).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var categoryInsights = BuildGroupInsightRows(
            rows,
            r => r.Category,
            "(uncategorized)",
            group => group.Select(r => r.SellerName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var sellerInsights = BuildGroupInsightRows(
            rows,
            r => r.SellerName,
            "(unknown)",
            group => group.Select(r => r.Category).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        ws.Cells[1, 1].Value = "PRICE INDEX OVERVIEW";
        ws.Cells[1, 1].Style.Font.Bold = true;
        ws.Cells[1, 1].Style.Font.Size = 16;
        ws.Cells[2, 1].Value = $"Snapshot: {dateKey}";
        ws.Cells[2, 1].Style.Font.Italic = true;

        ws.Cells[4, 1].Value = "Catalog Footprint";
        ws.Cells[4, 1].Style.Font.Bold = true;
        ws.Cells[4, 1].Style.Font.Size = 12;

        string[] footprintHeaders = ["Metric", "Value"];
        for (int c = 0; c < footprintHeaders.Length; c++)
            ws.Cells[5, c + 1].Value = footprintHeaders[c];
        StyleHeaderRange(ws.Cells[5, 1, 5, footprintHeaders.Length], ColHeaderDark);

        int footprintRow = 6;
        void Metric(string label, object value, string format = "")
        {
            if ((footprintRow - 6) % 2 == 1)
                ApplyAltRow(ws, footprintRow, 1, 2);

            ws.Cells[footprintRow, 1].Value = label;
            ws.Cells[footprintRow, 1].Style.Font.Bold = true;
            ws.Cells[footprintRow, 2].Value = value;
            if (!string.IsNullOrEmpty(format))
                ws.Cells[footprintRow, 2].Style.Numberformat.Format = format;

            footprintRow++;
        }

        Metric("Total Products", rows.Count);
        Metric("Products In Stock", inStock.Count);
        Metric("Stock-Outs", stockOuts);
        Metric("Unique Sellers", uniqueSellers);
        Metric("Unique Categories", uniqueCategories);
        if (inStock.Count > 0)
        {
            Metric("Average Current Price", inStock.Average(r => r.TodayPrice), "#,##0.00");
            Metric("Median Current Price", MedianPrice(inStock), "#,##0.00");
        }

        ws.Cells[4, 4].Value = "Daily Movement Mix";
        ws.Cells[4, 4].Style.Font.Bold = true;
        ws.Cells[4, 4].Style.Font.Size = 12;

        string[] movementHeaders = ["Status", "Products", "Share of Catalog"];
        for (int c = 0; c < movementHeaders.Length; c++)
            ws.Cells[5, c + 4].Value = movementHeaders[c];
        StyleHeaderRange(ws.Cells[5, 4, 5, 6], ColHeaderGreen);

        (string Label, int Count)[] movementRows =
        [
            ("⬆ Up", increases),
            ("⬇ Down", decreases),
            ("➡ Flat", flat),
            ("🆕 New", newProducts),
            ("📦 In Stock", inStock.Count),
            ("🚫 Stock-Out", stockOuts)
        ];

        for (int i = 0; i < movementRows.Length; i++)
        {
            int row = 6 + i;
            if (i % 2 == 1)
                ApplyAltRow(ws, row, 4, 6);

            ws.Cells[row, 4].Value = movementRows[i].Label;
            ws.Cells[row, 5].Value = movementRows[i].Count;
            SetRatioCell(ws, row, 6, movementRows[i].Count, rows.Count);
        }

        int insightRow = Math.Max(footprintRow, 12) + 2;
        ws.Cells[insightRow, 1].Value = "Movement Quality";
        ws.Cells[insightRow, 1].Style.Font.Bold = true;
        ws.Cells[insightRow, 1].Style.Font.Size = 12;

        string[] qualityHeaders = ["Signal", "Value"];
        for (int c = 0; c < qualityHeaders.Length; c++)
            ws.Cells[insightRow + 1, c + 1].Value = qualityHeaders[c];
        StyleHeaderRange(ws.Cells[insightRow + 1, 1, insightRow + 1, 2], Color.FromArgb(89, 89, 89));

        int qualityRow = insightRow + 2;
        void Quality(string label, object value, string format = "")
        {
            if ((qualityRow - (insightRow + 2)) % 2 == 1)
                ApplyAltRow(ws, qualityRow, 1, 2);

            ws.Cells[qualityRow, 1].Value = label;
            ws.Cells[qualityRow, 1].Style.Font.Bold = true;
            ws.Cells[qualityRow, 2].Value = value;
            if (!string.IsNullOrEmpty(format))
                ws.Cells[qualityRow, 2].Style.Numberformat.Format = format;

            qualityRow++;
        }

        var medianMoverPct = Median(movers.Select(r => r.DailyChangePct!.Value));
        var median7DayPct = Median(rows.Where(r => r.Change7DayPct.HasValue).Select(r => r.Change7DayPct!.Value));
        var median30DayPct = Median(rows.Where(r => r.Change30DayPct.HasValue).Select(r => r.Change30DayPct!.Value));

        Quality("Products with prior history", withHistory.Count);
        Quality("Products with price movement", movers.Count);
        Quality("Mover share", rows.Count > 0 ? (decimal)movers.Count / rows.Count : 0, "0.0%");
        Quality("Median mover Δ%", medianMoverPct.HasValue ? medianMoverPct.Value / 100m : "-", medianMoverPct.HasValue ? "+0.0%;-0.0%;0.0%" : "");
        Quality("Median 7d Δ%", median7DayPct.HasValue ? median7DayPct.Value / 100m : "-", median7DayPct.HasValue ? "+0.0%;-0.0%;0.0%" : "");
        Quality("Median 30d Δ%", median30DayPct.HasValue ? median30DayPct.Value / 100m : "-", median30DayPct.HasValue ? "+0.0%;-0.0%;0.0%" : "");

        int overviewRow = qualityRow + 2;
        overviewRow = WriteGroupOverviewTable(ws, overviewRow, "Largest Categories", "Sellers", categoryInsights.Take(8).ToList(), rows.Count, ColHeaderGreen);
        overviewRow += 2;
        overviewRow = WriteGroupOverviewTable(ws, overviewRow, "Largest Sellers", "Categories", sellerInsights.Take(8).ToList(), rows.Count, Color.FromArgb(89, 89, 89));
        overviewRow += 2;

        var topUp = rows.Where(r => r.DailyChangePct > 0)
            .OrderByDescending(r => r.DailyChangePct).Take(15).ToList();
        if (topUp.Count > 0)
        {
            ws.Cells[overviewRow, 1].Value = "⬆ TOP PRICE INCREASES";
            ws.Cells[overviewRow, 1].Style.Font.Bold = true;
            ws.Cells[overviewRow, 1].Style.Font.Size = 12;
            overviewRow++;
            WriteCompactTable(ws, ref overviewRow, topUp, dateKey);
            overviewRow++;
        }

        var topDown = rows.Where(r => r.DailyChangePct < 0)
            .OrderBy(r => r.DailyChangePct).Take(15).ToList();
        if (topDown.Count > 0)
        {
            ws.Cells[overviewRow, 1].Value = "⬇ TOP PRICE DECREASES";
            ws.Cells[overviewRow, 1].Style.Font.Bold = true;
            ws.Cells[overviewRow, 1].Style.Font.Size = 12;
            overviewRow++;
            WriteCompactTable(ws, ref overviewRow, topDown, dateKey);
        }

        ws.Cells.AutoFitColumns(8, 42);
        ws.Column(1).Width = 28;
        ws.Column(2).Width = 16;

        package.Workbook.Worksheets.MoveToStart("📊 Summary");
    }

    private static void StyleInsightHeader(ExcelWorksheet ws, int cols, Color bg)
    {
        StyleHeaderRange(ws.Cells[1, 1, 1, cols], bg);
    }

    private static void StyleHeaderRange(ExcelRange range, Color bg)
    {
        range.Style.Font.Bold = true;
        range.Style.Font.Color.SetColor(Color.White);
        range.Style.Fill.PatternType = ExcelFillStyle.Solid;
        range.Style.Fill.BackgroundColor.SetColor(bg);
        range.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
    }

    private static List<GroupInsightRow> BuildGroupInsightRows(
        List<PriceIndexReportRow> rows,
        Func<PriceIndexReportRow, string> groupSelector,
        string fallbackName,
        Func<IEnumerable<PriceIndexReportRow>, int> coverageCounter)
    {
        return rows
            .GroupBy(r => NormalizeGroupName(groupSelector(r), fallbackName), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var items = g.ToList();
                var inStockPrices = items.Where(r => !r.IsStockOut).Select(r => r.TodayPrice).ToList();
                var movers = items.Where(r => r.DailyChangePct.HasValue && r.DailyChangePct.Value != 0).ToList();
                var topIncrease = movers.Where(r => r.DailyChangePct > 0).MaxBy(r => r.DailyChangePct);
                var topDecrease = movers.Where(r => r.DailyChangePct < 0).MinBy(r => r.DailyChangePct);

                return new GroupInsightRow(
                    Name: g.Key,
                    ProductCount: items.Count,
                    CoverageCount: coverageCounter(items),
                    InStockCount: items.Count(r => !r.IsStockOut),
                    UpCount: items.Count(r => r.DailyChangePct > 0),
                    DownCount: items.Count(r => r.DailyChangePct < 0),
                    FlatCount: items.Count(r => r.DailyChangePct == 0),
                    NewCount: items.Count(r => r.DailyChangePct is null),
                    ChangedCount: items.Count(r => r.DailyChangePct.HasValue && r.DailyChangePct.Value != 0),
                    AvgPrice: inStockPrices.Count > 0 ? inStockPrices.Average() : 0,
                    MinPrice: inStockPrices.Count > 0 ? inStockPrices.Min() : 0,
                    MaxPrice: inStockPrices.Count > 0 ? inStockPrices.Max() : 0,
                    AvgRank: Math.Round((decimal)items.Average(r => r.Rank), 1),
                    MedianMoverPct: Median(movers.Select(r => r.DailyChangePct!.Value)),
                    TopIncrease: topIncrease?.Name ?? string.Empty,
                    TopIncreasePct: topIncrease?.DailyChangePct,
                    TopDecrease: topDecrease?.Name ?? string.Empty,
                    TopDecreasePct: topDecrease?.DailyChangePct);
            })
            .OrderByDescending(g => g.ProductCount)
            .ThenByDescending(g => g.ChangedCount)
            .ThenBy(g => g.AvgRank)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int WriteGroupOverviewTable(
        ExcelWorksheet ws,
        int startRow,
        string title,
        string coverageLabel,
        List<GroupInsightRow> groups,
        int totalProducts,
        Color headerColor)
    {
        ws.Cells[startRow, 1].Value = title;
        ws.Cells[startRow, 1].Style.Font.Bold = true;
        ws.Cells[startRow, 1].Style.Font.Size = 12;

        string[] headers = ["Name", "Products", "Share", coverageLabel, "In Stock %", "Changed %", "Median Mover Δ%", "Signal"];
        for (int c = 0; c < headers.Length; c++)
            ws.Cells[startRow + 1, c + 1].Value = headers[c];
        StyleHeaderRange(ws.Cells[startRow + 1, 1, startRow + 1, headers.Length], headerColor);

        int row = startRow + 2;
        for (int i = 0; i < groups.Count; i++, row++)
        {
            var group = groups[i];
            if (i % 2 == 1)
                ApplyAltRow(ws, row, 1, headers.Length);

            ws.Cells[row, 1].Value = group.Name;
            ws.Cells[row, 2].Value = group.ProductCount;
            SetRatioCell(ws, row, 3, group.ProductCount, totalProducts);
            ws.Cells[row, 4].Value = group.CoverageCount;
            SetRatioCell(ws, row, 5, group.InStockCount, group.ProductCount);
            SetRatioCell(ws, row, 6, group.ChangedCount, group.ProductCount);
            SetSignedPctSummaryCell(ws, row, 7, group.MedianMoverPct);
            ws.Cells[row, 8].Value = DescribeMovement(group);
        }

        return row;
    }

    private static string NormalizeGroupName(string value, string fallbackName) =>
        string.IsNullOrWhiteSpace(value) ? fallbackName : value.Trim();

    private static string DescribeMovement(GroupInsightRow group)
    {
        if (group.ChangedCount == 0)
            return group.NewCount > 0 ? $"{group.NewCount} new / {group.FlatCount} flat" : $"{group.FlatCount} flat";

        return group.UpCount >= group.DownCount
            ? $"{group.UpCount} up / {group.DownCount} down"
            : $"{group.DownCount} down / {group.UpCount} up";
    }

    private static string FormatLeadSignal(string name, decimal? pct)
    {
        if (string.IsNullOrWhiteSpace(name) || !pct.HasValue)
            return "-";

        return $"{name} ({pct.Value:+0.0;-0.0;0.0}%)";
    }

    private static void WriteCompactTable(ExcelWorksheet ws, ref int row, List<PriceIndexReportRow> items, string dateKey)
    {
        string[] headers = ["Rank", "Product Name", "Brand", "Category", "Seller", $"Today ({dateKey})", "Prev Date", "Previous", $"Δ% ({dateKey})"];
        for (int c = 0; c < headers.Length; c++)
        {
            ws.Cells[row, c + 1].Value = headers[c];
            ws.Cells[row, c + 1].Style.Font.Bold = true;
            ws.Cells[row, c + 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
            ws.Cells[row, c + 1].Style.Fill.BackgroundColor.SetColor(ColHeaderDark);
            ws.Cells[row, c + 1].Style.Font.Color.SetColor(Color.White);
        }

        row++;

        foreach (var reportRow in items)
        {
            ws.Cells[row, 1].Value = reportRow.Rank;
            ws.Cells[row, 2].Value = reportRow.Name;
            ws.Cells[row, 3].Value = reportRow.Brand;
            ws.Cells[row, 4].Value = reportRow.Category;
            ws.Cells[row, 5].Value = reportRow.SellerName;
            ws.Cells[row, 6].Value = reportRow.TodayPrice;
            ws.Cells[row, 6].Style.Numberformat.Format = "#,##0.00";
            ws.Cells[row, 7].Value = FormatDateOrDash(reportRow.PreviousSnapshotDate);

            if (reportRow.PreviousPrice.HasValue)
            {
                ws.Cells[row, 8].Value = reportRow.PreviousPrice.Value;
                ws.Cells[row, 8].Style.Numberformat.Format = "#,##0.00";
            }

            if (reportRow.DailyChangePct.HasValue)
            {
                ws.Cells[row, 9].Value = reportRow.DailyChangePct.Value / 100m;
                ws.Cells[row, 9].Style.Numberformat.Format = "+0.00%;-0.00%";
                ApplyDeltaColor(ws.Cells[row, 9], reportRow.DailyChangePct.Value);
            }

            row++;
        }
    }

    private static void ApplyAltRow(ExcelWorksheet ws, int row, int startCol, int endCol)
    {
        using var range = ws.Cells[row, startCol, row, endCol];
        range.Style.Fill.PatternType = ExcelFillStyle.Solid;
        range.Style.Fill.BackgroundColor.SetColor(RowAlt);
    }

    private static void SetRatioCell(ExcelWorksheet ws, int row, int col, int numerator, int denominator)
    {
        if (denominator <= 0) return;

        ws.Cells[row, col].Value = (decimal)numerator / denominator;
        ws.Cells[row, col].Style.Numberformat.Format = "0.0%";
    }

    private static void SetPriceCell(ExcelWorksheet ws, int row, int col, decimal value)
    {
        if (value <= 0)
        {
            ws.Cells[row, col].Value = "-";
            return;
        }

        ws.Cells[row, col].Value = value;
        ws.Cells[row, col].Style.Numberformat.Format = "#,##0.00";
    }

    private static void SetSignedPctSummaryCell(ExcelWorksheet ws, int row, int col, decimal? value)
    {
        if (!value.HasValue)
        {
            ws.Cells[row, col].Value = "-";
            return;
        }

        ws.Cells[row, col].Value = value.Value / 100m;
        ws.Cells[row, col].Style.Numberformat.Format = "+0.0%;-0.0%;0.0%";
        ApplyDeltaColor(ws.Cells[row, col], value.Value);
    }

    private static decimal? Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;

        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2
            : sorted[mid];
    }

    private static decimal MedianPrice(List<PriceIndexReportRow> inStock)
    {
        return Median(inStock.Select(r => r.TodayPrice)) ?? 0;
    }

    private static void SetDeltaAmountCell(ExcelWorksheet ws, int row, int col, decimal? value, bool altRow)
    {
        if (!value.HasValue)
        {
            if (altRow)
            {
                ws.Cells[row, col].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[row, col].Style.Fill.BackgroundColor.SetColor(RowAlt);
            }

            return;
        }

        ws.Cells[row, col].Value = value.Value;
        ws.Cells[row, col].Style.Numberformat.Format = "+#,##0.00;-#,##0.00;\"-\"";
        ApplyDeltaColor(ws.Cells[row, col], value.Value);
    }

    private static void SetDeltaPctCell(ExcelWorksheet ws, int row, int col, decimal? value, bool altRow)
    {
        if (!value.HasValue)
        {
            if (altRow)
            {
                ws.Cells[row, col].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[row, col].Style.Fill.BackgroundColor.SetColor(RowAlt);
            }

            return;
        }

        ws.Cells[row, col].Value = value.Value / 100m;
        ws.Cells[row, col].Style.Numberformat.Format = "+0.00%;-0.00%;\"-\"";
        ApplyDeltaColor(ws.Cells[row, col], value.Value);
    }

    private static void ApplyDeltaColor(ExcelRange cell, decimal value)
    {
        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
        if (value > 0)
        {
            cell.Style.Fill.BackgroundColor.SetColor(RedBg);
            cell.Style.Font.Color.SetColor(RedFg);
            cell.Style.Font.Bold = true;
        }
        else if (value < 0)
        {
            cell.Style.Fill.BackgroundColor.SetColor(GreenBg);
            cell.Style.Font.Color.SetColor(GreenFg);
        }
        else
        {
            cell.Style.Fill.BackgroundColor.SetColor(RowAlt);
        }
    }

    private static async Task SendComplete(Func<int, string, string, Task> onProgress, string? fileName, int productCount)
    {
        var data = new
        {
            complete = true,
            downloadUrl = fileName != null ? $"/api/download/{fileName}" : null,
            fileName,
            productCount
        };

        await onProgress(100, JsonSerializer.Serialize(data), "complete");
    }
}
