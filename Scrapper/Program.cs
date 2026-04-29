using OfficeOpenXml;
using Scrapper.Infrastructure;
using Scrapper.Models;
using Scrapper.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<HttpClient>();

// New wsrv.nl image service (replaces FTP-based CDN)
builder.Services.AddSingleton<WsrvImageService>();

builder.Services.AddSingleton<TrendyolScraperService>();
builder.Services.AddSingleton<HepsiburadaScraperService>();
builder.Services.AddSingleton<AkakceScraperService>();
builder.Services.AddSingleton<AkakceSearchService>();
builder.Services.AddSingleton<AkakcePriceComparisonService>();
builder.Services.AddSingleton<AkakceScrapeDoService>();
builder.Services.AddSingleton<AkakcePriceComparisonV2Service>();
builder.Services.AddSingleton<PriceIndexService>();
builder.Services.AddSingleton<HepsiburadaBarcodeSearchService>();
builder.Services.AddSingleton<HepsiburadaProductSearchService>();

builder.Services.AddSingleton<BulkImageExcelReader>();
builder.Services.AddSingleton<BulkImageProcessingService>();
builder.Services.AddSingleton<BulkImageExcelExporter>();

builder.Services.AddControllersWithViews();


ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

var app = builder.Build();


if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Serve static files from wwwroot
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// ============================================
// API ENDPOINTS
// ============================================

// Main scraper endpoint (Trendyol/Hepsiburada) — single URL
app.MapPost("/api/scrape", async (ScraperRequest request, TrendyolScraperService trendyolService, HepsiburadaScraperService hepsiburadaService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        var progressCallback = SseHelper.CreateProgressCallback(writer);
        
        var scrapeMethod = request.ScrapeMethod.ToLower() == "scrapedo" 
            ? ScrapeMethod.ScrapeDo 
            : ScrapeMethod.Selenium;
        
        if (request.Platform.ToLower() == "hepsiburada")
        {
            await hepsiburadaService.ScrapeWithProgressAsync(
                request.CategoryUrl,
                request.MaxProducts,
                request.ExcludePrice,
                scrapeMethod,
                request.ProcessImages,
                progressCallback,
                request.SessionId
            );
        }
        else
        {
            await trendyolService.ScrapeWithProgressAsync(
                request.CategoryUrl,
                request.MaxProducts,
                request.ExcludePrice,
                scrapeMethod,
                request.ProcessImages,
                request.TemplateName,
                progressCallback,
                request.SessionId
            );
        }
    }, "text/event-stream");
});

// Hepsiburada product search endpoint - search by product name from Excel and scrape the matched product page
app.MapPost("/api/hepsiburada-product/search", async (HttpRequest request, HepsiburadaProductSearchService productSearchService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);

        try
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var sessionId = form["sessionId"].ToString();

            if (file == null || file.Length == 0)
            {
                await SseHelper.SendNoFileErrorAsync(writer);
                return;
            }

            using var memoryStream = await SseHelper.ReadFileToMemoryStreamAsync(file);

            await productSearchService.SearchAndScrapeFromExcelAsync(
                memoryStream,
                SseHelper.CreateProgressCallback(writer),
                sessionId,
                file.FileName
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Batch scraper endpoint — multiple URLs combined into one Excel
app.MapPost("/api/scrape-batch", async (BatchScraperRequest request, TrendyolScraperService trendyolService, HepsiburadaScraperService hepsiburadaService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        var progressCallback = SseHelper.CreateProgressCallback(writer);

        var scrapeMethod = request.ScrapeMethod.ToLower() == "scrapedo"
            ? ScrapeMethod.ScrapeDo
            : ScrapeMethod.Selenium;

        try
        {
            if (request.Platform.ToLower() == "hepsiburada")
            {
                await hepsiburadaService.ScrapeMultipleWithProgressAsync(
                    request.CategoryUrls,
                    request.MaxProducts,
                    request.ExcludePrice,
                    scrapeMethod,
                    request.ProcessImages,
                    progressCallback,
                    request.SessionId
                );
            }
            else
            {
                await trendyolService.ScrapeMultipleWithProgressAsync(
                    request.CategoryUrls,
                    request.MaxProducts,
                    request.ExcludePrice,
                    scrapeMethod,
                    request.ProcessImages,
                    request.TemplateName,
                    progressCallback,
                    request.SessionId
                );
            }
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Bulk image processing endpoint
app.MapPost("/api/bulk-image/process", async (HttpRequest request, BulkImageProcessingService bulkImageService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        
        try
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var sessionId = form["sessionId"].ToString();
            
            if (file == null || file.Length == 0)
            {
                await SseHelper.SendNoFileErrorAsync(writer);
                return;
            }
            
            using var memoryStream = await SseHelper.ReadFileToMemoryStreamAsync(file);
            
            await bulkImageService.ProcessExcelAsync(
                memoryStream,
                hasHeader: true,
                SseHelper.CreateProgressCallback(writer),
                sessionId
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Akakce category URL scraping endpoint
app.MapPost("/api/akakce/scrape-category", async (AkakceCategoryRequest request, AkakceScraperService akakceService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        
        try
        {
            await akakceService.ProcessCategoryUrlAsync(
                request.CategoryUrl,
                request.MaxProducts,
                SseHelper.CreateProgressCallback(writer),
                request.SessionId,
                request.StartFrom,
                request.ScanVariants,
                request.MaxSellersPerProduct,
                request.IncludePreferredMarketplaceMatches,
                request.PreferredMarketplaces,
                request.UseScrapeDo
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Akakce batch category scraping endpoint (multiple URLs → one combined Excel)
app.MapPost("/api/akakce/scrape-categories", async (AkakceBatchCategoryRequest request, AkakceScraperService akakceService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);

        try
        {
            await akakceService.ProcessBatchCategoryUrlsAsync(
                request.CategoryUrls,
                request.MaxProducts,
                SseHelper.CreateProgressCallback(writer),
                request.SessionId,
                request.StartFrom,
                request.ScanVariants,
                request.MaxSellersPerProduct,
                request.IncludePreferredMarketplaceMatches,
                request.PreferredMarketplaces,
                request.UseScrapeDo
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Akakce multi-group endpoint — server processes all groups sequentially in one request.
// Immune to Windows lock-screen JS throttling because the loop runs entirely server-side.
app.MapPost("/api/akakce/scrape-groups", async (AkakceGroupsRequest request, AkakceScraperService akakceService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);

        try
        {
            await akakceService.ProcessSeparateCategoryGroupsAsync(
                request.UrlGroups,
                request.MaxProducts,
                SseHelper.CreateProgressCallback(writer),
                request.SessionId,
                request.StartFrom,
                request.ScanVariants,
                request.MaxSellersPerProduct,
                request.IncludePreferredMarketplaceMatches,
                request.PreferredMarketplaces,
                request.UseScrapeDo
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Price Index — upload daily export, store history, generate trend report
app.MapPost("/api/akakce/price-index", async (HttpRequest request, PriceIndexService indexService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);

        try
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var sessionId = form["sessionId"].ToString();
            var dateOverride = form["dateOverride"].ToString(); // optional "yyyy-MM-dd"

            if (file == null || file.Length == 0)
            {
                await SseHelper.SendNoFileErrorAsync(writer);
                return;
            }

            using var memoryStream = await SseHelper.ReadFileToMemoryStreamAsync(file);

            await indexService.ProcessAsync(
                memoryStream,
                SseHelper.CreateProgressCallback(writer),
                sessionId,
                dateOverride,
                file.FileName
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Price Index — clear all history
app.MapPost("/api/akakce/price-index/clear", async () =>
{
    await PriceIndexService.ClearHistoryAsync();
    return Results.Ok(new { message = "Price index history cleared" });
});

// Price Index — list all snapshot dates
app.MapGet("/api/akakce/price-index/dates", async () =>
{
    var dates = await PriceIndexService.GetSnapshotDatesAsync();
    return Results.Ok(new { dates, count = dates.Count });
});

// Price Index — delete a single snapshot date from all products
app.MapDelete("/api/akakce/price-index/dates/{date}", async (string date) =>
{
    var removed = await PriceIndexService.DeleteSnapshotDateAsync(date);
    return Results.Ok(new { message = $"Removed {date} from {removed} product(s)", removed });
});

// Price Index — export the full JSON history file
app.MapGet("/api/akakce/price-index/export", async () =>
{
    var filePath = PriceIndexService.GetHistoryFilePath();
    if (!File.Exists(filePath))
        return Results.NotFound(new { message = "No history file exists yet" });

    var bytes = await File.ReadAllBytesAsync(filePath);
    return Results.File(bytes, "application/json", "price-index-history.json");
});

// Price Index — import a JSON history file (replaces existing history)
app.MapPost("/api/akakce/price-index/import", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { message = "No file provided" });

    try
    {
        using var stream = file.OpenReadStream();
        await PriceIndexService.ImportHistoryAsync(stream);
        return Results.Ok(new { message = "History imported successfully" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Import failed: {ex.Message}" });
    }
});

// Price Index — offer data import (formerly 'backfill')
app.MapPost("/api/akakce/price-index/offer-import", async (HttpRequest request, PriceIndexService indexService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        try
        {
            var form         = await request.ReadFormAsync();
            var file         = form.Files.GetFile("file");
            var dateOverride = form["dateOverride"].ToString();

            if (file == null || file.Length == 0)
            {
                await SseHelper.SendNoFileErrorAsync(writer);
                return;
            }

            if (string.IsNullOrWhiteSpace(dateOverride))
            {
                await SseHelper.SendErrorAsync(writer, "A date is required for offer import");
                return;
            }

            using var memoryStream = await SseHelper.ReadFileToMemoryStreamAsync(file);

            await indexService.BackfillAsync(
                memoryStream,
                SseHelper.CreateProgressCallback(writer),
                dateOverride
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Bulk image URL list processing endpoint (no Excel file)
app.MapPost("/api/bulk-image/process-urls", async (HttpRequest request, BulkImageProcessingService bulkImageService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        
        try
        {
            var form = await request.ReadFormAsync();
            var urlsText = form["urls"].ToString();
            var sessionId = form["sessionId"].ToString();
            
            if (string.IsNullOrWhiteSpace(urlsText))
            {
                await SseHelper.SendErrorAsync(writer, "No URLs provided");
                return;
            }
            
            // Split by newlines and filter out empty lines
            var urls = urlsText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(u => u.Trim())
                              .Where(u => !string.IsNullOrWhiteSpace(u))
                              .ToList();
            
            await bulkImageService.ProcessUrlListAsync(
                urls,
                SseHelper.CreateProgressCallback(writer),
                sessionId
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Akakce file upload scraping endpoint
app.MapPost("/api/akakce/scrape", async (HttpRequest request, AkakceScraperService akakceService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        
        try
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var scrapeMethodStr = form["scrapeMethod"].ToString();
            var sessionId = form["sessionId"].ToString();
            var scanVariantsStr = form["scanVariants"].ToString();
            
            if (file == null || file.Length == 0)
            {
                await SseHelper.SendNoFileErrorAsync(writer);
                return;
            }
            
            var scrapeMethod = scrapeMethodStr.ToLower() == "scrapedo" 
                ? ScrapeMethod.ScrapeDo 
                : ScrapeMethod.Selenium;
            
            bool scanVariants = bool.TryParse(scanVariantsStr, out var sv) && sv;
            
            using var memoryStream = await SseHelper.ReadFileToMemoryStreamAsync(file);
            
            await akakceService.ProcessExcelFileAsync(
                memoryStream,
                scrapeMethod,
                SseHelper.CreateProgressCallback(writer),
                sessionId,
                1,
                scanVariants
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Akakce price comparison endpoint - search by name + compare against user's prices
app.MapPost("/api/akakce/price-compare", async (HttpRequest request, AkakcePriceComparisonService compareService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);

        try
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var sessionId = form["sessionId"].ToString();

            if (file == null || file.Length == 0)
            {
                await SseHelper.SendNoFileErrorAsync(writer);
                return;
            }

            using var memoryStream = await SseHelper.ReadFileToMemoryStreamAsync(file);

            await compareService.CompareFromExcelAsync(
                memoryStream,
                SseHelper.CreateProgressCallback(writer),
                sessionId
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Akakce price comparison V2 - Selenium search + Scrape.do fetch (faster, no Cloudflare on product pages)
app.MapPost("/api/akakce/price-compare-v2", async (HttpRequest request, AkakcePriceComparisonV2Service v2Service) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);

        try
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var sessionId = form["sessionId"].ToString();

            if (file == null || file.Length == 0)
            {
                await SseHelper.SendNoFileErrorAsync(writer);
                return;
            }

            using var memoryStream = await SseHelper.ReadFileToMemoryStreamAsync(file);

            await v2Service.CompareFromExcelAsync(
                memoryStream,
                SseHelper.CreateProgressCallback(writer),
                sessionId
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Akakce search endpoint - search by product name from Excel
app.MapPost("/api/akakce/search", async (HttpRequest request, AkakceSearchService searchService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        
        try
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var sessionId = form["sessionId"].ToString();
            var scanVariantsStr = form["scanVariants"].ToString();
            
            if (file == null || file.Length == 0)
            {
                await SseHelper.SendNoFileErrorAsync(writer);
                return;
            }
            
            bool scanVariants = bool.TryParse(scanVariantsStr, out var sv) && sv;
            
            using var memoryStream = await SseHelper.ReadFileToMemoryStreamAsync(file);
            
            await searchService.SearchAndScrapeFromExcelAsync(
                memoryStream,
                scanVariants,
                SseHelper.CreateProgressCallback(writer),
                sessionId
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Hepsiburada barcode search endpoint - search by barcode from Excel
app.MapPost("/api/hepsiburada-barcode/search", async (HttpRequest request, HepsiburadaBarcodeSearchService barcodeService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        
        try
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var sessionId = form["sessionId"].ToString();
            
            if (file == null || file.Length == 0)
            {
                await SseHelper.SendNoFileErrorAsync(writer);
                return;
            }
            
            using var memoryStream = await SseHelper.ReadFileToMemoryStreamAsync(file);
            
            await barcodeService.SearchBarcodesFromExcelAsync(
                memoryStream,
                SseHelper.CreateProgressCallback(writer),
                sessionId,
                file.FileName
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Download endpoint — checks working directory and PriceIndexData subfolder
app.MapGet("/api/download/{fileName}", (string fileName) =>
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), fileName),
        Path.Combine(Directory.GetCurrentDirectory(), "PriceIndexData", fileName)
    };

    var filePath = candidates.FirstOrDefault(File.Exists);
    if (filePath is null)
        return Results.NotFound();

    return Results.File(filePath, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

// ============================================
// STOP ENDPOINTS
// ============================================

app.MapPost("/api/stop/{sessionId}", (string sessionId) =>
{
    TrendyolScraperService.StopSession(sessionId);
    HepsiburadaScraperService.StopSession(sessionId);
    Console.WriteLine($"[API] Stop requested for session: {sessionId}");
    return Results.Ok(new { message = "Stop signal sent" });
});

app.MapPost("/api/akakce/stop/{sessionId}", (string sessionId) =>
{
    AkakceScraperService.StopSession(sessionId);
    AkakceSearchService.StopSession(sessionId);
    AkakcePriceComparisonService.StopSession(sessionId);
    AkakcePriceComparisonV2Service.StopSession(sessionId);
    // Also stop any group sub-sessions
    for (int i = 0; i < 50; i++)
        AkakceScraperService.StopSession($"{sessionId}_g{i}");
    Console.WriteLine($"[API] Akakce stop requested for session: {sessionId}");
    return Results.Ok(new { message = "Stop signal sent" });
});

app.MapPost("/api/bulk-image/stop/{sessionId}", (string sessionId) =>
{
    BulkImageProcessingService.StopSession(sessionId);
    Console.WriteLine($"[API] Bulk image stop requested for session: {sessionId}");
    return Results.Ok(new { message = "Stop signal sent" });
});

app.MapPost("/api/hepsiburada-barcode/stop", (HttpRequest request) =>
{
    var sessionId = request.Query["sessionId"].ToString();
    HepsiburadaBarcodeSearchService.StopSession(sessionId);
    Console.WriteLine($"[API] Hepsiburada barcode stop requested for session: {sessionId}");
    return Results.Ok(new { message = "Stop signal sent" });
});

app.MapPost("/api/hepsiburada-product/stop", (HttpRequest request) =>
{
    var sessionId = request.Query["sessionId"].ToString();
    HepsiburadaProductSearchService.StopSession(sessionId);
    Console.WriteLine($"[API] Hepsiburada product search stop requested for session: {sessionId}");
    return Results.Ok(new { message = "Stop signal sent" });
});

// ============================================
// PAGE ROUTES - Serve HTML from wwwroot/pages
// ============================================

app.MapGet("/", async (IWebHostEnvironment env) =>
{
    var filePath = Path.Combine(env.WebRootPath, "pages", "index.html");
    var content = await File.ReadAllTextAsync(filePath);
    return Results.Content(content, "text/html; charset=utf-8");
});

app.MapGet("/akakce", async (IWebHostEnvironment env) =>
{
    var filePath = Path.Combine(env.WebRootPath, "pages", "akakce.html");
    var content = await File.ReadAllTextAsync(filePath);
    return Results.Content(content, "text/html; charset=utf-8");
});

app.MapGet("/akakce-search", async (IWebHostEnvironment env) =>
{
    var filePath = Path.Combine(env.WebRootPath, "pages", "akakce-search.html");
    var content = await File.ReadAllTextAsync(filePath);
    return Results.Content(content, "text/html; charset=utf-8");
});

app.MapGet("/hepsiburada-barcode", async (IWebHostEnvironment env) =>
{
    var filePath = Path.Combine(env.WebRootPath, "pages", "hepsiburada-barcode.html");
    var content = await File.ReadAllTextAsync(filePath);
    return Results.Content(content, "text/html; charset=utf-8");
});

app.MapGet("/hepsiburada-product-search", async (IWebHostEnvironment env) =>
{
    var filePath = Path.Combine(env.WebRootPath, "pages", "hepsiburada-product-search.html");
    var content = await File.ReadAllTextAsync(filePath);
    return Results.Content(content, "text/html; charset=utf-8");
});

app.MapGet("/bulk-image", async (IWebHostEnvironment env, HttpContext context) =>
{
    var filePath = Path.Combine(env.WebRootPath, "pages", "bulk-image.html");
    var content = await File.ReadAllTextAsync(filePath);
    
    // Add cache-busting headers
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    
    return Results.Content(content, "text/html; charset=utf-8");
});

app.MapGet("/price-index", async (IWebHostEnvironment env) =>
{
    var filePath = Path.Combine(env.WebRootPath, "pages", "price-index.html");
    var content = await File.ReadAllTextAsync(filePath);
    return Results.Content(content, "text/html; charset=utf-8");
});

// ============================================
// STARTUP
// ============================================

Console.WriteLine("🔧 Scrapper Web Application");
Console.WriteLine("🌐 Open your browser and navigate to: http://localhost:5000");
Console.WriteLine("   - Category Scraper: http://localhost:5000/");
Console.WriteLine("   - Akakce Scraper: http://localhost:5000/akakce");
Console.WriteLine("   - Akakce Search: http://localhost:5000/akakce-search");
Console.WriteLine("   - Akakce Price Compare: http://localhost:5000/akakce (Price Compare mode)");
Console.WriteLine("   - Price Index: http://localhost:5000/price-index");
Console.WriteLine("   - Bulk Image Uploader: http://localhost:5000/bulk-image");
Console.WriteLine("   - Hepsiburada Product Search: http://localhost:5000/hepsiburada-product-search");
Console.WriteLine("Press Ctrl+C to stop the server");

app.Run("http://localhost:5000");

// ============================================
// REQUEST RECORDS
// ============================================

public record ScraperRequest(
    string Platform, 
    string CategoryUrl, 
    int MaxProducts, 
    bool ExcludePrice, 
    bool ProcessImages, 
    string ScrapeMethod,
    string? TemplateName,
    string? SessionId
);

public record BatchScraperRequest(
    string Platform,
    string[] CategoryUrls,
    int MaxProducts,
    bool ExcludePrice,
    bool ProcessImages,
    string ScrapeMethod,
    string? TemplateName,
    string? SessionId
);

public record AkakceCategoryRequest(
    string CategoryUrl,
    int MaxProducts,
    string? SessionId,
    int StartFrom = 1,
    bool ScanVariants = false,
    int MaxSellersPerProduct = 0,
    bool IncludePreferredMarketplaceMatches = false,
    string? PreferredMarketplaces = null,
    bool UseScrapeDo = false
);

// Used by the server-side group loop endpoint (screen-lock safe)
public record AkakceGroupsRequest(
    string[][] UrlGroups,
    int MaxProducts,
    string? SessionId,
    int StartFrom = 1,
    bool ScanVariants = false,
    int MaxSellersPerProduct = 0,
    bool IncludePreferredMarketplaceMatches = false,
    string? PreferredMarketplaces = null,
    bool UseScrapeDo = false
);

public record AkakceBatchCategoryRequest(
    string[] CategoryUrls,
    int MaxProducts,
    string? SessionId,
    int StartFrom = 1,
    bool ScanVariants = false,
    int MaxSellersPerProduct = 0,
    bool IncludePreferredMarketplaceMatches = false,
    string? PreferredMarketplaces = null,
    bool UseScrapeDo = false
);

