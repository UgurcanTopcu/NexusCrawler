using Scrapper.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Scrapper.Services;

public class AkakceScraperService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    private static readonly Random _random = new Random();

    private readonly AkakceScrapeDoService _scrapeDoService;

    private const int MIN_DELAY_BETWEEN_PRODUCTS_MS = 2000;
    private const int MAX_DELAY_BETWEEN_PRODUCTS_MS = 4000;
    private const int MAX_PRODUCT_RETRIES = 2;
    private const int CLOUDFLARE_COOLDOWN_MS = 30000;

    public AkakceScraperService(AkakceScrapeDoService scrapeDoService)
    {
        _scrapeDoService = scrapeDoService;
    }

    public static void StopSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var cts))
            cts.Cancel();
    }
    
    public async Task ProcessCategoryUrlAsync(
        string categoryUrl,
        int maxProducts,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null,
        int startFrom = 1,
        bool scanVariants = false,
        int maxSellersPerProduct = 0,
        bool includePreferredMarketplaceMatches = false,
        string? preferredMarketplaces = null,
        bool useScrapeDoForProductPages = false)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;
        
        var products = new List<AkakceProductInfo>();
        AkakceScraper? scraper = null;
        
        try
        {
            var preferredMarketplaceList = ParsePreferredMarketplaces(preferredMarketplaces);
            if (includePreferredMarketplaceMatches && preferredMarketplaceList.Count == 0)
            {
                preferredMarketplaceList = ParsePreferredMarketplaces("Trendyol, Hepsiburada, n11");
            }

            await onProgress(1, "🔍 Starting Akakce category scraper...", "info");
            await onProgress(2, $"🌐 URL: {categoryUrl}", "info");
            await onProgress(3, $"🎯 Target: {maxProducts} products", "info");
            
            if (scanVariants)
            {
                await onProgress(4, "⚠️ Variant scanning enabled - this will take significantly longer", "info");
            }
            
            if (startFrom > 1)
            {
                await onProgress(4, $"⏭️ Starting from product #{startFrom} (skipping first {startFrom - 1})", "info");
            }
            else
            {
                await onProgress(4, "⚠️ Note: Akakce has Cloudflare protection. Delays added between products.", "info");
            }

            if (maxSellersPerProduct > 0)
            {
                await onProgress(5, $"🏷️ Seller limit enabled: first {maxSellersPerProduct} seller(s) per product", "info");
            }

            if (includePreferredMarketplaceMatches && preferredMarketplaceList.Count > 0)
            {
                await onProgress(6, $"🔎 Preferred marketplace match enabled: {string.Join(", ", preferredMarketplaceList)}", "info");
            }

            if (useScrapeDoForProductPages)
            {
                await onProgress(6, "⚡ Scrape.do enabled for product pages — no Selenium / no Cloudflare delays", "info");
            }

            scraper = new AkakceScraper();
            scraper.Method = ScrapeMethod.Selenium;
            
            // Step 1: Extract product URLs from category page
            // Need to fetch enough URLs to cover both skipped and target products
            int totalUrlsNeeded = startFrom + maxProducts - 1;
            var productUrls = await scraper.GetProductUrlsFromCategoryAsync(categoryUrl, totalUrlsNeeded, onProgress);
            
            if (productUrls.Count == 0)
            {
                await onProgress(100, "❌ No product URLs found on the category page", "error");
                await SendComplete(onProgress, null, null);
                return;
            }

            // Validate startFrom parameter
            if (startFrom > productUrls.Count)
            {
                await onProgress(100, $"❌ Start position ({startFrom}) exceeds available products ({productUrls.Count})", "error");
                await SendComplete(onProgress, null, null);
                return;
            }

            // Skip products before startFrom
            var urlsToScrape = productUrls.Skip(startFrom - 1).Take(maxProducts).ToList();
            
            await onProgress(15, $"✅ Found {productUrls.Count} total products. Will scrape {urlsToScrape.Count} starting from #{startFrom}", "success");

            // Only wait if we're scraping many products (potential Cloudflare trigger) — skip when using Scrape.do
            if (!useScrapeDoForProductPages && urlsToScrape.Count > 20)
            {
                int initialWaitSeconds = 5;
                await onProgress(18, $"⏳ Waiting {initialWaitSeconds}s before starting (large batch)...", "info");
                await Task.Delay(initialWaitSeconds * 1000);
            }

            // Step 2: Scrape each product with significant delays and skip logic
            var progressPerProduct = 75.0 / urlsToScrape.Count;
            var currentProgress = 20.0;

            int successCount = 0;
            int errorCount = 0;
            int skippedCount = 0;
            
            for (int i = 0; i < urlsToScrape.Count; i++)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, $"⏹️ Stopped at product {startFrom + i}/{productUrls.Count}", "warning");
                    break;
                }
                
                var url = urlsToScrape[i];
                int absoluteProductNumber = startFrom + i;
                
                // Selenium mode: add random delay between requests to avoid Cloudflare
                if (!useScrapeDoForProductPages && i > 0)
                {
                    var delayMs = _random.Next(MIN_DELAY_BETWEEN_PRODUCTS_MS, MAX_DELAY_BETWEEN_PRODUCTS_MS);
                    await onProgress((int)currentProgress, $"⏱️ Waiting {delayMs/1000}s to avoid Cloudflare...", "info");
                    await Task.Delay(delayMs);
                }
                
                await onProgress((int)currentProgress, $"📦 Scraping product #{absoluteProductNumber} ({i + 1}/{urlsToScrape.Count})...", "info");
                
                // Retry logic with automatic skip
                bool productScraped = false;
                AkakceProductInfo? product = null;
                int retryCount = 0;
                
                while (!productScraped && retryCount <= MAX_PRODUCT_RETRIES)
                {
                    try
                    {
                        product = useScrapeDoForProductPages
                            ? await _scrapeDoService.ScrapeProductAsync(url)
                            : await scraper.ScrapeProductAsync(url, scanVariants);
                        
                        if (product.IsSuccess)
                        {
                            ApplySellerSelectionOptions(product, maxSellersPerProduct, includePreferredMarketplaceMatches, preferredMarketplaceList);
                            successCount++;
                            
                            if (product.HasVariants)
                            {
                                var totalSellers = product.Variants.Sum(v => v.SellerCount);
                                await onProgress((int)currentProgress, $"✅ {product.Name}: {product.Variants.Count} variants, {totalSellers} sellers", "success");
                            }
                            else
                            {
                                var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 40
                                    ? product.Name.Substring(0, 40) + "..."
                                    : product.Name ?? "Unknown";
                                
                                await onProgress((int)currentProgress, $"✅ {displayName} ({product.SellerCount} sellers)", "success");
                            }
                            productScraped = true;
                        }
                        else
                        {
                            // Check if it's a Cloudflare block
                            if (product.ErrorMessage?.Contains("Cloudflare") == true)
                            {
                                retryCount++;
                                
                                if (retryCount <= MAX_PRODUCT_RETRIES)
                                {
                                    await onProgress((int)currentProgress, 
                                        $"🔄 Cloudflare block - retry {retryCount}/{MAX_PRODUCT_RETRIES} in 30s...", 
                                        "warning");
                                    await Task.Delay(CLOUDFLARE_COOLDOWN_MS);
                                }
                                else
                                {
                                    // Max retries reached - skip this product
                                    skippedCount++;
                                    await onProgress((int)currentProgress, 
                                        $"⏭️ Skipping product #{absoluteProductNumber} after {MAX_PRODUCT_RETRIES} Cloudflare blocks", 
                                        "warning");
                                    productScraped = true; // Exit retry loop
                                }
                            }
                            else
                            {
                                // Other error - count and skip
                                errorCount++;
                                await onProgress((int)currentProgress, $"❌ {product.ErrorMessage}", "error");
                                productScraped = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        
                        if (retryCount <= MAX_PRODUCT_RETRIES)
                        {
                            await onProgress((int)currentProgress, 
                                $"🔄 Error: {ex.Message} - retry {retryCount}/{MAX_PRODUCT_RETRIES}...", 
                                "warning");
                            await Task.Delay(5000); // Short delay before retry
                        }
                        else
                        {
                            // Max retries reached - create error product and skip
                            errorCount++;
                            skippedCount++;
                            product = new AkakceProductInfo
                            {
                                ProductUrl = url,
                                ErrorMessage = $"Skipped after {MAX_PRODUCT_RETRIES} retries: {ex.Message}",
                                ScrapedAt = DateTime.Now
                            };
                            await onProgress((int)currentProgress, 
                                $"⏭️ Skipping product #{absoluteProductNumber} after multiple errors", 
                                "warning");
                            productScraped = true;
                        }
                    }
                }
                
                // Add product to list (even if failed/skipped for reporting)
                if (product != null)
                {
                    product.CategoryName = GetCategoryFileLabel(categoryUrl);
                    product.SourceCategoryUrl = categoryUrl;
                    SetBrandFromName(product);
                    products.Add(product);
                }
                
                currentProgress += progressPerProduct;
            }
            
            // Step 3: Export results
            if (products.Count > 0)
            {
                var stoppedText = cts.Token.IsCancellationRequested ? " (stopped early)" : "";
                await onProgress(95, $"📊 Creating Excel report{stoppedText}...", "info");
                
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var categoryFileLabel = GetCategoryFileLabel(categoryUrl);
                var fileName = $"Akakce_{categoryFileLabel}_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                
                try
                {
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(products, filePath);
                    
                    var totalSellers = products.Sum(p => p.SellerCount);
                    
                    var summary = $"✅ {successCount} products, {totalSellers} sellers scraped. " +
                                 $"{errorCount} errors. {skippedCount} skipped.";
                    
                    if (startFrom > 1)
                    {
                        summary += $" (Started from #{startFrom})";
                    }
                    
                    await onProgress(100, summary, "success");
                    await SendComplete(onProgress, fileName, successCount);
                }
                catch (Exception excelEx)
                {
                    await onProgress(100, $"❌ Excel error: {excelEx.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, "❌ No products scraped", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        catch (Exception ex)
        {
            // Try to save what we have
            if (products.Count > 0)
            {
                try
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var categoryFileLabel = GetCategoryFileLabel(categoryUrl);
                    var fileName = $"Akakce_{categoryFileLabel}_Partial_{timestamp}.xlsx";
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(products, filePath);
                    
                    await onProgress(100, $"⚠️ Error but saved {products.Count} products", "warning");
                    await SendComplete(onProgress, fileName, products.Count);
                }
                catch
                {
                    await onProgress(100, $"❌ Error: {ex.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, $"❌ Error: {ex.Message}", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        finally
        {
            scraper?.Dispose();
            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessions.TryRemove(sessionId, out _);
            }
            cts.Dispose();
        }
    }

    private static void SetBrandFromName(AkakceProductInfo product)
    {
        if (string.IsNullOrWhiteSpace(product.Name))
        {
            return;
        }

        var firstWord = product.Name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstWord))
        {
            product.Brand = firstWord;
        }
    }

    /// <summary>
    /// Process multiple URL groups on the server, producing one Excel per group.
    /// All groups are handled inside a single HTTP request so the browser's JS never
    /// needs to loop — Windows lock-screen throttling cannot interrupt the sequence.
    /// </summary>
    public async Task ProcessSeparateCategoryGroupsAsync(
        string[][] urlGroups,
        int maxProducts,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null,
        int startFrom = 1,
        bool scanVariants = false,
        int maxSellersPerProduct = 0,
        bool includePreferredMarketplaceMatches = false,
        string? preferredMarketplaces = null,
        bool useScrapeDoForProductPages = false)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;

        AkakceScraper? scraper = null;

        try
        {
            var preferredMarketplaceList = ParsePreferredMarketplaces(preferredMarketplaces);
            if (includePreferredMarketplaceMatches && preferredMarketplaceList.Count == 0)
                preferredMarketplaceList = ParsePreferredMarketplaces("Trendyol, Hepsiburada, n11");

            await onProgress(1, $"🗂️ Processing {urlGroups.Length} group(s) — server-side loop (screen lock safe)", "info");

            if (useScrapeDoForProductPages)
            {
                await onProgress(2, "⚡ Scrape.do enabled for product pages — no Selenium / no Cloudflare delays", "info");
            }

            scraper = new AkakceScraper();
            scraper.Method = ScrapeMethod.Selenium;

            double progressPerGroup = 94.0 / urlGroups.Length;
            double groupProgressStart = 3.0;
            int totalFilesCreated = 0;

            for (int gIdx = 0; gIdx < urlGroups.Length; gIdx++)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    await onProgress((int)groupProgressStart, $"⏹️ Stopped after {gIdx}/{urlGroups.Length} groups", "warning");
                    break;
                }

                var groupUrls = urlGroups[gIdx];
                if (groupUrls.Length == 0) { groupProgressStart += progressPerGroup; continue; }

                // Use first URL as label for the Excel filename
                var groupLabel = groupUrls.Length == 1
                    ? GetCategoryFileLabel(groupUrls[0])
                    : $"Group{gIdx + 1}_{GetCategoryFileLabel(groupUrls[0])}";

                await onProgress((int)groupProgressStart,
                    $"━━━ Group {gIdx + 1}/{urlGroups.Length}: {groupLabel} ({groupUrls.Length} URL(s)) ━━━", "info");

                var groupProducts = new List<AkakceProductInfo>();
                double progressPerUrl = progressPerGroup / groupUrls.Length;
                double urlProgressStart = groupProgressStart;

                foreach (var categoryUrl in groupUrls)
                {
                    if (cts.Token.IsCancellationRequested) break;

                    await onProgress((int)urlProgressStart, $"🌐 {categoryUrl}", "info");

                    try
                    {
                        int totalUrlsNeeded = startFrom + maxProducts - 1;
                        var productUrls = await scraper.GetProductUrlsFromCategoryAsync(categoryUrl, totalUrlsNeeded, onProgress);

                        if (productUrls.Count == 0)
                        {
                            await onProgress((int)urlProgressStart, $"⚠️ No products found for {GetCategoryFileLabel(categoryUrl)}", "warning");
                            urlProgressStart += progressPerUrl;
                            continue;
                        }

                        var urlsToScrape = productUrls.Skip(startFrom - 1).Take(maxProducts).ToList();
                        await onProgress((int)urlProgressStart, $"✅ Found {productUrls.Count} products. Scraping {urlsToScrape.Count}...", "success");

                        double progressPerProduct = progressPerUrl / urlsToScrape.Count;
                        double currentProgress = urlProgressStart;
                        int successCount = 0;

                        for (int i = 0; i < urlsToScrape.Count; i++)
                        {
                            if (cts.Token.IsCancellationRequested) break;

                            if (!useScrapeDoForProductPages && i > 0)
                            {
                                var delayMs = _random.Next(MIN_DELAY_BETWEEN_PRODUCTS_MS, MAX_DELAY_BETWEEN_PRODUCTS_MS);
                                await Task.Delay(delayMs);
                            }

                            var url = urlsToScrape[i];
                            await onProgress((int)currentProgress, $"📦 [{groupLabel}] #{startFrom + i} ({i + 1}/{urlsToScrape.Count})", "info");

                            AkakceProductInfo? product = null;
                            bool done = false;
                            int retryCount = 0;

                            while (!done && retryCount <= MAX_PRODUCT_RETRIES)
                            {
                                try
                                {
                                    product = useScrapeDoForProductPages
                                        ? await _scrapeDoService.ScrapeProductAsync(url)
                                        : await scraper.ScrapeProductAsync(url, scanVariants);

                                    if (product.IsSuccess)
                                    {
                                        ApplySellerSelectionOptions(product, maxSellersPerProduct, includePreferredMarketplaceMatches, preferredMarketplaceList);
                                        successCount++;
                                        var name = product.Name is { Length: > 40 } n ? n[..40] + "..." : product.Name ?? "?";
                                        await onProgress((int)currentProgress, $"✅ {name} ({product.SellerCount} sellers)", "success");
                                        done = true;
                                    }
                                    else if (product.ErrorMessage?.Contains("Cloudflare") == true)
                                    {
                                        retryCount++;
                                        if (retryCount <= MAX_PRODUCT_RETRIES)
                                        {
                                            await onProgress((int)currentProgress, $"🔄 Cloudflare block - retry {retryCount}/{MAX_PRODUCT_RETRIES} in 30s...", "warning");
                                            await Task.Delay(CLOUDFLARE_COOLDOWN_MS);
                                        }
                                        else
                                        {
                                            await onProgress((int)currentProgress, $"⏭️ Skipping product after Cloudflare blocks", "warning");
                                            done = true;
                                        }
                                    }
                                    else
                                    {
                                        await onProgress((int)currentProgress, $"❌ {product.ErrorMessage}", "error");
                                        done = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    retryCount++;
                                    if (retryCount <= MAX_PRODUCT_RETRIES)
                                    {
                                        await onProgress((int)currentProgress, $"🔄 Error: {ex.Message} - retry {retryCount}/{MAX_PRODUCT_RETRIES}...", "warning");
                                        await Task.Delay(5000);
                                    }
                                    else
                                    {
                                        product = new AkakceProductInfo { ProductUrl = url, ErrorMessage = $"Skipped: {ex.Message}", ScrapedAt = DateTime.Now };
                                        done = true;
                                    }
                                }
                            }

                            if (product != null)
                            {
                                product.CategoryName = GetCategoryFileLabel(categoryUrl);
                                SetBrandFromName(product);
                                groupProducts.Add(product);
                            }

                            currentProgress += progressPerProduct;
                        }
                    }
                    catch (Exception urlEx)
                    {
                        await onProgress((int)urlProgressStart, $"❌ URL failed: {urlEx.Message}", "error");
                    }

                    urlProgressStart += progressPerUrl;
                }

                // Export this group's Excel
                if (groupProducts.Count > 0)
                {
                    try
                    {
                        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        var fileName = $"Akakce_{groupLabel}_{timestamp}.xlsx";
                        var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                        new AkakceExcelExporter().Export(groupProducts, filePath);

                        totalFilesCreated++;
                        var totalSellers = groupProducts.Sum(p => p.SellerCount);
                        await onProgress((int)(groupProgressStart + progressPerGroup),
                            $"📥 Group {gIdx + 1} done: {groupProducts.Count} products, {totalSellers} sellers", "success");

                        // Emit download link for this group without closing the stream
                        var downloadData = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            complete = false,
                            downloadUrl = $"/api/download/{fileName}",
                            fileName,
                            groupIndex = gIdx + 1,
                            productCount = groupProducts.Count
                        });
                        await onProgress((int)(groupProgressStart + progressPerGroup), downloadData, "download");
                    }
                    catch (Exception excelEx)
                    {
                        await onProgress((int)(groupProgressStart + progressPerGroup), $"❌ Excel error for group {gIdx + 1}: {excelEx.Message}", "error");
                    }
                }
                else
                {
                    await onProgress((int)(groupProgressStart + progressPerGroup), $"⚠️ Group {gIdx + 1}: no products scraped", "warning");
                }

                groupProgressStart += progressPerGroup;
            }

            await onProgress(100, $"🏁 All {urlGroups.Length} groups processed — {totalFilesCreated} file(s) created", "success");
            // Final complete event closes the stream
            await SendComplete(onProgress, null, totalFilesCreated);
        }
        catch (Exception ex)
        {
            await onProgress(100, $"❌ Error: {ex.Message}", "error");
            await SendComplete(onProgress, null, null);
        }
        finally
        {
            scraper?.Dispose();
            if (!string.IsNullOrEmpty(sessionId))
                _sessions.TryRemove(sessionId, out _);
            cts.Dispose();
        }
    }

    internal static string GetCategoryFileLabel(string categoryUrl)
    {
        if (string.IsNullOrWhiteSpace(categoryUrl))
        {
            return "Category";
        }

        if (!Uri.TryCreate(categoryUrl, UriKind.Absolute, out var categoryUri))
        {
            return "Category";
        }

        var rawSegment = categoryUri.Segments.LastOrDefault();
        if (string.IsNullOrWhiteSpace(rawSegment))
        {
            return "Category";
        }

        var categorySegment = Uri.UnescapeDataString(rawSegment.Trim('/'));
        var categoryName = Path.GetFileNameWithoutExtension(categorySegment)
            .Replace('-', ' ')
            .Trim();

        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return "Category";
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var sanitizedCategoryName = new string(categoryName
            .Select(character => invalidFileNameChars.Contains(character) ? ' ' : character)
            .ToArray());

        return string.Join(' ', sanitizedCategoryName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static void ApplySellerSelectionOptions(
        AkakceProductInfo product,
        int maxSellersPerProduct,
        bool includePreferredMarketplaceMatches,
        IReadOnlyCollection<string> preferredMarketplaces)
    {
        if (product.HasVariants)
        {
            foreach (var variant in product.Variants)
            {
                variant.Sellers = FilterSellers(variant.Sellers, maxSellersPerProduct, includePreferredMarketplaceMatches, preferredMarketplaces);
                UpdatePriceRange(variant.Sellers, price => variant.LowestPrice = price, price => variant.HighestPrice = price);
            }
        }

        product.Sellers = FilterSellers(product.Sellers, maxSellersPerProduct, includePreferredMarketplaceMatches, preferredMarketplaces);

        var allSellers = product.HasVariants
            ? product.Variants.SelectMany(v => v.Sellers).ToList()
            : product.Sellers;

        product.SellerCount = allSellers.Count;
        UpdatePriceRange(allSellers, price => product.LowestPrice = price, price => product.HighestPrice = price);
    }

    private static List<AkakceSellerInfo> FilterSellers(
        List<AkakceSellerInfo> sellers,
        int maxSellersPerProduct,
        bool includePreferredMarketplaceMatches,
        IReadOnlyCollection<string> preferredMarketplaces)
    {
        if (sellers.Count == 0)
        {
            return sellers;
        }

        var rankedSellers = sellers
            .OrderBy(s => s.Rank > 0 ? s.Rank : int.MaxValue)
            .ThenBy(s => s.Price)
            .ToList();

        if (includePreferredMarketplaceMatches && preferredMarketplaces.Count > 0)
        {
            rankedSellers = rankedSellers
                .Where(seller => MatchesPreferredMarketplace(seller, preferredMarketplaces))
                .ToList();
        }

        if (maxSellersPerProduct <= 0)
        {
            return rankedSellers;
        }

        return rankedSellers
            .Take(maxSellersPerProduct)
            .ToList();
    }

    private static List<string> ParsePreferredMarketplaces(string? preferredMarketplaces)
    {
        if (string.IsNullOrWhiteSpace(preferredMarketplaces))
        {
            return new List<string>();
        }

        return preferredMarketplaces
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesPreferredMarketplace(AkakceSellerInfo seller, IReadOnlyCollection<string> preferredMarketplaces)
    {
        var marketplace = NormalizeForMatch(seller.Marketplace);
        var sellerName = NormalizeForMatch(seller.SellerName);
        var productLink = NormalizeForMatch(seller.ProductLink);

        foreach (var preferredMarketplace in preferredMarketplaces)
        {
            var normalizedPreferred = NormalizeForMatch(preferredMarketplace);
            if (string.IsNullOrEmpty(normalizedPreferred))
            {
                continue;
            }

            if (marketplace.Contains(normalizedPreferred, StringComparison.Ordinal) ||
                sellerName.Contains(normalizedPreferred, StringComparison.Ordinal) ||
                productLink.Contains(normalizedPreferred, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(character);
            if (unicodeCategory == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character switch
                {
                    'ı' => 'i',
                    _ => character
                });
            }
        }

        return builder.ToString();
    }

    private static void UpdatePriceRange(
        IReadOnlyCollection<AkakceSellerInfo> sellers,
        Action<string> setLowestPrice,
        Action<string> setHighestPrice)
    {
        if (sellers.Count == 0)
        {
            setLowestPrice(string.Empty);
            setHighestPrice(string.Empty);
            return;
        }

        var lowest = sellers.MinBy(s => s.Price);
        var highest = sellers.MaxBy(s => s.Price);

        setLowestPrice(lowest?.PriceFormatted ?? string.Empty);
        setHighestPrice(highest?.PriceFormatted ?? string.Empty);
    }
    
    /// <summary>
    /// Process multiple category URLs and combine all results into a single Excel file
    /// </summary>
    public async Task ProcessBatchCategoryUrlsAsync(
        string[] categoryUrls,
        int maxProducts,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null,
        int startFrom = 1,
        bool scanVariants = false,
        int maxSellersPerProduct = 0,
        bool includePreferredMarketplaceMatches = false,
        string? preferredMarketplaces = null,
        bool useScrapeDoForProductPages = false)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;

        var allProducts = new List<AkakceProductInfo>();
        AkakceScraper? scraper = null;

        try
        {
            var preferredMarketplaceList = ParsePreferredMarketplaces(preferredMarketplaces);
            if (includePreferredMarketplaceMatches && preferredMarketplaceList.Count == 0)
            {
                preferredMarketplaceList = ParsePreferredMarketplaces("Trendyol, Hepsiburada, n11");
            }

            await onProgress(1, $"🔍 Starting batch Akakce scraper for {categoryUrls.Length} category URL(s)...", "info");

            if (scanVariants)
            {
                await onProgress(2, "⚠️ Variant scanning enabled - this will take significantly longer", "info");
            }

            if (useScrapeDoForProductPages)
            {
                await onProgress(2, "⚡ Scrape.do enabled for product pages — no Selenium / no Cloudflare delays", "info");
            }

            scraper = new AkakceScraper();
            scraper.Method = ScrapeMethod.Selenium;

            double progressPerCategory = 90.0 / categoryUrls.Length;
            double categoryProgressStart = 5.0;

            int totalSuccess = 0;
            int totalErrors = 0;
            int totalSkipped = 0;

            for (int catIdx = 0; catIdx < categoryUrls.Length; catIdx++)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    await onProgress((int)categoryProgressStart, $"⏹️ Stopped after {catIdx}/{categoryUrls.Length} categories", "warning");
                    break;
                }

                var categoryUrl = categoryUrls[catIdx];
                var categoryName = GetCategoryFileLabel(categoryUrl);

                await onProgress((int)categoryProgressStart, $"━━━ Category {catIdx + 1}/{categoryUrls.Length}: {categoryName} ━━━", "info");
                await onProgress((int)categoryProgressStart, $"🌐 URL: {categoryUrl}", "info");

                try
                {

                // Get product URLs from category page
                int totalUrlsNeeded = startFrom + maxProducts - 1;
                var productUrls = await scraper.GetProductUrlsFromCategoryAsync(categoryUrl, totalUrlsNeeded, onProgress);

                if (productUrls.Count == 0)
                {
                    await onProgress((int)categoryProgressStart, $"⚠️ No product URLs found for {categoryName}", "warning");
                    categoryProgressStart += progressPerCategory;
                    continue;
                }

                if (startFrom > productUrls.Count)
                {
                    await onProgress((int)categoryProgressStart, $"⚠️ Start position ({startFrom}) exceeds products ({productUrls.Count}) for {categoryName}", "warning");
                    categoryProgressStart += progressPerCategory;
                    continue;
                }

                var urlsToScrape = productUrls.Skip(startFrom - 1).Take(maxProducts).ToList();
                await onProgress((int)categoryProgressStart, $"✅ Found {productUrls.Count} products. Scraping {urlsToScrape.Count} from #{startFrom}", "success");

                if (urlsToScrape.Count > 20)
                {
                    if (!useScrapeDoForProductPages)
                    {
                        await onProgress((int)categoryProgressStart, "⏳ Waiting 5s before starting (large batch)...", "info");
                        await Task.Delay(5000);
                    }
                }

                double progressPerProduct = progressPerCategory / urlsToScrape.Count;
                double currentProgress = categoryProgressStart;

                for (int i = 0; i < urlsToScrape.Count; i++)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        await onProgress((int)currentProgress, $"⏹️ Stopped during {categoryName}", "warning");
                        break;
                    }

                    var url = urlsToScrape[i];
                    int absoluteProductNumber = startFrom + i;

                    if (!useScrapeDoForProductPages && i > 0)
                    {
                        var delayMs = _random.Next(MIN_DELAY_BETWEEN_PRODUCTS_MS, MAX_DELAY_BETWEEN_PRODUCTS_MS);
                        await onProgress((int)currentProgress, $"⏱️ Waiting {delayMs / 1000}s...", "info");
                        await Task.Delay(delayMs);
                    }

                    await onProgress((int)currentProgress, $"📦 [{categoryName}] Product #{absoluteProductNumber} ({i + 1}/{urlsToScrape.Count})...", "info");

                    bool productScraped = false;
                    AkakceProductInfo? product = null;
                    int retryCount = 0;

                    while (!productScraped && retryCount <= MAX_PRODUCT_RETRIES)
                    {
                        try
                        {
                            product = useScrapeDoForProductPages
                                ? await _scrapeDoService.ScrapeProductAsync(url)
                                : await scraper.ScrapeProductAsync(url, scanVariants);

                            if (product.IsSuccess)
                            {
                                ApplySellerSelectionOptions(product, maxSellersPerProduct, includePreferredMarketplaceMatches, preferredMarketplaceList);
                                totalSuccess++;

                                var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 40
                                    ? product.Name[..40] + "..."
                                    : product.Name ?? "Unknown";

                                await onProgress((int)currentProgress, $"✅ {displayName} ({product.SellerCount} sellers)", "success");
                                productScraped = true;
                            }
                            else if (product.ErrorMessage?.Contains("Cloudflare") == true)
                            {
                                retryCount++;
                                if (retryCount <= MAX_PRODUCT_RETRIES)
                                {
                                    await onProgress((int)currentProgress, $"🔄 Cloudflare block - retry {retryCount}/{MAX_PRODUCT_RETRIES} in 30s...", "warning");
                                    await Task.Delay(CLOUDFLARE_COOLDOWN_MS);
                                }
                                else
                                {
                                    totalSkipped++;
                                    await onProgress((int)currentProgress, $"⏭️ Skipping product #{absoluteProductNumber} after Cloudflare blocks", "warning");
                                    productScraped = true;
                                }
                            }
                            else
                            {
                                totalErrors++;
                                await onProgress((int)currentProgress, $"❌ {product.ErrorMessage}", "error");
                                productScraped = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            retryCount++;
                            if (retryCount <= MAX_PRODUCT_RETRIES)
                            {
                                await onProgress((int)currentProgress, $"🔄 Error: {ex.Message} - retry {retryCount}/{MAX_PRODUCT_RETRIES}...", "warning");
                                await Task.Delay(5000);
                            }
                            else
                            {
                                totalErrors++;
                                totalSkipped++;
                                product = new AkakceProductInfo
                                {
                                    ProductUrl = url,
                                    ErrorMessage = $"Skipped after {MAX_PRODUCT_RETRIES} retries: {ex.Message}",
                                    ScrapedAt = DateTime.Now
                                };
                                await onProgress((int)currentProgress, $"⏭️ Skipping product #{absoluteProductNumber} after errors", "warning");
                                productScraped = true;
                            }
                        }
                    }

                    if (product != null)
                    {
                        product.CategoryName = categoryName;
                        product.SourceCategoryUrl = categoryUrl;
                        SetBrandFromName(product);
                        allProducts.Add(product);
                    }

                    currentProgress += progressPerProduct;
                }

                }
                catch (Exception catEx)
                {
                    totalErrors++;
                    await onProgress((int)categoryProgressStart, $"❌ Category {categoryName} failed: {catEx.Message} — skipping to next", "error");
                }

                categoryProgressStart += progressPerCategory;
            }

            // Export combined results
            if (allProducts.Count > 0)
            {
                var stoppedText = cts.Token.IsCancellationRequested ? " (stopped early)" : "";
                await onProgress(95, $"📊 Creating combined Excel report{stoppedText}...", "info");

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileLabel = categoryUrls.Length == 1
                    ? GetCategoryFileLabel(categoryUrls[0])
                    : "Combined";
                var fileName = $"Akakce_{fileLabel}_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                try
                {
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(allProducts, filePath);

                    var totalSellers = allProducts.Sum(p => p.SellerCount);
                    var summary = $"✅ {categoryUrls.Length} categories, {totalSuccess} products, {totalSellers} sellers. " +
                                  $"{totalErrors} errors. {totalSkipped} skipped.";

                    await onProgress(100, summary, "success");
                    await SendComplete(onProgress, fileName, totalSuccess);
                }
                catch (Exception excelEx)
                {
                    await onProgress(100, $"❌ Excel error: {excelEx.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, "❌ No products scraped from any category", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        catch (Exception ex)
        {
            if (allProducts.Count > 0)
            {
                try
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var fileName = $"Akakce_Combined_Partial_{timestamp}.xlsx";
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(allProducts, filePath);

                    await onProgress(100, $"⚠️ Error but saved {allProducts.Count} products from partial run", "warning");
                    await SendComplete(onProgress, fileName, allProducts.Count);
                }
                catch
                {
                    await onProgress(100, $"❌ Error: {ex.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, $"❌ Error: {ex.Message}", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        finally
        {
            scraper?.Dispose();
            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessions.TryRemove(sessionId, out _);
            }
            cts.Dispose();
        }
    }

    public async Task ProcessExcelFileAsync(
        Stream excelStream,
        ScrapeMethod scrapeMethod,
        Func<int, string, string, Task> onProgress,
        string? sessionId = null,
        int startFrom = 1,
        bool scanVariants = false)
    {
        var cts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
            _sessions[sessionId] = cts;
        
        var products = new List<AkakceProductInfo>();
        AkakceScraper? scraper = null;
        
        try
        {
            // IMPORTANT: Akakce uses Cloudflare protection, so Scrape.do won't work!
            await onProgress(0, "⚠️ Akakce has Cloudflare protection. Using Selenium with delays.", "info");
            await onProgress(1, "Starting Akakce scraper (Selenium)...", "info");
            
            if (scanVariants)
            {
                await onProgress(2, "⚠️ Variant scanning enabled - this will take significantly longer", "info");
            }
            
            // Step 1: Read URLs from Excel
            await onProgress(2, "Reading URLs from Excel file...", "info");
            
            var reader = new AkakceExcelReader();
            excelStream.Position = 0;
            var urlColumn = reader.DetectUrlColumn(excelStream, hasHeader: true);
            excelStream.Position = 0;
            var urls = reader.ReadUrlsFromStream(excelStream, urlColumn, hasHeader: true);
            
            if (urls.Count == 0)
            {
                await onProgress(100, "No valid Akakce URLs found in the Excel file", "error");
                await SendComplete(onProgress, null, null);
                return;
            }

            // Validate startFrom parameter
            if (startFrom > urls.Count)
            {
                await onProgress(100, $"❌ Start position ({startFrom}) exceeds available URLs ({urls.Count})", "error");
                await SendComplete(onProgress, null, null);
                return;
            }

            if (urls.Count > 500)
            {
                await onProgress(5, $"Limiting to first 500 URLs", "info");
                urls = urls.Take(500).ToList();
            }

            // Skip URLs before startFrom
            var urlsToScrape = urls.Skip(startFrom - 1).ToList();
            
            if (startFrom > 1)
            {
                await onProgress(5, $"Found {urls.Count} valid Akakce URLs. Will scrape {urlsToScrape.Count} starting from row #{startFrom}", "success");
            }
            else
            {
                await onProgress(5, $"Found {urls.Count} valid Akakce URLs", "success");
            }

            // Step 2: Scrape each URL with delays and skip logic
            var progressPerProduct = 85.0 / urlsToScrape.Count;
            var currentProgress = 10.0;

            scraper = new AkakceScraper();
            scraper.Method = ScrapeMethod.Selenium;

            int successCount = 0;
            int errorCount = 0;
            int skippedCount = 0;

            for (int i = 0; i < urlsToScrape.Count; i++)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    await onProgress((int)currentProgress, $"⏹️ Stopped at row #{startFrom + i}/{urls.Count}", "warning");
                    break;
                }
                
                var url = urlsToScrape[i];
                int absoluteRowNumber = startFrom + i;
                
                // Add delay between products (except first one)
                if (i > 0)
                {
                    var delayMs = _random.Next(MIN_DELAY_BETWEEN_PRODUCTS_MS, MAX_DELAY_BETWEEN_PRODUCTS_MS);
                    await onProgress((int)currentProgress, $"⏱️ Waiting {delayMs/1000}s to avoid Cloudflare...", "info");
                    await Task.Delay(delayMs);
                }
                
                await onProgress((int)currentProgress, $"📦 Scraping row #{absoluteRowNumber} ({i + 1}/{urlsToScrape.Count})...", "info");

                // Retry logic with automatic skip
                bool productScraped = false;
                AkakceProductInfo? product = null;
                int retryCount = 0;
                
                while (!productScraped && retryCount <= MAX_PRODUCT_RETRIES)
                {
                    try
                    {
                        product = await scraper.ScrapeProductAsync(url, scanVariants);
                        
                        if (product.IsSuccess)
                        {
                            successCount++;
                            var displayName = !string.IsNullOrEmpty(product.Name) && product.Name.Length > 40
                                ? product.Name.Substring(0, 40) + "..."
                                : product.Name ?? "Unknown";
                            
                            await onProgress((int)currentProgress, $"✅ {displayName} ({product.SellerCount} sellers)", "success");
                            productScraped = true;
                        }
                        else
                        {
                            // Check if it's a Cloudflare block
                            if (product.ErrorMessage?.Contains("Cloudflare") == true)
                            {
                                retryCount++;
                                
                                if (retryCount <= MAX_PRODUCT_RETRIES)
                                {
                                    await onProgress((int)currentProgress, 
                                        $"🔄 Cloudflare block - retry {retryCount}/{MAX_PRODUCT_RETRIES} in 30s...", 
                                        "warning");
                                    await Task.Delay(CLOUDFLARE_COOLDOWN_MS);
                                }
                                else
                                {
                                    // Max retries reached - skip this product
                                    skippedCount++;
                                    await onProgress((int)currentProgress, 
                                        $"⏭️ Skipping row #{absoluteRowNumber} after {MAX_PRODUCT_RETRIES} Cloudflare blocks", 
                                        "warning");
                                    productScraped = true; // Exit retry loop
                                }
                            }
                            else
                            {
                                // Other error - count and skip
                                errorCount++;
                                await onProgress((int)currentProgress, $"❌ {product.ErrorMessage}", "error");
                                productScraped = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        
                        if (retryCount <= MAX_PRODUCT_RETRIES)
                        {
                            await onProgress((int)currentProgress, 
                                $"🔄 Error: {ex.Message} - retry {retryCount}/{MAX_PRODUCT_RETRIES}...", 
                                "warning");
                            await Task.Delay(5000); // Short delay before retry
                        }
                        else
                        {
                            // Max retries reached - create error product and skip
                            errorCount++;
                            skippedCount++;
                            product = new AkakceProductInfo
                            {
                                ProductUrl = url,
                                ErrorMessage = $"Skipped after {MAX_PRODUCT_RETRIES} retries: {ex.Message}",
                                ScrapedAt = DateTime.Now
                            };
                            await onProgress((int)currentProgress, 
                                $"⏭️ Skipping row #{absoluteRowNumber} after multiple errors", 
                                "warning");
                            productScraped = true;
                        }
                    }
                }
                
                // Add product to list (even if failed/skipped for reporting)
                if (product != null)
                {
                    products.Add(product);
                }

                currentProgress += progressPerProduct;
            }

            if (products.Count > 0)
            {
                var stoppedText = cts.Token.IsCancellationRequested ? " (stopped early)" : "";
                await onProgress(95, $"📊 Creating Excel report{stoppedText}...", "info");

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"Akakce_Results_{timestamp}.xlsx";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                try
                {
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(products, filePath);

                    var totalSellers = products.Sum(p => p.SellerCount);
                    
                    var summary = $"✅ {successCount} products, {totalSellers} sellers. " +
                                 $"{errorCount} errors. {skippedCount} skipped.";
                    
                    if (startFrom > 1)
                    {
                        summary += $" (Started from row #{startFrom})";
                    }
                    
                    await onProgress(100, summary, "success");
                    await SendComplete(onProgress, fileName, successCount);
                }
                catch (Exception excelEx)
                {
                    await onProgress(100, $"❌ Excel error: {excelEx.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, "No products scraped", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        catch (Exception ex)
        {
            // Try to save what we have
            if (products.Count > 0)
            {
                try
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var fileName = $"Akakce_Partial_{timestamp}.xlsx";
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    var exporter = new AkakceExcelExporter();
                    exporter.Export(products, filePath);
                    
                    await onProgress(100, $"⚠️ Error but saved {products.Count} products", "warning");
                    await SendComplete(onProgress, fileName, products.Count);
                }
                catch
                {
                    await onProgress(100, $"❌ Error: {ex.Message}", "error");
                    await SendComplete(onProgress, null, null);
                }
            }
            else
            {
                await onProgress(100, $"❌ Error: {ex.Message}", "error");
                await SendComplete(onProgress, null, null);
            }
        }
        finally
        {
            // Cleanup
            scraper?.Dispose();
            if (!string.IsNullOrEmpty(sessionId))
            {
                _sessions.TryRemove(sessionId, out _);
            }
            cts.Dispose();
        }
    }

    private async Task SendComplete(Func<int, string, string, Task> onProgress, string? fileName, int? productCount)
    {
        var data = new
        {
            complete = true,
            downloadUrl = fileName != null ? $"/api/download/{fileName}" : null,
            fileName = fileName,
            productCount = productCount
        };

        var json = System.Text.Json.JsonSerializer.Serialize(data);
        await onProgress(100, json, "complete");
    }
}
