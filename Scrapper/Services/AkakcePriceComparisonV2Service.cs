using OfficeOpenXml;
using Scrapper.Models;
using System.Collections.Concurrent;
using System.Globalization;

namespace Scrapper.Services;

/// <summary>
/// Price comparison V2: Phase 1 uses Selenium to search Akakce and collect product URLs,
/// Phase 2 uses Scrape.do to fetch each product page (no Cloudflare, much faster).
/// </summary>
public class AkakcePriceComparisonV2Service
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();

    private readonly AkakceScrapeDoService _scrapeDoService;

    private const int MAX_CANDIDATES_TO_TRY = 3;
    private const decimal PRICE_RATIO_THRESHOLD = 5.0m;
    private const int SCRAPEDO_DELAY_MS = 500;

    public AkakcePriceComparisonV2Service(AkakceScrapeDoService scrapeDoService)
    {
        _scrapeDoService = scrapeDoService;
    }

    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
            cts.Cancel();
    }

    public async Task CompareFromExcelAsync(
        Stream excelStream,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;

        var rows = new List<PriceComparisonRow>();

        try
        {
            await onProgress(1, "?? Reading Excel file...", "info");

            var readResult = ReadInputExcel(excelStream);
            var inputRows = readResult.Rows;
            int duplicatesSkipped = readResult.DuplicatesSkipped;

            if (inputRows.Count == 0)
            {
                await onProgress(100, "?? No products found in the Excel file", "warning");
                await SendComplete(onProgress, null, 0);
                return;
            }

            var dupMsg = duplicatesSkipped > 0 ? $" ({duplicatesSkipped} duplicate name(s) skipped)" : "";
            await onProgress(3, $"? Found {inputRows.Count} unique products{dupMsg}", "success");

            // ??? PHASE 1: Search Akakce via Selenium to collect product URLs ???
            await onProgress(4, "?? Phase 1: Searching Akakce for product URLs via Edge...", "info");

            using var scraper = new AkakceScraper();
            var warmupSuccess = await scraper.WarmupAsync(onProgress);

            if (!warmupSuccess)
            {
                await onProgress(100, "? Could not connect to Edge browser.", "error");
                await SendComplete(onProgress, null, 0);
                return;
            }

            var phase1Progress = 10.0;
            var phase1ProgressPerProduct = 40.0 / inputRows.Count;
            var urlMap = new Dictionary<int, (string Url, string Title)>();
            int searchSuccess = 0;
            int searchFailed = 0;

            for (int i = 0; i < inputRows.Count; i++)
            {
                if (cts.Token.IsCancellationRequested) break;

                var row = inputRows[i];
                var searchName = row.SearchName;
                var myPrice = row.MyPrice;
                var isStockOut = row.IsStockOut;

                await onProgress((int)phase1Progress,
                    $"?? [{i + 1}/{inputRows.Count}] Searching: {Truncate(searchName, 50)}...", "info");

                try
                {
                    var candidates = await scraper.SearchProductCandidatesAsync(searchName, MAX_CANDIDATES_TO_TRY);

                    if (candidates.Count > 0)
                    {
                        // Use listing price to pick the best candidate
                        string? selectedUrl = null;
                        string? selectedTitle = null;

                        if (!isStockOut && myPrice > 0)
                        {
                            foreach (var (ct, cu, lp) in candidates)
                            {
                                if (lp > 0 && IsListingPriceInRange(myPrice, lp))
                                {
                                    selectedUrl = cu;
                                    selectedTitle = ct;
                                    break;
                                }
                            }
                        }

                        selectedUrl ??= candidates[0].Url;
                        selectedTitle ??= candidates[0].Title;

                        urlMap[i] = (selectedUrl, selectedTitle);
                        searchSuccess++;
                        await onProgress((int)phase1Progress,
                            $"? Found: {Truncate(selectedTitle, 50)}", "success");
                    }
                    else
                    {
                        searchFailed++;
                        await onProgress((int)phase1Progress,
                            $"?? No results: {Truncate(searchName, 40)}", "warning");
                    }
                }
                catch (Exception ex)
                {
                    searchFailed++;
                    Console.WriteLine($"[PriceCompV2] Search failed for '{searchName}': {ex.Message}");
                }

                phase1Progress += phase1ProgressPerProduct;
            }

            await onProgress(50,
                $"?? Phase 1 complete: {searchSuccess} URLs found, {searchFailed} failed", "info");

            if (urlMap.Count == 0)
            {
                await onProgress(100, "? No product URLs found in Phase 1", "error");
                await SendComplete(onProgress, null, 0);
                return;
            }

            // ??? PHASE 2: Fetch product pages via Scrape.do and parse sellers ???
            await onProgress(51, $"? Phase 2: Fetching {urlMap.Count} products via Scrape.do (no Cloudflare)...", "info");

            var phase2Progress = 52.0;
            var phase2ProgressPerProduct = 42.0 / urlMap.Count;
            int fetchSuccess = 0;
            int fetchFailed = 0;

            for (int i = 0; i < inputRows.Count; i++)
            {
                if (cts.Token.IsCancellationRequested) break;

                var inputRow = inputRows[i];

                if (!urlMap.TryGetValue(i, out var urlInfo))
                {
                    // No URL found in Phase 1
                    inputRow.ErrorMessage = "No search results found";
                    rows.Add(inputRow);
                    continue;
                }

                await onProgress((int)phase2Progress,
                    $"? [{fetchSuccess + fetchFailed + 1}/{urlMap.Count}] {Truncate(urlInfo.Title, 50)}...", "info");

                try
                {
                    var product = await _scrapeDoService.ScrapeProductAsync(urlInfo.Url);

                    inputRow.AkakceName = product.Name;
                    inputRow.AkakceUrl = product.ProductUrl;
                    inputRow.ErrorMessage = product.ErrorMessage;

                    if (product.IsSuccess)
                    {
                        CollectMarketplacePrices(product, inputRow);
                        fetchSuccess++;

                        await onProgress((int)phase2Progress,
                            $"? {Truncate(product.Name, 40)}: {product.SellerCount} sellers", "success");
                    }
                    else
                    {
                        fetchFailed++;
                        await onProgress((int)phase2Progress,
                            $"?? {Truncate(urlInfo.Title, 40)}: {product.ErrorMessage}", "warning");
                    }
                }
                catch (Exception ex)
                {
                    fetchFailed++;
                    inputRow.ErrorMessage = ex.Message;
                    Console.WriteLine($"[PriceCompV2] Scrape.do failed for '{urlInfo.Url}': {ex.Message}");
                }

                rows.Add(inputRow);
                phase2Progress += phase2ProgressPerProduct;

                // Small delay to avoid Scrape.do rate limits
                await Task.Delay(SCRAPEDO_DELAY_MS);
            }

            // Add rows that weren't processed (cancellation)
            for (int i = 0; i < inputRows.Count; i++)
            {
                if (!rows.Contains(inputRows[i]))
                {
                    inputRows[i].ErrorMessage = "Cancelled";
                    rows.Add(inputRows[i]);
                }
            }

            if (rows.Count > 0)
            {
                await onProgress(95, "?? Creating comparison Excel report...", "info");

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"AkakcePriceComparison_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                var exporter = new AkakcePriceComparisonExcelExporter();
                exporter.Export(rows, filePath);

                var done = rows.Count(r => r.IsSuccess);
                var failed = rows.Count - done;
                await onProgress(100,
                    $"? Done! {done} compared, {failed} failed (Phase1: {searchSuccess} found, Phase2: {fetchSuccess} scraped)",
                    "success");
                await SendComplete(onProgress, fileName, done);
            }
            else
            {
                await onProgress(100, "No products processed", "warning");
                await SendComplete(onProgress, null, 0);
            }
        }
        catch (Exception ex)
        {
            await onProgress(100, $"? Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, 0);
        }
        finally
        {
            if (!string.IsNullOrEmpty(sessionId))
                _sessions.TryRemove(sessionId, out _);
            cts.Dispose();
        }
    }

    // ??? Helpers (same logic as V1) ???

    private static void CollectMarketplacePrices(AkakceProductInfo product, PriceComparisonRow row)
    {
        IEnumerable<AkakceSellerInfo> allSellers = product.HasVariants
            ? product.Variants.SelectMany(v => v.Sellers)
            : product.Sellers;

        foreach (var seller in allSellers.Where(s => s.InStock && s.Price > 0))
        {
            var mp = NormalizeMarketplace(seller.Marketplace);
            if (!row.MarketplaceBestPrices.TryGetValue(mp, out var existing) || seller.Price < existing)
                row.MarketplaceBestPrices[mp] = seller.Price;
        }
    }

    private static string NormalizeMarketplace(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Diðer";

        return raw.Trim().ToLowerInvariant() switch
        {
            "hepsiburada" => "Hepsiburada",
            "idefix" or "Ýdefix" or "idefix.com" => "Ýdefix",
            "mediamarkt" or "media markt" or "media_markt" => "Media Markt",
            "mediamarkt pazar yeri" or "media markt pazar yeri" or "media_markt pazar yeri" => "Media Markt Pazar Yeri",
            "n11" or "n11.com" => "n11",
            "pazarama" => "Pazarama",
            "pttavm" or "ptt avm" => "Pttavm",
            "teknosa" => "Teknosa",
            "trendyol" => "Trendyol",
            "amazon" or "amazon.com.tr" or "amazon türkiye" => "Amazon Türkiye",
            "turkcell" => "Turkcell",
            "gittigidiyor" => "GittiGidiyor",
            "ciceksepeti" or "çiçeksepeti" => "ÇiçekSepeti",
            "koçtaþ" or "koctas" or "koctas.com.tr" or "koçtaþ.com.tr" => "Koçtaþ",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Trim().ToLower())
        };
    }

    private static bool IsListingPriceInRange(decimal myPrice, decimal listingPrice)
    {
        if (myPrice <= 0 || listingPrice <= 0) return true;
        var ratio = listingPrice / myPrice;
        return ratio >= (1m / PRICE_RATIO_THRESHOLD) && ratio <= PRICE_RATIO_THRESHOLD;
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
        await onProgress(100, System.Text.Json.JsonSerializer.Serialize(data), "complete");
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;

    // ??? Excel reading (same as V1) ???

    private static (List<PriceComparisonRow> Rows, int DuplicatesSkipped) ReadInputExcel(Stream stream)
    {
        var result = new List<PriceComparisonRow>();
        int duplicatesSkipped = 0;

        try
        {
            using var package = new ExcelPackage(stream);
            var ws = package.Workbook.Worksheets.FirstOrDefault();
            if (ws == null) return (result, 0);

            var rowCount = ws.Dimension?.Rows ?? 0;
            var columnMap = GetColumnMap(ws);
            var selectedRows = new Dictionary<string, PriceComparisonRow>(StringComparer.OrdinalIgnoreCase);

            for (int r = 2; r <= rowCount; r++)
            {
                var productName = GetCell(ws, r, columnMap.ProductName);
                if (string.IsNullOrWhiteSpace(productName)) continue;

                var offerTotalPriceRaw = GetCell(ws, r, columnMap.OfferTotalPrice);
                var (price, isStockOut) = ParsePrice(offerTotalPriceRaw);

                var row = new PriceComparisonRow
                {
                    OfferId = GetCell(ws, r, columnMap.OfferId),
                    FocusCategory = GetCell(ws, r, columnMap.FocusCategory),
                    CategoryLabel = GetCell(ws, r, columnMap.CategoryLabel),
                    Gtin = GetCell(ws, r, columnMap.Gtin),
                    SourceProductId = GetCell(ws, r, columnMap.ProductId),
                    SourceProductBrand = GetCell(ws, r, columnMap.ProductBrand),
                    SearchName = productName,
                    TotalActiveOffers = GetCell(ws, r, columnMap.TotalActiveOffers),
                    SourceStock = GetCell(ws, r, columnMap.Stock),
                    WinnerAssortmentType = GetCell(ws, r, columnMap.WinnerAssortmentType),
                    MyPrice = price,
                    IsStockOut = isStockOut,
                    OfferScoreRank = GetCell(ws, r, columnMap.OfferScoreRank),
                    SourceSellerName = GetCell(ws, r, columnMap.SellerName),
                    ProductSoldItems30d = GetCell(ws, r, columnMap.ProductSoldItems30d),
                    ProductGmvInclShipping30d = GetCell(ws, r, columnMap.ProductGmvInclShipping30d),
                    SessionsByProductWithPdp30d = GetCell(ws, r, columnMap.SessionsByProductWithPdp30d),
                    SessionsByProductWithAddToCartInPdp30d = GetCell(ws, r, columnMap.SessionsByProductWithAddToCartInPdp30d)
                };

                var dedupeKey = !string.IsNullOrWhiteSpace(row.Gtin)
                    ? $"gtin:{row.Gtin}"
                    : $"name:{row.SearchName}";

                if (selectedRows.TryGetValue(dedupeKey, out var existing))
                {
                    duplicatesSkipped++;
                    if (ShouldReplace(existing, row))
                        selectedRows[dedupeKey] = row;
                    continue;
                }

                selectedRows[dedupeKey] = row;
            }

            result = selectedRows.Values
                .OrderBy(r => r.SearchName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PriceCompV2] Excel read error: {ex.Message}");
        }

        return (result, duplicatesSkipped);
    }

    private static bool ShouldReplace(PriceComparisonRow current, PriceComparisonRow candidate)
    {
        if (current.IsStockOut && !candidate.IsStockOut) return true;
        if (!current.IsStockOut && candidate.IsStockOut) return false;
        if (current.MyPrice <= 0) return candidate.MyPrice > 0;
        if (candidate.MyPrice <= 0) return false;
        return candidate.MyPrice < current.MyPrice;
    }

    private static string GetCell(ExcelWorksheet ws, int row, int col) =>
        col <= 0 ? string.Empty : ws.Cells[row, col].Value?.ToString()?.Trim() ?? string.Empty;

    private static (decimal Price, bool IsStockOut) ParsePrice(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (0, true);
        var trimmed = raw.Trim();
        if (trimmed.Equals("stock out", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("stok yok", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("-", StringComparison.Ordinal))
            return (0, true);

        var cleaned = trimmed
            .Replace("TL", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("?", string.Empty)
            .Replace(" ", string.Empty)
            .Trim();

        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var p1) && p1 > 0)
            return (Math.Truncate(p1), false);
        if (decimal.TryParse(cleaned, NumberStyles.Any, new CultureInfo("tr-TR"), out var p2) && p2 > 0)
            return (Math.Truncate(p2), false);

        return (0, true);
    }

    private static ColumnMap GetColumnMap(ExcelWorksheet ws)
    {
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var colCount = ws.Dimension?.Columns ?? 0;
        for (int c = 1; c <= colCount; c++)
        {
            var h = ws.Cells[1, c].Value?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(h) && !headers.ContainsKey(h))
                headers[h] = c;
        }

        return new ColumnMap(
            Col(headers, "Offer id"),
            Col(headers, "Focus Category"),
            Col(headers, "Category Label"),
            Col(headers, "gtin"),
            Col(headers, "Product id"),
            Col(headers, "Product Brand"),
            Col(headers, "Product Name"),
            Col(headers, "Total Active Offers"),
            Col(headers, "Stock"),
            Col(headers, "Winner Assortment Type"),
            Col(headers, "Offer Total Price"),
            Col(headers, "Offer Score Rank"),
            Col(headers, "Seller Name"),
            Col(headers, "Product - Sold items (30d)"),
            Col(headers, "Product - GMV incl. Shipping (30d)"),
            Col(headers, "Sessions by Product with PDP (30d)"),
            Col(headers, "Sessions by Product with Add to Cart in pdp (30d)"));
    }

    private static int Col(Dictionary<string, int> h, string name) =>
        h.TryGetValue(name, out var c) ? c : throw new InvalidOperationException($"Required column '{name}' not found.");

    private readonly record struct ColumnMap(
        int OfferId, int FocusCategory, int CategoryLabel, int Gtin,
        int ProductId, int ProductBrand, int ProductName,
        int TotalActiveOffers, int Stock, int WinnerAssortmentType,
        int OfferTotalPrice, int OfferScoreRank, int SellerName,
        int ProductSoldItems30d, int ProductGmvInclShipping30d,
        int SessionsByProductWithPdp30d, int SessionsByProductWithAddToCartInPdp30d);
}
