using OfficeOpenXml;
using Scrapper.Models;
using System.Collections.Concurrent;
using System.Globalization;

namespace Scrapper.Services;

public class AkakcePriceComparisonService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private const int MAX_RETRIES = 1;
    private const int MAX_CONSECUTIVE_FAILURES = 3;
    private const int MAX_CANDIDATES_TO_TRY = 3;

    /// <summary>
    /// Maximum price ratio allowed between my price and the market price.
    /// If market price is more than this factor away from my price, the product is likely wrong.
    /// Example: 5.0 means market price must be between myPrice/5 and myPrice*5.
    /// </summary>
    private const decimal PRICE_RATIO_THRESHOLD = 5.0m;

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
            await onProgress(5, $"? Found {inputRows.Count} unique products to compare{dupMsg}", "success");

            using var scraper = new AkakceScraper();

            await onProgress(6, "?? Connecting to your Edge browser...", "info");
            var warmupSuccess = await scraper.WarmupAsync(onProgress);

            if (!warmupSuccess)
            {
                await onProgress(100, "? Could not connect to Edge. See console for setup instructions.", "error");
                await SendComplete(onProgress, null, 0);
                return;
            }

            var progressPerProduct = 82.0 / inputRows.Count;
            var currentProgress = 12.0;
            int successCount = 0;
            int failedCount = 0;
            int retryCount = 0;
            int consecutiveFailures = 0;

            for (int i = 0; i < inputRows.Count; i++)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, "? Comparison stopped by user", "warning");
                    break;
                }

                var inputRow = inputRows[i];
                var searchName = inputRow.SearchName;
                var myPrice = inputRow.MyPrice;
                var isStockOut = inputRow.IsStockOut;
                var priceDisplay = isStockOut ? "stock out" : myPrice.ToString("N0");
                await onProgress((int)currentProgress,
                    $"?? [{i + 1}/{inputRows.Count}] {Truncate(searchName, 50)} | My price: {priceDisplay} (?{successCount} ?{failedCount})",
                    "info");

                AkakceProductInfo? product = null;
                bool searchSuccess = false;

                for (int attempt = 1; attempt <= MAX_RETRIES && !searchSuccess; attempt++)
                {
                    try
                    {
                        if (attempt > 1)
                        {
                            retryCount++;
                            await onProgress((int)currentProgress,
                                $"?? Retry {attempt}/{MAX_RETRIES} for: {Truncate(searchName, 40)}...", "warning");
                            await Task.Delay(3000 * attempt);
                        }

                        var candidates = await scraper.SearchProductCandidatesAsync(searchName, MAX_CANDIDATES_TO_TRY);

                        if (candidates.Count == 0)
                        {
                            if (attempt == MAX_RETRIES)
                            {
                                await onProgress((int)currentProgress,
                                    $"?? No results after {MAX_RETRIES} tries: {Truncate(searchName, 40)}", "warning");
                                product = new AkakceProductInfo
                                {
                                    Name = searchName,
                                    ErrorMessage = $"No search results found after {MAX_RETRIES} attempts"
                                };
                                failedCount++;
                            }
                            continue;
                        }

                        // Fast pre-filter: use listing prices from the search page to pick the
                        // best candidate BEFORE doing an expensive full scrape.
                        string? selectedUrl = null;
                        string? selectedTitle = null;

                        if (!isStockOut && myPrice > 0)
                        {
                            // Try to find a candidate whose listing price is in range
                            foreach (var (ct, cu, lp) in candidates)
                            {
                                if (lp > 0 && IsListingPriceInRange(myPrice, lp))
                                {
                                    selectedUrl = cu;
                                    selectedTitle = ct;
                                    break;
                                }
                            }

                            // Log skipped candidates
                            if (selectedUrl == null)
                            {
                                foreach (var (ct, cu, lp) in candidates)
                                {
                                    if (lp > 0)
                                    {
                                        var ratio = lp / myPrice;
                                        await onProgress((int)currentProgress,
                                            $"?? '{Truncate(ct, 30)}' listing {lp:N0} TL vs yours {myPrice:N0} TL ({ratio:F1}x) — price mismatch",
                                            "warning");
                                    }
                                }
                            }
                        }

                        // Fallback: no price to compare, or no listing prices available ? use first candidate
                        selectedUrl ??= candidates[0].Url;
                        selectedTitle ??= candidates[0].Title;

                        await onProgress((int)currentProgress,
                            $"?? Scraping: {Truncate(selectedTitle, 50)}...", "info");

                        var scraped = await scraper.ScrapeProductAsync(selectedUrl, false);

                        if (scraped.IsSuccess)
                        {
                            product = scraped;
                            searchSuccess = true;
                            successCount++;

                            var sellerCount = product.HasVariants
                                ? product.Variants.Sum(v => v.SellerCount)
                                : product.SellerCount;
                            await onProgress((int)currentProgress,
                                $"? {Truncate(product.Name, 40)}: {sellerCount} sellers", "success");
                        }
                        else if (attempt == MAX_RETRIES)
                        {
                            product = scraped;
                            failedCount++;
                            await onProgress((int)currentProgress,
                                $"? Scrape failed: {scraped.ErrorMessage}", "error");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PriceCompare] Attempt {attempt} failed for '{searchName}': {ex.Message}");
                        if (attempt == MAX_RETRIES)
                        {
                            await onProgress((int)currentProgress,
                                $"? Failed after {MAX_RETRIES} tries '{Truncate(searchName, 30)}': {ex.Message}", "error");
                            product = new AkakceProductInfo
                            {
                                Name = searchName,
                                ErrorMessage = $"Failed after {MAX_RETRIES} attempts: {ex.Message}"
                            };
                            failedCount++;
                        }
                    }
                }

                var row = inputRow;

                if (product != null)
                {
                    row.AkakceName = product.Name;
                    row.AkakceUrl = product.ProductUrl;
                    row.ErrorMessage = product.ErrorMessage;

                    if (product.IsSuccess)
                        CollectMarketplacePrices(product, row);
                }

                rows.Add(row);

                if (searchSuccess)
                    consecutiveFailures = 0;
                else
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= MAX_CONSECUTIVE_FAILURES && !cts.Token.IsCancellationRequested)
                    {
                        await onProgress((int)currentProgress,
                            $"?? {consecutiveFailures} consecutive failures — pausing 20s and reconnecting browser...",
                            "warning");
                        await Task.Delay(20000);
                        var rewarm = await scraper.WarmupAsync(onProgress);
                        await onProgress((int)currentProgress,
                            rewarm ? "? Browser reconnected, resuming..." : "?? Reconnect failed, continuing anyway...",
                            rewarm ? "success" : "warning");
                        if (rewarm) consecutiveFailures = 0;
                    }
                }

                currentProgress += progressPerProduct;
                await Task.Delay(500);
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
                await onProgress(100, $"? Done! {done} compared, {failed} failed ({retryCount} retries)", "success");
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

    /// <summary>
    /// Collect the best (lowest in-stock) price per marketplace from a scraped product.
    /// </summary>
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

    /// <summary>
    /// Normalize raw marketplace name to a display-friendly, consistent key.
    /// </summary>
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
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Trim().ToLower())
        };
    }

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
            var columnMap = GetPriceComparisonColumnMap(ws);
            var selectedRows = new Dictionary<string, PriceComparisonRow>(StringComparer.OrdinalIgnoreCase);

            for (int r = 2; r <= rowCount; r++)
            {
                var productName = GetCellValue(ws, r, columnMap.ProductName);
                if (string.IsNullOrWhiteSpace(productName))
                {
                    continue;
                }

                var offerTotalPriceRaw = GetCellValue(ws, r, columnMap.OfferTotalPrice);
                var (price, isStockOut) = ParseOfferTotalPrice(offerTotalPriceRaw);

                var row = new PriceComparisonRow
                {
                    OfferId = GetCellValue(ws, r, columnMap.OfferId),
                    FocusCategory = GetCellValue(ws, r, columnMap.FocusCategory),
                    CategoryLabel = GetCellValue(ws, r, columnMap.CategoryLabel),
                    Gtin = GetCellValue(ws, r, columnMap.Gtin),
                    SourceProductId = GetCellValue(ws, r, columnMap.ProductId),
                    SourceProductBrand = GetCellValue(ws, r, columnMap.ProductBrand),
                    SearchName = productName,
                    TotalActiveOffers = GetCellValue(ws, r, columnMap.TotalActiveOffers),
                    SourceStock = GetCellValue(ws, r, columnMap.Stock),
                    WinnerAssortmentType = GetCellValue(ws, r, columnMap.WinnerAssortmentType),
                    MyPrice = price,
                    IsStockOut = isStockOut,
                    OfferScoreRank = GetCellValue(ws, r, columnMap.OfferScoreRank),
                    SourceSellerName = GetCellValue(ws, r, columnMap.SellerName),
                    ProductSoldItems30d = GetCellValue(ws, r, columnMap.ProductSoldItems30d),
                    ProductGmvInclShipping30d = GetCellValue(ws, r, columnMap.ProductGmvInclShipping30d),
                    SessionsByProductWithPdp30d = GetCellValue(ws, r, columnMap.SessionsByProductWithPdp30d),
                    SessionsByProductWithAddToCartInPdp30d = GetCellValue(ws, r, columnMap.SessionsByProductWithAddToCartInPdp30d)
                };

                var dedupeKey = !string.IsNullOrWhiteSpace(row.Gtin)
                    ? $"gtin:{row.Gtin}"
                    : $"name:{row.SearchName}";

                if (selectedRows.TryGetValue(dedupeKey, out var existingRow))
                {
                    duplicatesSkipped++;

                    if (ShouldReplacePriceComparisonRow(existingRow, row))
                    {
                        selectedRows[dedupeKey] = row;
                    }

                    continue;
                }

                selectedRows[dedupeKey] = row;
            }

            result = selectedRows.Values
                .OrderBy(row => row.SearchName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PriceCompare] Excel read error: {ex.Message}");
        }

        return (result, duplicatesSkipped);
    }

    private static bool ShouldReplacePriceComparisonRow(PriceComparisonRow currentRow, PriceComparisonRow candidateRow)
    {
        if (currentRow.IsStockOut && !candidateRow.IsStockOut)
        {
            return true;
        }

        if (!currentRow.IsStockOut && candidateRow.IsStockOut)
        {
            return false;
        }

        if (currentRow.MyPrice <= 0)
        {
            return candidateRow.MyPrice > 0;
        }

        if (candidateRow.MyPrice <= 0)
        {
            return false;
        }

        return candidateRow.MyPrice < currentRow.MyPrice;
    }

    private static string GetCellValue(ExcelWorksheet worksheet, int row, int column)
    {
        return column <= 0
            ? string.Empty
            : worksheet.Cells[row, column].Value?.ToString()?.Trim() ?? string.Empty;
    }

    private static (decimal Price, bool IsStockOut) ParseOfferTotalPrice(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (0, true);
        }

        var trimmed = raw.Trim();
        if (trimmed.Equals("stock out", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("stok yok", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("-", StringComparison.Ordinal))
        {
            return (0, true);
        }

        var cleaned = trimmed
            .Replace("TL", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("?", string.Empty)
            .Replace(" ", string.Empty)
            .Trim();

        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariantPrice) && invariantPrice > 0)
        {
            return (Math.Truncate(invariantPrice), false);
        }

        if (decimal.TryParse(cleaned, NumberStyles.Any, new CultureInfo("tr-TR"), out var turkishPrice) && turkishPrice > 0)
        {
            return (Math.Truncate(turkishPrice), false);
        }

        return (0, true);
    }

    private static PriceComparisonColumnMap GetPriceComparisonColumnMap(ExcelWorksheet worksheet)
    {
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var columnCount = worksheet.Dimension?.Columns ?? 0;

        for (int c = 1; c <= columnCount; c++)
        {
            var header = worksheet.Cells[1, c].Value?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(header) && !headers.ContainsKey(header))
            {
                headers[header] = c;
            }
        }

        return new PriceComparisonColumnMap(
            GetRequiredColumn(headers, "Offer id"),
            GetRequiredColumn(headers, "Focus Category"),
            GetRequiredColumn(headers, "Category Label"),
            GetRequiredColumn(headers, "gtin"),
            GetRequiredColumn(headers, "Product id"),
            GetRequiredColumn(headers, "Product Brand"),
            GetRequiredColumn(headers, "Product Name"),
            GetRequiredColumn(headers, "Total Active Offers"),
            GetRequiredColumn(headers, "Stock"),
            GetRequiredColumn(headers, "Winner Assortment Type"),
            GetRequiredColumn(headers, "Offer Total Price"),
            GetRequiredColumn(headers, "Offer Score Rank"),
            GetRequiredColumn(headers, "Seller Name"),
            GetRequiredColumn(headers, "Product - Sold items (30d)"),
            GetRequiredColumn(headers, "Product - GMV incl. Shipping (30d)"),
            GetRequiredColumn(headers, "Sessions by Product with PDP (30d)"),
            GetRequiredColumn(headers, "Sessions by Product with Add to Cart in pdp (30d)"));
    }

    private static int GetRequiredColumn(Dictionary<string, int> headers, string headerName)
    {
        return headers.TryGetValue(headerName, out var column)
            ? column
            : throw new InvalidOperationException($"Required column '{headerName}' was not found in the Excel file.");
    }

    private readonly record struct PriceComparisonColumnMap(
        int OfferId,
        int FocusCategory,
        int CategoryLabel,
        int Gtin,
        int ProductId,
        int ProductBrand,
        int ProductName,
        int TotalActiveOffers,
        int Stock,
        int WinnerAssortmentType,
        int OfferTotalPrice,
        int OfferScoreRank,
        int SellerName,
        int ProductSoldItems30d,
        int ProductGmvInclShipping30d,
        int SessionsByProductWithPdp30d,
        int SessionsByProductWithAddToCartInPdp30d);

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;

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

    /// <summary>
    /// Check if the market price is within a reasonable range of my price.
    /// Returns true if the product is likely the correct match.
    /// </summary>
    private static bool IsPriceInRange(decimal myPrice, AkakceProductInfo product)
    {
        if (myPrice <= 0) return true;

        var marketPrice = GetLowestNumericPrice(product);
        if (marketPrice <= 0) return true;

        var ratio = marketPrice / myPrice;
        return ratio >= (1m / PRICE_RATIO_THRESHOLD) && ratio <= PRICE_RATIO_THRESHOLD;
    }

    /// <summary>
    /// Fast check using the listing price from the search results page (no scrape needed).
    /// </summary>
    private static bool IsListingPriceInRange(decimal myPrice, decimal listingPrice)
    {
        if (myPrice <= 0 || listingPrice <= 0) return true;
        var ratio = listingPrice / myPrice;
        return ratio >= (1m / PRICE_RATIO_THRESHOLD) && ratio <= PRICE_RATIO_THRESHOLD;
    }

    /// <summary>
    /// Extract the lowest numeric price from the product's sellers.
    /// </summary>
    private static decimal GetLowestNumericPrice(AkakceProductInfo product)
    {
        IEnumerable<AkakceSellerInfo> sellers = product.HasVariants
            ? product.Variants.SelectMany(v => v.Sellers)
            : product.Sellers;

        var prices = sellers.Where(s => s.Price > 0).Select(s => s.Price).ToList();
        return prices.Count > 0 ? prices.Min() : 0;
    }
}
