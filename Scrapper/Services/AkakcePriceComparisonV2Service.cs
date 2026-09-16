using OfficeOpenXml;
using Scrapper.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Scrapper.Services;

/// <summary>
/// High-precision Akakce price comparison with balanced matching.
/// Improvements:
/// - safer decimal parsing for Excel prices
/// - relaxed scoring to reduce "No confident match"
/// - multi-query Akakce search without GTIN query
/// - detail page validation before accepting a match
/// </summary>
public class AkakcePriceComparisonV2Service
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();

    private readonly AkakceScrapeDoService _scrapeDoService;
    private readonly AkakceHttpSearchService _searchService;

    private const int MAX_SEARCH_RESULTS_PER_QUERY = 12;
    private const int MAX_UNIQUE_CANDIDATES = 20;
    private const int TOP_CANDIDATES_TO_VALIDATE = 4;

    private const int SCRAPEDO_DELAY_MS = 450;

    // Relaxed acceptance
    private const int ACCEPT_SCORE_THRESHOLD = 66;
    private const int ACCEPT_SCORE_LEAD = 5;
    private const int PREVALIDATION_MIN_SCORE = 0;

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonWordRegex = new(@"[^\p{L}\p{Nd}\s\-\/\.]", RegexOptions.Compiled);
    private static readonly Regex ModelTokenRegex = new(@"\b[a-z]*\d+[a-z0-9\-\/]*\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DigitsOnlyRegex = new(@"[^\d]", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ve", "ile", "icin", "için", "bir", "bu", "the", "for", "new", "yeni",
        "urun", "ürün", "model", "paket", "set", "renk", "color", "akilli", "akıllı",
        "telefon", "cep", "resmi", "garantili", "garanti", "distributor", "distribütör",
        "siyah", "beyaz", "mavi", "kirmizi", "kırmızı", "yesil", "yeşil", "gri", "gray",
        "silver", "black", "white"
    };

    public AkakcePriceComparisonV2Service(
        AkakceScrapeDoService scrapeDoService,
        AkakceHttpSearchService searchService)
    {
        _scrapeDoService = scrapeDoService;
        _searchService = searchService;
    }

    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
            cts.Cancel();
    }

    /// <summary>
    /// Backwards-compatible entry point: compare an .xlsx input and emit the original
    /// marketplace comparison report.
    /// </summary>
    public Task CompareFromExcelAsync(
        Stream excelStream,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null) =>
        CompareFromFileAsync(excelStream, "input.xlsx", new PriceComparisonOptions(), onProgress, sessionId);

    /// <summary>
    /// Match every product in the input against Akakce and emit a comparison report.
    /// Accepts .csv or .xlsx; <paramref name="options"/> controls scope and which
    /// report is produced.
    /// </summary>
    public async Task CompareFromFileAsync(
        Stream inputStream,
        string fileName,
        PriceComparisonOptions options,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrWhiteSpace(sessionId))
            _sessions[sessionId!] = cts;

        var rows = new List<PriceComparisonRow>();

        try
        {
            var isCsv = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            await onProgress(1, $"Reading {(isCsv ? "CSV" : "Excel")} file...", "info");

            var readResult = isCsv ? ReadInputCsv(inputStream) : ReadInputExcel(inputStream);
            var inputRows = readResult.Rows;
            int duplicatesSkipped = readResult.DuplicatesSkipped;

            if (inputRows.Count == 0)
            {
                await onProgress(100, "No products found in the input file", "warning");
                await SendComplete(onProgress, null, 0);
                return;
            }

            var dupMsg = duplicatesSkipped > 0 ? $" ({duplicatesSkipped} duplicate offer row(s) collapsed)" : "";
            await onProgress(3, $"Found {inputRows.Count} unique products{dupMsg}", "success");

            inputRows = ApplyScope(inputRows, options, out var scopeMsg);

            if (inputRows.Count == 0)
            {
                await onProgress(100, "No products left after applying the category filter", "warning");
                await SendComplete(onProgress, null, 0);
                return;
            }

            if (!string.IsNullOrEmpty(scopeMsg))
                await onProgress(5, scopeMsg, "info");

            await onProgress(8, "Starting product matching...", "info");

            int matchedCount = 0;
            int unmatchedCount = 0;
            int searchFailureCount = 0;
            int detailFailureCount = 0;
            int completedCount = 0;

            double progressBase = 10.0;
            double progressPerRow = 84.0 / inputRows.Count;

            // onProgress writes to a single SSE StreamWriter, which is not thread-safe -
            // concurrent writes would interleave and corrupt the event stream. One gate
            // serialises them.
            using var progressGate = new SemaphoreSlim(1, 1);

            async Task Report(int percent, string message, string type)
            {
                await progressGate.WaitAsync();
                try { await onProgress(percent, message, type); }
                finally { progressGate.Release(); }
            }

            // A slot per input row: workers finish out of order, but the report should
            // still follow the input ordering, and every row must appear even if it failed.
            var results = new PriceComparisonRow[inputRows.Count];

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.DegreeOfParallelism,
                // Cancellation is handled per row instead, so a stopped run still
                // produces a report containing everything finished so far.
                CancellationToken = CancellationToken.None
            };

            await Parallel.ForEachAsync(
                inputRows.Select((row, index) => (Row: row, Index: index)),
                parallelOptions,
                async (item, _) =>
            {
                var row = item.Row;
                results[item.Index] = row;

                if (cts.Token.IsCancellationRequested)
                {
                    row.ErrorMessage = "Cancelled";
                    return;
                }

                var done = Interlocked.Increment(ref completedCount);
                var pct = (int)Math.Min(94, progressBase + ((done - 1) * progressPerRow));

                try
                {
                    await Report(
                        pct,
                        $"[{done}/{inputRows.Count}] Matching: {Truncate(row.SearchName, 70)}",
                        "info");

                    var fingerprint = BuildFingerprint(row);
                    var queries = BuildSearchQueries(row, fingerprint);

                    if (queries.Count == 0)
                    {
                        row.ErrorMessage = "No usable search query could be built";
                        Interlocked.Increment(ref unmatchedCount);
                        return;
                    }

                    var listingCandidates = await SearchAndScoreCandidatesAsync(
                        row,
                        fingerprint,
                        queries,
                        cts.Token);

                    if (listingCandidates.Count == 0)
                    {
                        row.ErrorMessage = "No relevant search candidates found";
                        Interlocked.Increment(ref unmatchedCount);
                        Interlocked.Increment(ref searchFailureCount);
                        return;
                    }

                    var shortlisted = listingCandidates
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.TokenOverlapCount)
                        .Take(TOP_CANDIDATES_TO_VALIDATE)
                        .ToList();

                    var validated = new List<ValidatedCandidate>();

                    foreach (var candidate in shortlisted)
                    {
                        if (cts.Token.IsCancellationRequested)
                            break;

                        try
                        {
                            // row.SearchName steers variant selection when the candidate
                            // turns out to be a variant group page.
                            var product = await _scrapeDoService.ScrapeProductAsync(
                                candidate.Candidate.Url, row.SearchName, cts.Token);

                            if (product == null)
                            {
                                validated.Add(new ValidatedCandidate(
                                    candidate.Candidate,
                                    candidate.Score - 6,
                                    candidate.Reasons.Append("Detail page returned null").ToList(),
                                    null));

                                Interlocked.Increment(ref detailFailureCount);
                            }
                            else
                            {
                                var finalScore = ScoreDetailCandidate(row, fingerprint, candidate, product, out var detailReasons);

                                validated.Add(new ValidatedCandidate(
                                    candidate.Candidate,
                                    finalScore,
                                    candidate.Reasons.Concat(detailReasons).Distinct().ToList(),
                                    product));
                            }
                        }
                        catch (Exception ex)
                        {
                            validated.Add(new ValidatedCandidate(
                                candidate.Candidate,
                                candidate.Score - 6,
                                candidate.Reasons.Append($"Detail validation failed: {ex.Message}").ToList(),
                                null));

                            Interlocked.Increment(ref detailFailureCount);
                        }

                        try
                        {
                            await Task.Delay(SCRAPEDO_DELAY_MS, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }

                    if (cts.Token.IsCancellationRequested)
                    {
                        row.ErrorMessage = "Cancelled";
                        return;
                    }

                    if (validated.Count == 0)
                    {
                        row.ErrorMessage = "No candidate detail pages could be validated";
                        Interlocked.Increment(ref unmatchedCount);
                        return;
                    }

                    var orderedValidated = validated
                        .OrderByDescending(x => x.Score)
                        .ToList();

                    var best = orderedValidated[0];
                    var second = orderedValidated.Count > 1 ? orderedValidated[1] : null;

                    if (IsConfidentMatch(best, second))
                    {
                        ApplyAcceptedMatch(row, best);
                        Interlocked.Increment(ref matchedCount);

                        await Report(
                            pct,
                            $"Matched: {Truncate(best.Product?.Name ?? best.Candidate.Title, 70)} [score={best.Score}]",
                            "success");
                    }
                    else
                    {
                        row.ErrorMessage = BuildNoConfidenceMessage(orderedValidated);
                        Interlocked.Increment(ref unmatchedCount);

                        await Report(
                            pct,
                            $"No confident match: {Truncate(row.SearchName, 60)}",
                            "warning");
                    }
                }
                catch (OperationCanceledException)
                {
                    row.ErrorMessage = "Cancelled";
                }
                catch (Exception ex)
                {
                    row.ErrorMessage = ex.Message;
                    Interlocked.Increment(ref unmatchedCount);

                    Console.WriteLine($"[PriceCompV2] Row failed for '{row.SearchName}': {ex.Message}");
                }
            });

            rows.AddRange(results.Where(r => r != null));

            await onProgress(
                95,
                $"Creating comparison Excel report... Matched={matchedCount}, Unmatched={unmatchedCount}",
                "info");

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var outputFileName = options.RetailReport
                ? $"RetailVsMarketplace_{timestamp}.xlsx"
                : $"AkakcePriceComparison_{timestamp}.xlsx";
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), outputFileName);

            if (options.RetailReport)
                new RetailVsMarketplaceExcelExporter().Export(rows, filePath);
            else
                new AkakcePriceComparisonExcelExporter().Export(rows, filePath);

            var done = rows.Count(r => r.IsSuccess);
            var failed = rows.Count - done;
            var withRetail = rows.Count(r => r.RetailPrice > 0);

            await onProgress(
                100,
                $"Done! {done} matched, {failed} unmatched/failed, {withRetail} with a MediaMarkt Retail price. " +
                $"SearchFailures={searchFailureCount}, DetailFailures={detailFailureCount}",
                "success");

            await SendComplete(onProgress, outputFileName, done);
        }
        catch (Exception ex)
        {
            await onProgress(100, $"Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, 0);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
                _sessions.TryRemove(sessionId!, out _);

            cts.Dispose();
        }
    }

    // =========================
    // Matching pipeline
    // =========================

    private async Task<List<ScoredCandidate>> SearchAndScoreCandidatesAsync(
        PriceComparisonRow row,
        ProductFingerprint fingerprint,
        List<string> queries,
        CancellationToken cancellationToken)
    {
        var aggregated = new Dictionary<string, ScoredCandidate>(StringComparer.OrdinalIgnoreCase);

        // Deliberately silent: at four queries per product this would add thousands of
        // interleaved lines to the live log on a full run without telling the operator
        // anything they act on.
        foreach (var query in queries)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            List<(string Title, string Url, decimal ListingPrice)> rawCandidates;
            try
            {
                rawCandidates = await _searchService.SearchProductCandidatesAsync(
                    query, MAX_SEARCH_RESULTS_PER_QUERY, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PriceCompV2] Search query failed '{query}': {ex.Message}");
                continue;
            }

            foreach (var (title, url, listingPrice) in rawCandidates)
            {
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
                    continue;

                var candidate = new SearchCandidateInfo(
                    Query: query,
                    Title: title.Trim(),
                    Url: url.Trim(),
                    ListingPrice: listingPrice);

                var scored = ScoreListingCandidate(row, fingerprint, candidate);

                if (scored.Score < PREVALIDATION_MIN_SCORE)
                    continue;

                if (aggregated.TryGetValue(candidate.Url, out var existing))
                {
                    if (scored.Score > existing.Score)
                        aggregated[candidate.Url] = scored;
                }
                else
                {
                    aggregated[candidate.Url] = scored;
                }

                if (aggregated.Count >= MAX_UNIQUE_CANDIDATES)
                    break;
            }

            if (aggregated.Count >= MAX_UNIQUE_CANDIDATES)
                break;
        }

        return aggregated.Values
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.TokenOverlapCount)
            .ToList();
    }

    private static ScoredCandidate ScoreListingCandidate(
        PriceComparisonRow row,
        ProductFingerprint source,
        SearchCandidateInfo candidate)
    {
        var reasons = new List<string>();
        int score = 0;

        var candNorm = NormalizeText(candidate.Title);
        var candTokens = Tokenize(candNorm);
        var candModels = ExtractModelTokens(candNorm);
        var candAttrs = ExtractAttributes(candNorm);

        if (!string.IsNullOrWhiteSpace(source.Brand))
        {
            if (candNorm.Contains(source.Brand, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
                reasons.Add("Brand match");
            }
            else
            {
                score -= 4;
                reasons.Add("Brand not visible");
            }
        }

        var tokenOverlap = source.Tokens.Intersect(candTokens, StringComparer.OrdinalIgnoreCase).Count();
        var tokenUnion = source.Tokens.Union(candTokens, StringComparer.OrdinalIgnoreCase).Count();
        var jaccard = tokenUnion == 0 ? 0 : (double)tokenOverlap / tokenUnion;

        if (candNorm.Equals(source.NormalizedTitle, StringComparison.OrdinalIgnoreCase))
        {
            score += 26;
            reasons.Add("Exact normalized title");
        }
        else if (candNorm.Contains(source.NormalizedTitle, StringComparison.OrdinalIgnoreCase) ||
                 source.NormalizedTitle.Contains(candNorm, StringComparison.OrdinalIgnoreCase))
        {
            score += 15;
            reasons.Add("Title containment");
        }

        if (jaccard >= 0.50)
        {
            score += 18;
            reasons.Add($"Strong title similarity ({jaccard:F2})");
        }
        else if (jaccard >= 0.32)
        {
            score += 12;
            reasons.Add($"Moderate title similarity ({jaccard:F2})");
        }
        else if (jaccard >= 0.18)
        {
            score += 5;
            reasons.Add($"Weak title similarity ({jaccard:F2})");
        }
        else
        {
            score -= 2;
            reasons.Add($"Low title similarity ({jaccard:F2})");
        }

        if (source.ModelTokens.Count > 0)
        {
            var overlap = source.ModelTokens.Intersect(candModels, StringComparer.OrdinalIgnoreCase).ToList();

            if (overlap.Count > 0)
            {
                score += 28;
                reasons.Add($"Model match: {string.Join(", ", overlap)}");
            }
            else if (candModels.Count > 0)
            {
                score -= 10;
                reasons.Add("Model differs");
            }
            else
            {
                reasons.Add("Model not visible in candidate");
            }
        }

        foreach (var kv in source.Attributes)
        {
            if (!candAttrs.TryGetValue(kv.Key, out var candValue))
                continue;

            if (string.Equals(kv.Value, candValue, StringComparison.OrdinalIgnoreCase))
            {
                score += 8;
                reasons.Add($"{kv.Key} match ({candValue})");
            }
            else
            {
                score -= 10;
                reasons.Add($"{kv.Key} mismatch (src={kv.Value}, cand={candValue})");
            }
        }

        if (!string.IsNullOrWhiteSpace(source.Category))
        {
            var categoryTokens = Tokenize(source.Category);
            var categoryOverlap = categoryTokens.Intersect(candTokens, StringComparer.OrdinalIgnoreCase).Count();
            if (categoryOverlap > 0)
            {
                score += 3;
                reasons.Add("Category hint matched");
            }
        }

        score += ScorePriceSanity(row.MyPrice, candidate.ListingPrice, out var priceReason);
        if (!string.IsNullOrWhiteSpace(priceReason))
            reasons.Add(priceReason);

        return new ScoredCandidate(candidate, score, tokenOverlap, reasons);
    }

    private static int ScoreDetailCandidate(
        PriceComparisonRow row,
        ProductFingerprint source,
        ScoredCandidate listingCandidate,
        AkakceProductInfo product,
        out List<string> detailReasons)
    {
        detailReasons = new List<string>();

        if (product == null)
        {
            detailReasons.Add("Detail product is null");
            return listingCandidate.Score - 6;
        }

        int score = listingCandidate.Score;

        if (!product.IsSuccess)
        {
            detailReasons.Add($"Detail scrape failed: {product.ErrorMessage}");
            return score - 6;
        }

        var detailTitle = product.Name ?? string.Empty;
        var detailNorm = NormalizeText(detailTitle);
        var detailTokens = Tokenize(detailNorm);
        var detailModels = ExtractModelTokens(detailNorm);
        var detailAttrs = ExtractAttributes(detailNorm);

        if (!string.IsNullOrWhiteSpace(source.Brand))
        {
            if (detailNorm.Contains(source.Brand, StringComparison.OrdinalIgnoreCase))
            {
                score += 6;
                detailReasons.Add("Brand confirmed on detail page");
            }
            else
            {
                score -= 3;
                detailReasons.Add("Brand not visible on detail page");
            }
        }

        var overlap = source.Tokens.Intersect(detailTokens, StringComparer.OrdinalIgnoreCase).Count();
        var union = source.Tokens.Union(detailTokens, StringComparer.OrdinalIgnoreCase).Count();
        var jaccard = union == 0 ? 0 : (double)overlap / union;

        if (jaccard >= 0.55)
        {
            score += 15;
            detailReasons.Add($"Strong detail-title similarity ({jaccard:F2})");
        }
        else if (jaccard >= 0.35)
        {
            score += 8;
            detailReasons.Add($"Moderate detail-title similarity ({jaccard:F2})");
        }
        else if (jaccard >= 0.20)
        {
            score += 3;
            detailReasons.Add($"Weak detail-title similarity ({jaccard:F2})");
        }
        else
        {
            score -= 3;
            detailReasons.Add($"Low detail-title similarity ({jaccard:F2})");
        }

        if (source.ModelTokens.Count > 0)
        {
            var modelOverlap = source.ModelTokens.Intersect(detailModels, StringComparer.OrdinalIgnoreCase).ToList();

            if (modelOverlap.Count > 0)
            {
                score += 18;

                // Separate a real part number from an incidental one. "ddr5" or "16gb"
                // overlapping means little; "ax5u6400c3216g-clarbk" overlapping means
                // this is the same product, and IsConfidentMatch treats it that way.
                var distinctive = modelOverlap.Where(IsDistinctiveSku).ToList();
                if (distinctive.Count > 0)
                    detailReasons.Add($"Distinctive SKU confirmed on detail page: {string.Join(", ", distinctive)}");
                else
                    detailReasons.Add($"Model confirmed on detail page: {string.Join(", ", modelOverlap)}");
            }
            else if (FindSkuInTitle(source.ModelTokens, detailNorm) is { } sku)
            {
                // Exact token comparison misses SKUs that the two titles punctuate
                // differently ("AX5U6400C3216G-CLARBK" tokenised one way on our side and
                // another on Akakce's), so fall back to containment.
                score += 22;
                detailReasons.Add($"Distinctive SKU confirmed on detail page: {sku}");
            }
            else if (detailModels.Count > 0)
            {
                score -= 10;
                detailReasons.Add("Detail page model differs");
            }
        }

        foreach (var kv in source.Attributes)
        {
            if (!detailAttrs.TryGetValue(kv.Key, out var detailValue))
                continue;

            if (string.Equals(kv.Value, detailValue, StringComparison.OrdinalIgnoreCase))
            {
                score += 6;
                detailReasons.Add($"{kv.Key} confirmed on detail page ({detailValue})");
            }
            else
            {
                score -= 10;
                detailReasons.Add($"{kv.Key} conflict on detail page (src={kv.Value}, detail={detailValue})");
            }
        }

        score += ScoreColourAgreement(source.NormalizedTitle, detailNorm, detailReasons);

        var bestDetailPrice = GetBestProductPrice(product);
        score += ScorePriceSanity(row.MyPrice, bestDetailPrice, out var detailPriceReason);
        if (!string.IsNullOrWhiteSpace(detailPriceReason))
            detailReasons.Add($"Detail price: {detailPriceReason}");

        if (product.SellerCount > 0)
        {
            score += 2;
            detailReasons.Add($"Seller count available ({product.SellerCount})");
        }

        return score;
    }

    private static bool IsConfidentMatch(ValidatedCandidate best, ValidatedCandidate? second)
    {
        if (best.Product == null || !best.Product.IsSuccess)
            return false;

        var skuConfirmed = best.Reasons.Any(r =>
            r.StartsWith("Distinctive SKU confirmed", StringComparison.OrdinalIgnoreCase));

        // An exact manufacturer part number is stronger evidence than any amount of
        // title similarity, so it clears the score threshold on its own. Without this,
        // rows whose Akakce listing is titled differently to ours - "ADATA
        // AX5U6400C3216G-CLARBK ... PC Ram" against "XPG Lancer RGB 16 GB ...
        // AX5U6400C3216G-CLARBK" - scored 63 against a threshold of 66 and were
        // reported as unmatched despite the part numbers being identical.
        // Conflicts below still veto.
        if (!skuConfirmed && best.Score < ACCEPT_SCORE_THRESHOLD)
            return false;

        // A recorded conflict blocks acceptance outright, however far ahead the
        // candidate is on points.
        //
        // This used to be checked only in the tie-break branch below, so a candidate
        // that led by five points was accepted even when the detail page contradicted
        // the source. That is how an "iPad Pro M5 256GB" row matched an "iPad Air
        // 256GB" listing at score 114: enough shared tokens (apple/ipad/256gb/wi-fi/11)
        // to outweigh a -10 model mismatch. In a pricing report a confidently wrong
        // price is worse than a gap, and unmatched rows are already flagged for review.
        if (CountHardConflicts(best.Reasons) > 0)
            return false;

        if (skuConfirmed)
            return true;

        if (second == null)
            return true;

        if ((best.Score - second.Score) >= ACCEPT_SCORE_LEAD)
            return true;

        var strongSignals = best.Reasons.Count(r =>
            r.Contains("Brand match", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("Model match", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("Strong title similarity", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("confirmed on detail page", StringComparison.OrdinalIgnoreCase));

        return best.Score >= 70 && strongSignals >= 2;
    }

    private static readonly string[] ColourWords =
    [
        "siyah", "beyaz", "mavi", "kirmizi", "kırmızı", "yesil", "yeşil", "gri",
        "lacivert", "pembe", "mor", "sari", "sarı", "turuncu", "altin", "altın",
        "gumus", "gümüş", "bej", "kahverengi", "black", "white", "blue", "red",
        "green", "grey", "gray", "silver", "gold", "pink"
    ];

    /// <summary>
    /// Nudge candidates towards the right colour variant.
    /// </summary>
    /// <remarks>
    /// Akakce lists each colour of a speaker or phone as its own product, but colour
    /// words are stop-words for tokenising, so the scorer cannot tell them apart:
    /// "Anker SoundCore Glow" (plain), "... Siyah" and "... Kirmizi" all tied on 66
    /// against a "Mavi" source and the row was dropped as ambiguous. Comparing colours
    /// directly on the raw titles breaks that tie; an untitled-colour listing is left
    /// neutral because it is usually the parent product.
    /// </remarks>
    private static int ScoreColourAgreement(string sourceNorm, string detailNorm, List<string> reasons)
    {
        var sourceColour = ColourWords.FirstOrDefault(c => ContainsWord(sourceNorm, c));
        if (sourceColour == null) return 0;

        if (ContainsWord(detailNorm, sourceColour))
        {
            reasons.Add($"Colour confirmed on detail page ({sourceColour})");
            return 8;
        }

        var detailColour = ColourWords.FirstOrDefault(c => ContainsWord(detailNorm, c));
        if (detailColour == null) return 0;

        // Deliberately avoids the words that CountHardConflicts treats as vetoes: the
        // wrong colour of the right model almost always carries the same price, so this
        // should only break ties, never reject a row outright.
        reasons.Add($"Different colour on detail page (src={sourceColour}, detail={detailColour})");
        return -8;
    }

    private static bool ContainsWord(string haystack, string word) =>
        Regex.IsMatch(haystack, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// A number followed by a unit - "6400mhz", "18000btu", "256gb". These look like
    /// part numbers by length and character mix but are specifications that unrelated
    /// products share, so they must never trigger the SKU shortcut.
    /// </summary>
    private static readonly Regex SpecTokenRegex = new(@"^\d+[a-z]{1,4}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// True for a token that identifies one product rather than describing it: long
    /// enough to be a part number, mixing letters and digits, and not a bare spec.
    /// </summary>
    private static bool IsDistinctiveSku(string token) =>
        token.Length >= 6
        && ContainsLetterAndDigit(token)
        && !SpecTokenRegex.IsMatch(token);

    /// <summary>
    /// Find a distinctive SKU from the source that appears verbatim in a candidate title.
    /// Only long mixed letter-and-digit tokens qualify: short ones like "16gb" or "40w"
    /// are specifications shared by unrelated products, not identifiers.
    /// </summary>
    private static string? FindSkuInTitle(IEnumerable<string> modelTokens, string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)) return null;

        var stripped = DigitsLettersOnly(normalizedTitle);

        return modelTokens
            .Where(IsDistinctiveSku)
            .OrderByDescending(t => t.Length)
            .FirstOrDefault(t =>
                normalizedTitle.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                stripped.Contains(DigitsLettersOnly(t), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Drop punctuation so "ax5u-6400/c32" and "ax5u6400c32" compare equal.</summary>
    private static string DigitsLettersOnly(string input) =>
        new(input.Where(char.IsLetterOrDigit).ToArray());

    private static int CountHardConflicts(IEnumerable<string> reasons) =>
        reasons.Count(r =>
            r.Contains("mismatch", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("differs", StringComparison.OrdinalIgnoreCase));

    private static void ApplyAcceptedMatch(PriceComparisonRow row, ValidatedCandidate best)
    {
        var product = best.Product!;
        row.AkakceName = product.Name;
        row.AkakceUrl = product.ProductUrl;
        row.ErrorMessage = string.Empty;
        row.MatchScore = best.Score;
        row.MatchNotes = string.Join(" | ", best.Reasons);

        CollectMarketplacePrices(product, row);
        CollectRetailAndAggregate(product, row);
    }

    /// <summary>
    /// Copy the aggregate price summary and the first-party retailer prices onto the row.
    ///
    /// The MediaMarkt entry in the store-prices block is MediaMarkt Retail (1P) - a
    /// different business to the Marketplace (3P) sellers the input CSV describes,
    /// which is exactly the comparison this report exists to make.
    /// </summary>
    private static void CollectRetailAndAggregate(AkakceProductInfo product, PriceComparisonRow row)
    {
        row.MarketLowestPrice = product.MarketLowestPrice;
        row.MarketOfferCount = product.MarketOfferCount;

        foreach (var kv in product.StorePrices)
            row.StorePrices[kv.Key] = kv.Value;

        foreach (var kv in product.StorePrices)
        {
            if (!IsMediaMarktRetail(kv.Key)) continue;
            if (row.RetailPrice <= 0 || kv.Value < row.RetailPrice)
                row.RetailPrice = kv.Value;
        }
    }

    /// <summary>
    /// True for the MediaMarkt first-party store entry. Deliberately does NOT match
    /// "Media Markt Pazar Yeri", which is the third-party marketplace.
    /// </summary>
    private static bool IsMediaMarktRetail(string name)
    {
        var normalized = NormalizeMarketplace(name);
        return normalized.Equals("Media Markt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for any MediaMarkt entity, Retail or Pazar Yeri.</summary>
    private static bool IsAnyMediaMarkt(string name)
    {
        var normalized = NormalizeMarketplace(name);
        return normalized.StartsWith("Media Markt", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildNoConfidenceMessage(List<ValidatedCandidate> validated)
    {
        var ordered = validated.OrderByDescending(x => x.Score).Take(3).ToList();
        if (ordered.Count == 0)
            return "No confident match";

        var summary = ordered
            .Select(x => $"[{x.Score}] {Truncate(x.Product?.Name ?? x.Candidate.Title, 55)}")
            .ToList();

        // Spell out why the leader fell short. Without this a reviewer only sees a
        // number and cannot tell a genuine near-miss from a wrong product.
        var best = ordered[0];
        var verdict = best.Score < ACCEPT_SCORE_THRESHOLD
            ? $"below threshold ({best.Score} < {ACCEPT_SCORE_THRESHOLD})"
            : CountHardConflicts(best.Reasons) > 0
                ? "blocked by conflict"
                : "too close to runner-up";

        return $"No confident match ({verdict}). Top candidates: {string.Join(" | ", summary)}"
             + $" || Best-candidate signals: {string.Join("; ", best.Reasons)}";
    }

    // =========================
    // Fingerprint + search query building
    // =========================

    private static ProductFingerprint BuildFingerprint(PriceComparisonRow row)
    {
        var normalizedTitle = NormalizeText(row.SearchName);
        var brand = NormalizeText(row.SourceProductBrand);
        var gtin = NormalizeGtin(row.Gtin);
        var category = NormalizeText(row.CategoryLabel);

        var tokens = Tokenize(normalizedTitle);
        var modelTokens = ExtractModelTokens(normalizedTitle);
        var attributes = ExtractAttributes(normalizedTitle);

        return new ProductFingerprint(
            OriginalTitle: row.SearchName?.Trim() ?? string.Empty,
            NormalizedTitle: normalizedTitle,
            Brand: brand,
            Gtin: gtin,
            Category: category,
            Tokens: tokens,
            ModelTokens: modelTokens,
            Attributes: attributes);
    }

    private static List<string> BuildSearchQueries(PriceComparisonRow row, ProductFingerprint fp)
    {
        var queries = new List<string>();

        void Add(string? q)
        {
            if (string.IsNullOrWhiteSpace(q))
                return;

            var trimmed = MultiSpaceRegex.Replace(q.Trim(), " ");
            if (trimmed.Length < 2)
                return;

            if (!queries.Any(x => x.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
                queries.Add(trimmed);
        }

        // 1) Brand + the most distinctive model tokens, plus any attributes that add
        //    something new.
        //
        //    ModelTokens is an unordered set that frequently overlaps Attributes, so
        //    taking two of each blindly produced queries like "ANKER 40w 40w" and
        //    "APPLE 256gb mdwk4tu/a 256gb 12gb". Ordering by distinctiveness puts the
        //    real SKU first, and de-duplicating keeps the query clean.
        if (!string.IsNullOrWhiteSpace(row.SourceProductBrand) && fp.ModelTokens.Count > 0)
        {
            var parts = new List<string>();

            void AddPart(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (parts.Any(p => p.Equals(value, StringComparison.OrdinalIgnoreCase))) return;
                parts.Add(value.Trim());
            }

            foreach (var token in RankModelTokens(fp.ModelTokens).Take(2))
                AddPart(token);

            foreach (var attribute in fp.Attributes.Values.Take(2))
                AddPart(attribute);

            Add($"{row.SourceProductBrand} {string.Join(" ", parts)}".Trim());
        }

        // 2) Brand + compact title
        if (!string.IsNullOrWhiteSpace(row.SourceProductBrand))
            Add($"{row.SourceProductBrand} {BuildCompactSearchTitle(row.SearchName, 8)}");

        // 3) Compact title
        Add(BuildCompactSearchTitle(row.SearchName, 10));

        // 4) Raw title
        Add(row.SearchName);

        return queries;
    }

    /// <summary>
    /// Order model tokens so the ones that actually identify a product come first:
    /// mixed letters-and-digits SKUs ("mdwk4tu", "ax5u6400c3216g") beat bare capacity
    /// figures ("256gb", "40w"), and longer beats shorter.
    /// </summary>
    private static IEnumerable<string> RankModelTokens(IEnumerable<string> modelTokens) =>
        modelTokens
            .OrderByDescending(ContainsLetterAndDigit)
            .ThenByDescending(t => t.Length)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase);

    private static string BuildCompactSearchTitle(string? raw, int keep)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var parts = Regex
            .Split(raw, @"[\s,;:()\[\]\{\}\-_/\\]+")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        var filtered = new List<string>();

        foreach (var part in parts)
        {
            var norm = NormalizeText(part);
            if (string.IsNullOrWhiteSpace(norm))
                continue;

            if (StopWords.Contains(norm))
                continue;

            filtered.Add(part);
            if (filtered.Count >= keep)
                break;
        }

        return string.Join(" ", filtered);
    }

    // =========================
    // Text normalization / extraction
    // =========================

    private static string NormalizeText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var s = input.Trim().ToLowerInvariant()
            .Replace("i̇", "i")
            .Replace("ı", "i")
            .Replace("ş", "s")
            .Replace("ğ", "g")
            .Replace("ü", "u")
            .Replace("ö", "o")
            .Replace("ç", "c")
            .Replace("’", "'")
            .Replace("₺", " ")
            .Replace("tl", " ")
            .Replace("try", " ");

        s = s.Replace("\"", " inch ")
             .Replace("”", " inch ")
             .Replace("“", " inch ");

        s = NonWordRegex.Replace(s, " ");
        s = MultiSpaceRegex.Replace(s, " ").Trim();

        s = s.Replace("g b", "gb")
             .Replace("t b", "tb")
             .Replace("m a h", "mah")
             .Replace("h z", "hz")
             .Replace("inç", "inch");

        return s;
    }

    private static HashSet<string> Tokenize(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return normalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ExtractModelTokens(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return ModelTokenRegex.Matches(normalizedText)
            .Select(m => m.Value.Trim().ToLowerInvariant())
            .Where(v => v.Length >= 3 && ContainsLetterAndDigit(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ExtractAttributes(string normalizedText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static void AddIfMatch(Dictionary<string, string> dict, string key, string text, string pattern, Func<Match, string>? valueFactory = null)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (m.Success)
                dict[key] = valueFactory?.Invoke(m) ?? m.Value.Replace(" ", "").ToLowerInvariant();
        }

        AddIfMatch(result, "storage", normalizedText, @"\b(16|32|64|128|256|512|1024|1|2)\s?(gb|tb)\b",
            m => $"{m.Groups[1].Value.ToLowerInvariant()}{m.Groups[2].Value.ToLowerInvariant()}");

        AddIfMatch(result, "ram", normalizedText, @"\b(2|3|4|6|8|12|16|24|32)\s?gb\s?(ram)?\b",
            m => $"{m.Groups[1].Value.ToLowerInvariant()}gb");

        AddIfMatch(result, "inch", normalizedText, @"\b(\d{1,3}([.,]\d{1,2})?)\s?inch\b",
            m => m.Groups[1].Value.Replace(",", ".").ToLowerInvariant());

        AddIfMatch(result, "hz", normalizedText, @"\b(50|60|75|90|100|120|144|165|240)\s?hz\b",
            m => $"{m.Groups[1].Value.ToLowerInvariant()}hz");

        AddIfMatch(result, "mah", normalizedText, @"\b(\d{3,5})\s?mah\b",
            m => $"{m.Groups[1].Value.ToLowerInvariant()}mah");

        AddIfMatch(result, "watt", normalizedText, @"\b(\d{1,4})\s?w\b",
            m => $"{m.Groups[1].Value.ToLowerInvariant()}w");

        AddIfMatch(result, "pack", normalizedText, @"\b(\d+)\s?(li|adet|pack)\b",
            m => $"{m.Groups[1].Value.ToLowerInvariant()}pack");

        return result;
    }

    private static bool ContainsLetterAndDigit(string input)
    {
        bool hasLetter = false;
        bool hasDigit = false;

        foreach (var ch in input)
        {
            if (char.IsLetter(ch)) hasLetter = true;
            else if (char.IsDigit(ch)) hasDigit = true;

            if (hasLetter && hasDigit)
                return true;
        }

        return false;
    }

    private static string NormalizeGtin(string? gtin)
    {
        if (string.IsNullOrWhiteSpace(gtin))
            return string.Empty;

        return DigitsOnlyRegex.Replace(gtin, "");
    }

    // =========================
    // Price parsing / sanity
    // =========================

    private static (decimal Price, bool IsStockOut) ParsePrice(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (0m, true);

        var trimmed = raw.Trim();

        if (trimmed.Equals("stock out", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("stok yok", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("-", StringComparison.Ordinal))
            return (0m, true);

        var normalized = NormalizePriceString(trimmed);
        if (string.IsNullOrWhiteSpace(normalized))
            return (0m, true);

        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0)
            return (decimal.Round(value, 2, MidpointRounding.AwayFromZero), false);

        return (0m, true);
    }

    private static string NormalizePriceString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Trim()
            .Replace("TL", "", StringComparison.OrdinalIgnoreCase)
            .Replace("TRY", "", StringComparison.OrdinalIgnoreCase)
            .Replace("₺", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "")
            .Replace("\u00A0", "")
            .Trim();

        s = Regex.Replace(s, @"[^\d,.\-]", "");

        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        int lastDot = s.LastIndexOf('.');
        int lastComma = s.LastIndexOf(',');

        if (lastDot >= 0 && lastComma >= 0)
        {
            if (lastDot > lastComma)
            {
                // Example: 120,335.04
                s = s.Replace(",", "");
            }
            else
            {
                // Example: 120.335,04
                s = s.Replace(".", "");
                s = s.Replace(",", ".");
            }

            return s;
        }

        if (lastComma >= 0)
        {
            var decimals = s.Length - lastComma - 1;

            if (decimals is 1 or 2)
            {
                s = s.Replace(".", "");
                s = s.Replace(",", ".");
            }
            else
            {
                s = s.Replace(",", "");
            }

            return s;
        }

        if (lastDot >= 0)
        {
            var decimals = s.Length - lastDot - 1;

            if (decimals is 1 or 2)
            {
                s = s.Replace(",", "");
            }
            else
            {
                s = s.Replace(".", "");
            }

            return s;
        }

        return s;
    }

    private static int ScorePriceSanity(decimal myPrice, decimal candidatePrice, out string reason)
    {
        reason = string.Empty;

        if (myPrice <= 0 || candidatePrice <= 0)
            return 0;

        var ratio = candidatePrice / myPrice;

        if (ratio >= 0.90m && ratio <= 1.12m)
        {
            reason = $"Very close price ({candidatePrice:0.##})";
            return 8;
        }

        if (ratio >= 0.72m && ratio <= 1.35m)
        {
            reason = $"Close price ({candidatePrice:0.##})";
            return 5;
        }

        if (ratio >= 0.50m && ratio <= 1.90m)
        {
            reason = $"Reasonable price ({candidatePrice:0.##})";
            return 1;
        }

        if (ratio >= 0.30m && ratio <= 3.00m)
        {
            reason = $"Weak price sanity ({candidatePrice:0.##})";
            return -1;
        }

        reason = $"Price far from source ({candidatePrice:0.##})";
        return -4;
    }

    private static decimal GetBestProductPrice(AkakceProductInfo? product)
    {
        if (product == null)
            return 0m;

        IEnumerable<AkakceSellerInfo> sellers = product.HasVariants
            ? product.Variants.SelectMany(v => v.Sellers)
            : product.Sellers;

        return sellers
            .Where(s => s.InStock && s.Price > 0)
            .Select(s => s.Price)
            .DefaultIfEmpty(0m)
            .Min();
    }

    // =========================
    // Marketplace collection
    // =========================

    private static void CollectMarketplacePrices(AkakceProductInfo product, PriceComparisonRow row)
    {
        IEnumerable<AkakceSellerInfo> allSellers = product.HasVariants
            ? product.Variants.SelectMany(v => v.Sellers)
            : product.Sellers;

        var inStock = allSellers.Where(s => s.InStock && s.Price > 0).ToList();

        foreach (var seller in inStock)
        {
            var mp = NormalizeMarketplace(seller.Marketplace);
            if (!row.MarketplaceBestPrices.TryGetValue(mp, out var existing) || seller.Price < existing)
                row.MarketplaceBestPrices[mp] = seller.Price;
        }

        if (inStock.Count == 0) return;

        var cheapest = inStock.OrderBy(s => s.Price).First();
        row.CheapestMarketplace = NormalizeMarketplace(cheapest.Marketplace);
        row.CheapestSeller = cheapest.SellerName;

        var competitor = inStock
            .Where(s => !IsAnyMediaMarkt(s.Marketplace))
            .Select(s => s.Price)
            .DefaultIfEmpty(0m)
            .Min();

        row.CheapestExcludingMediaMarkt = competitor;
    }

    private static string NormalizeMarketplace(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Diğer";

        return raw.Trim().ToLowerInvariant() switch
        {
            "hepsiburada" => "Hepsiburada",
            "idefix" or "i̇defix" or "idefix.com" => "İdefix",
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
            "koçtaş" or "koctas" or "koctas.com.tr" or "koçtaş.com.tr" => "Koçtaş",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Trim().ToLowerInvariant())
        };
    }

    // =========================
    // Completion + UI helpers
    // =========================

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

    private static string Truncate(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty : s.Length > max ? s[..max] + "..." : s;

    // =========================
    // Scope
    // =========================

    /// <summary>
    /// Narrow the work set to the requested categories and product cap.
    /// </summary>
    /// <remarks>
    /// A pilot run takes a round-robin slice across Focus Categories rather than the
    /// first N alphabetically. The input is heavily skewed - IT Acc. alone is roughly a
    /// third of it - so an alphabetical head would validate matching against one kind of
    /// product and tell you very little about the rest.
    /// </remarks>
    private static List<PriceComparisonRow> ApplyScope(
        List<PriceComparisonRow> rows,
        PriceComparisonOptions options,
        out string message)
    {
        message = string.Empty;
        var parts = new List<string>();

        if (options.FocusCategories is { Count: > 0 })
        {
            rows = rows
                .Where(r => options.FocusCategories.Contains(r.FocusCategory.Trim()))
                .ToList();

            parts.Add($"category filter: {string.Join(", ", options.FocusCategories)}");
        }

        if (options.MaxProducts is > 0 && rows.Count > options.MaxProducts)
        {
            var groups = rows
                .GroupBy(r => r.FocusCategory, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.ToList())
                .ToList();

            var sampled = new List<PriceComparisonRow>();
            for (int i = 0; sampled.Count < options.MaxProducts; i++)
            {
                bool tookAny = false;
                foreach (var group in groups)
                {
                    if (i >= group.Count) continue;

                    sampled.Add(group[i]);
                    tookAny = true;

                    if (sampled.Count >= options.MaxProducts) break;
                }

                if (!tookAny) break;
            }

            rows = sampled;
            parts.Add($"pilot sample of {rows.Count} across {groups.Count} categories");
        }

        if (parts.Count > 0)
            message = $"Scope - {string.Join("; ", parts)} => {rows.Count} product(s)";

        return rows;
    }

    // =========================
    // CSV reading
    // =========================

    /// <summary>
    /// Read the Offer KPI export in CSV form.
    /// </summary>
    /// <remarks>
    /// Reuses the quote-aware splitter from <see cref="PriceIndexService"/> rather than
    /// splitting on commas: several hundred product names contain a comma inside a
    /// quoted field (e.g. "... El Blender Seti Siyah, Gri"), which a naive split corrupts.
    /// </remarks>
    private static (List<PriceComparisonRow> Rows, int DuplicatesSkipped) ReadInputCsv(Stream stream)
    {
        var selectedRows = new Dictionary<string, PriceComparisonRow>(StringComparer.OrdinalIgnoreCase);
        int duplicatesSkipped = 0;

        // detectEncodingFromByteOrderMarks strips the UTF-8 BOM this export carries.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
            return ([], 0);

        var delimiter = PriceIndexService.DetectCsvDelimiter(headerLine);
        var headerFields = PriceIndexService.ParseCsvLine(headerLine, delimiter);

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headerFields.Length; i++)
        {
            var name = headerFields[i].Trim();
            if (!string.IsNullOrWhiteSpace(name) && !headers.ContainsKey(name))
                headers[name] = i;
        }

        foreach (var required in RequiredColumns)
        {
            if (!headers.ContainsKey(required))
                throw new InvalidOperationException($"Required column '{required}' not found in the CSV.");
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = PriceIndexService.ParseCsvLine(line, delimiter);

            string Get(string column) =>
                headers.TryGetValue(column, out var idx) && idx < fields.Length
                    ? fields[idx].Trim()
                    : string.Empty;

            var productName = Get("Product Name");
            if (string.IsNullOrWhiteSpace(productName)) continue;

            var row = BuildRow(Get, productName);
            AddOrReplace(selectedRows, row, ref duplicatesSkipped);
        }

        return (OrderRows(selectedRows), duplicatesSkipped);
    }

    /// <summary>
    /// Build a comparison row from a column accessor, shared by the CSV and Excel readers.
    /// </summary>
    private static PriceComparisonRow BuildRow(Func<string, string> get, string productName)
    {
        var (price, isStockOut) = ParsePrice(get("Offer Total Price"));
        var sellerName = get("Seller Name");

        return new PriceComparisonRow
        {
            OfferId = get("Offer id"),
            FocusCategory = get("Focus Category"),
            CategoryLabel = get("Category Label"),
            Gtin = get("gtin"),
            SourceProductId = get("Product id"),
            SourceProductBrand = get("Product Brand"),
            SearchName = productName,
            TotalActiveOffers = get("Total Active Offers"),
            SourceStock = get("Stock"),
            WinnerAssortmentType = get("Winner Assortment Type"),
            MyPrice = price,
            IsStockOut = isStockOut,
            OfferScoreRank = get("Offer Score Rank"),
            SourceSellerName = sellerName,
            CsvCheapestSeller = sellerName,
            ProductSoldItems30d = get("Product - Sold items (30d)"),
            ProductGmvInclShipping30d = get("Product - GMV incl. Shipping (30d)"),
            SessionsByProductWithPdp30d = get("Sessions by Product with PDP (30d)"),
            SessionsByProductWithAddToCartInPdp30d = get("Sessions by Product with Add to Cart in pdp (30d)")
        };
    }

    /// <summary>
    /// Collapse the offer-level input to one row per product, keeping the cheapest
    /// in-stock offer. That surviving offer is what "our marketplace price" means.
    /// </summary>
    private static void AddOrReplace(
        Dictionary<string, PriceComparisonRow> selected,
        PriceComparisonRow row,
        ref int duplicatesSkipped)
    {
        var key = BuildDedupeKey(row);

        if (selected.TryGetValue(key, out var existing))
        {
            duplicatesSkipped++;
            if (ShouldReplace(existing, row))
                selected[key] = row;
            return;
        }

        selected[key] = row;
    }

    private static List<PriceComparisonRow> OrderRows(Dictionary<string, PriceComparisonRow> selected) =>
        selected.Values
            .OrderBy(r => r.SearchName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly string[] RequiredColumns =
    [
        "Offer id", "Focus Category", "Category Label", "gtin", "Product id",
        "Product Brand", "Product Name", "Total Active Offers", "Stock",
        "Winner Assortment Type", "Offer Total Price", "Offer Score Rank", "Seller Name",
        "Product - Sold items (30d)", "Product - GMV incl. Shipping (30d)",
        "Sessions by Product with PDP (30d)",
        "Sessions by Product with Add to Cart in pdp (30d)"
    ];

    // =========================
    // Excel reading
    // =========================

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
            var headers = GetHeaderMap(ws);

            foreach (var required in RequiredColumns)
            {
                if (!headers.ContainsKey(required))
                    throw new InvalidOperationException($"Required column '{required}' not found.");
            }

            var selectedRows = new Dictionary<string, PriceComparisonRow>(StringComparer.OrdinalIgnoreCase);

            for (int r = 2; r <= rowCount; r++)
            {
                int currentRow = r;
                string Get(string column) =>
                    headers.TryGetValue(column, out var col) ? GetCell(ws, currentRow, col) : string.Empty;

                var productName = Get("Product Name");
                if (string.IsNullOrWhiteSpace(productName))
                    continue;

                AddOrReplace(selectedRows, BuildRow(Get, productName), ref duplicatesSkipped);
            }

            result = OrderRows(selectedRows);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PriceCompV2] Excel read error: {ex.Message}");
        }

        return (result, duplicatesSkipped);
    }

    private static string BuildDedupeKey(PriceComparisonRow row)
    {
        var gtin = NormalizeGtin(row.Gtin);
        if (!string.IsNullOrWhiteSpace(gtin))
            return $"gtin:{gtin}";

        var brand = NormalizeText(row.SourceProductBrand);
        var title = NormalizeText(row.SearchName);
        var models = ExtractModelTokens(title);
        var modelPart = models.Count > 0 ? string.Join("|", models.OrderBy(x => x)) : "-";

        return $"brand:{brand}|title:{title}|models:{modelPart}";
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

    private static Dictionary<string, int> GetHeaderMap(ExcelWorksheet ws)
    {
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var colCount = ws.Dimension?.Columns ?? 0;

        for (int c = 1; c <= colCount; c++)
        {
            var h = ws.Cells[1, c].Value?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(h) && !headers.ContainsKey(h))
                headers[h] = c;
        }

        return headers;
    }

    private sealed record ProductFingerprint(
        string OriginalTitle,
        string NormalizedTitle,
        string Brand,
        string Gtin,
        string Category,
        HashSet<string> Tokens,
        HashSet<string> ModelTokens,
        Dictionary<string, string> Attributes);

    private sealed record SearchCandidateInfo(
        string Query,
        string Title,
        string Url,
        decimal ListingPrice);

    private sealed record ScoredCandidate(
        SearchCandidateInfo Candidate,
        int Score,
        int TokenOverlapCount,
        List<string> Reasons);

    private sealed record ValidatedCandidate(
        SearchCandidateInfo Candidate,
        int Score,
        List<string> Reasons,
        AkakceProductInfo? Product);
}