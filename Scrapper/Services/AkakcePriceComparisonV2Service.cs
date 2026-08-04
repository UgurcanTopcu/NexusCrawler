using OfficeOpenXml;
using Scrapper.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        if (!string.IsNullOrWhiteSpace(sessionId))
            _sessions[sessionId!] = cts;

        var rows = new List<PriceComparisonRow>();

        try
        {
            await onProgress(1, "Reading Excel file...", "info");

            var readResult = ReadInputExcel(excelStream);
            var inputRows = readResult.Rows;
            int duplicatesSkipped = readResult.DuplicatesSkipped;

            if (inputRows.Count == 0)
            {
                await onProgress(100, "No products found in the Excel file", "warning");
                await SendComplete(onProgress, null, 0);
                return;
            }

            var dupMsg = duplicatesSkipped > 0 ? $" ({duplicatesSkipped} duplicate row(s) skipped)" : "";
            await onProgress(4, $"Found {inputRows.Count} unique products{dupMsg}", "success");

            await onProgress(5, "Warming up Akakce Selenium search...", "info");

            using var scraper = new AkakceScraper();
            var warmupSuccess = await scraper.WarmupAsync(onProgress);

            if (!warmupSuccess)
            {
                await onProgress(100, "Could not connect to Edge browser.", "error");
                await SendComplete(onProgress, null, 0);
                return;
            }

            await onProgress(8, "Starting product matching...", "info");

            int matchedCount = 0;
            int unmatchedCount = 0;
            int searchFailureCount = 0;
            int detailFailureCount = 0;

            double progressBase = 10.0;
            double progressPerRow = 84.0 / inputRows.Count;

            for (int i = 0; i < inputRows.Count; i++)
            {
                if (cts.Token.IsCancellationRequested)
                    break;

                var row = inputRows[i];
                var pct = (int)Math.Min(94, progressBase + (i * progressPerRow));

                try
                {
                    await onProgress(
                        pct,
                        $"[{i + 1}/{inputRows.Count}] Matching: {Truncate(row.SearchName, 70)}",
                        "info");

                    var fingerprint = BuildFingerprint(row);
                    var queries = BuildSearchQueries(row, fingerprint);

                    if (queries.Count == 0)
                    {
                        row.ErrorMessage = "No usable search query could be built";
                        rows.Add(row);
                        unmatchedCount++;
                        continue;
                    }

                    var listingCandidates = await SearchAndScoreCandidatesAsync(
                        scraper,
                        row,
                        fingerprint,
                        queries,
                        onProgress,
                        pct,
                        cts.Token);

                    if (listingCandidates.Count == 0)
                    {
                        row.ErrorMessage = "No relevant search candidates found";
                        rows.Add(row);
                        unmatchedCount++;
                        searchFailureCount++;
                        continue;
                    }

                    var shortlisted = listingCandidates
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.TokenOverlapCount)
                        .Take(TOP_CANDIDATES_TO_VALIDATE)
                        .ToList();

                    await onProgress(
                        pct,
                        $"Validating top {shortlisted.Count} candidate(s) via detail pages...",
                        "info");

                    var validated = new List<ValidatedCandidate>();

                    foreach (var candidate in shortlisted)
                    {
                        if (cts.Token.IsCancellationRequested)
                            break;

                        try
                        {
                            var product = await _scrapeDoService.ScrapeProductAsync(candidate.Candidate.Url);

                            if (product == null)
                            {
                                validated.Add(new ValidatedCandidate(
                                    candidate.Candidate,
                                    candidate.Score - 6,
                                    candidate.Reasons.Append("Detail page returned null").ToList(),
                                    null));

                                detailFailureCount++;
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

                            detailFailureCount++;
                        }

                        try
                        {
                            await Task.Delay(SCRAPEDO_DELAY_MS, cts.Token);
                        }
                        catch (TaskCanceledException)
                        {
                            break;
                        }
                    }

                    if (cts.Token.IsCancellationRequested)
                    {
                        row.ErrorMessage = "Cancelled";
                        rows.Add(row);
                        break;
                    }

                    if (validated.Count == 0)
                    {
                        row.ErrorMessage = "No candidate detail pages could be validated";
                        rows.Add(row);
                        unmatchedCount++;
                        continue;
                    }

                    var orderedValidated = validated
                        .OrderByDescending(x => x.Score)
                        .ToList();

                    var best = orderedValidated[0];
                    var second = orderedValidated.Count > 1 ? orderedValidated[1] : null;

                    if (IsConfidentMatch(best, second))
                    {
                        ApplyAcceptedMatch(row, best);
                        rows.Add(row);
                        matchedCount++;

                        await onProgress(
                            pct,
                            $"Matched: {Truncate(best.Product?.Name ?? best.Candidate.Title, 70)} [score={best.Score}]",
                            "success");
                    }
                    else
                    {
                        row.ErrorMessage = BuildNoConfidenceMessage(orderedValidated);
                        rows.Add(row);
                        unmatchedCount++;

                        await onProgress(
                            pct,
                            $"No confident match: {Truncate(row.SearchName, 60)}",
                            "warning");
                    }
                }
                catch (Exception ex)
                {
                    row.ErrorMessage = ex.Message;
                    rows.Add(row);
                    unmatchedCount++;

                    Console.WriteLine($"[PriceCompV2] Row failed for '{row.SearchName}': {ex.Message}");
                }
            }

            foreach (var row in inputRows)
            {
                if (!rows.Contains(row))
                {
                    row.ErrorMessage = "Cancelled";
                    rows.Add(row);
                }
            }

            await onProgress(
                95,
                $"Creating comparison Excel report... Matched={matchedCount}, Unmatched={unmatchedCount}",
                "info");

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"AkakcePriceComparison_{timestamp}.xlsx";
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

            var exporter = new AkakcePriceComparisonExcelExporter();
            exporter.Export(rows, filePath);

            var done = rows.Count(r => r.IsSuccess);
            var failed = rows.Count - done;

            await onProgress(
                100,
                $"Done! {done} matched, {failed} unmatched/failed. SearchFailures={searchFailureCount}, DetailFailures={detailFailureCount}",
                "success");

            await SendComplete(onProgress, fileName, done);
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
        AkakceScraper scraper,
        PriceComparisonRow row,
        ProductFingerprint fingerprint,
        List<string> queries,
        Func<int, string, string, Task> onProgress,
        int pct,
        CancellationToken cancellationToken)
    {
        var aggregated = new Dictionary<string, ScoredCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await onProgress(pct, $"Searching Akakce with query: {Truncate(query, 80)}", "info");

            List<(string Title, string Url, decimal ListingPrice)> rawCandidates;
            try
            {
                rawCandidates = await scraper.SearchProductCandidatesAsync(query, MAX_SEARCH_RESULTS_PER_QUERY);
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
                detailReasons.Add($"Model confirmed on detail page: {string.Join(", ", modelOverlap)}");
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

        if (best.Score < ACCEPT_SCORE_THRESHOLD)
            return false;

        if (second == null)
            return true;

        if ((best.Score - second.Score) >= ACCEPT_SCORE_LEAD)
            return true;

        var strongSignals = best.Reasons.Count(r =>
            r.Contains("Brand match", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("Model match", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("Strong title similarity", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("confirmed on detail page", StringComparison.OrdinalIgnoreCase));

        var hardConflicts = best.Reasons.Count(r =>
            r.Contains("mismatch", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("differs", StringComparison.OrdinalIgnoreCase));

        return best.Score >= 70 && strongSignals >= 2 && hardConflicts == 0;
    }

    private static void ApplyAcceptedMatch(PriceComparisonRow row, ValidatedCandidate best)
    {
        var product = best.Product!;
        row.AkakceName = product.Name;
        row.AkakceUrl = product.ProductUrl;
        row.ErrorMessage = string.Empty;

        CollectMarketplacePrices(product, row);
    }

    private static string BuildNoConfidenceMessage(List<ValidatedCandidate> validated)
    {
        var ordered = validated
            .OrderByDescending(x => x.Score)
            .Take(3)
            .Select(x => $"[{x.Score}] {Truncate(x.Product?.Name ?? x.Candidate.Title, 55)}")
            .ToList();

        return ordered.Count == 0
            ? "No confident match"
            : $"No confident match. Top candidates: {string.Join(" | ", ordered)}";
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

        // 1) Brand + model tokens
        if (!string.IsNullOrWhiteSpace(row.SourceProductBrand) && fp.ModelTokens.Count > 0)
        {
            var modelPart = string.Join(" ", fp.ModelTokens.Take(2));
            var attrPart = string.Join(" ", fp.Attributes.Values.Take(2));
            Add($"{row.SourceProductBrand} {modelPart} {attrPart}".Trim());
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
            var columnMap = GetColumnMap(ws);
            var selectedRows = new Dictionary<string, PriceComparisonRow>(StringComparer.OrdinalIgnoreCase);

            for (int r = 2; r <= rowCount; r++)
            {
                var productName = GetCell(ws, r, columnMap.ProductName);
                if (string.IsNullOrWhiteSpace(productName))
                    continue;

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

                var dedupeKey = BuildDedupeKey(row);

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
        h.TryGetValue(name, out var c)
            ? c
            : throw new InvalidOperationException($"Required column '{name}' not found.");

    private readonly record struct ColumnMap(
        int OfferId, int FocusCategory, int CategoryLabel, int Gtin,
        int ProductId, int ProductBrand, int ProductName,
        int TotalActiveOffers, int Stock, int WinnerAssortmentType,
        int OfferTotalPrice, int OfferScoreRank, int SellerName,
        int ProductSoldItems30d, int ProductGmvInclShipping30d,
        int SessionsByProductWithPdp30d, int SessionsByProductWithAddToCartInPdp30d);

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