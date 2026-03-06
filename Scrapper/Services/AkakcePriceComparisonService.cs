using OfficeOpenXml;
using Scrapper.Models;
using System.Collections.Concurrent;
using System.Globalization;

namespace Scrapper.Services;

public class AkakcePriceComparisonService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private const int MAX_RETRIES = 3;
    private const int MAX_CONSECUTIVE_FAILURES = 3;

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

            var inputRows = ReadInputExcel(excelStream);

            if (inputRows.Count == 0)
            {
                await onProgress(100, "?? No products found in the Excel file", "warning");
                await SendComplete(onProgress, null, 0);
                return;
            }

            await onProgress(5, $"? Found {inputRows.Count} products to compare", "success");

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

                var (searchName, myPrice, isStockOut) = inputRows[i];
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

                        var productUrl = await scraper.SearchProductAsync(searchName);

                        if (string.IsNullOrEmpty(productUrl))
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

                        await onProgress((int)currentProgress, "?? Found product, scraping sellers...", "info");
                        product = await scraper.ScrapeProductAsync(productUrl, false);
                        searchSuccess = true;
                        successCount++;

                        var sellerCount = product.HasVariants
                            ? product.Variants.Sum(v => v.SellerCount)
                            : product.SellerCount;
                        await onProgress((int)currentProgress,
                            $"? {Truncate(product.Name, 40)}: {sellerCount} sellers", "success");
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

                var row = new PriceComparisonRow
                {
                    SearchName = searchName,
                    MyPrice = myPrice,
                    IsStockOut = isStockOut
                };

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
            "n11" or "n11.com" => "n11",
            "pazarama" => "Pazarama",
            "pttavm" or "ptt avm" => "Pttavm",
            "teknosa" => "Teknosa",
            "trendyol" => "Trendyol",
            "amazon" or "amazon.com.tr" => "Amazon",
            "gittigidiyor" => "GittiGidiyor",
            "ciceksepeti" or "çiçeksepeti" => "ÇiçekSepeti",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Trim().ToLower())
        };
    }

    /// <summary>
    /// Read product names and prices from the Excel file.
    /// Expected: first row = headers, column A = product name, column B = price (numeric or "stock out").
    /// </summary>
    private static List<(string Name, decimal Price, bool IsStockOut)> ReadInputExcel(Stream stream)
    {
        var result = new List<(string, decimal, bool)>();

        try
        {
            using var package = new ExcelPackage(stream);
            var ws = package.Workbook.Worksheets.FirstOrDefault();
            if (ws == null) return result;

            var rowCount = ws.Dimension?.Rows ?? 0;
            // Always skip the first row (headers)
            for (int r = 2; r <= rowCount; r++)
            {
                var nameVal = ws.Cells[r, 1].Value?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(nameVal)) continue;

                var priceRaw = ws.Cells[r, 2].Value?.ToString()?.Trim() ?? string.Empty;
                var (price, isStockOut) = ParsePrice(priceRaw);

                result.Add((nameVal, price, isStockOut));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PriceCompare] Excel read error: {ex.Message}");
        }

        return result;
    }

    private static (decimal Price, bool IsStockOut) ParsePrice(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (0, true);

        var trimmed = raw.Trim();

        if (trimmed.Equals("stock out", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("stok yok", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("-", StringComparison.Ordinal))
            return (0, true);

        // Strip currency symbols and thousand separators, handle Turkish comma decimal
        var cleaned = trimmed
            .Replace("TL", "", StringComparison.OrdinalIgnoreCase)
            .Replace("?", "")
            .Trim();

        // Turkish format: 1.234,56 ? 1234.56
        if (cleaned.Contains(','))
            cleaned = cleaned.Replace(".", "").Replace(",", ".");
        else
            cleaned = cleaned.Replace(".", ""); // plain integer with dot thousands sep

        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) && price > 0)
            return (price, false);

        return (0, true);
    }

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
}
