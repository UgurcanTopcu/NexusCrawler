using OfficeOpenXml;
using Scrapper.Infrastructure;
using Scrapper.Models;
using Scrapper.Services;

var builder = WebApplication.CreateBuilder(args);

// ============================================
// SERVICE REGISTRATION (no duplicates)
// ============================================

// Configuration
builder.Services.AddSingleton<CdnFtpConfig>();

// HTTP Client
builder.Services.AddHttpClient();
builder.Services.AddSingleton<HttpClient>();

// Core Services
builder.Services.AddSingleton<CdnCacheService>();
builder.Services.AddSingleton<FtpUploadService>();
builder.Services.AddSingleton<ImageProcessingService>(sp =>
{
    var httpClient = new HttpClient();
    var ftpService = sp.GetRequiredService<FtpUploadService>();
    var cdnCache = sp.GetRequiredService<CdnCacheService>();
    return new ImageProcessingService(httpClient, ftpService, cdnCache);
});

// Scraper Services
builder.Services.AddSingleton<TrendyolScraperService>();
builder.Services.AddSingleton<HepsiburadaScraperService>();
builder.Services.AddSingleton<AkakceScraperService>();
builder.Services.AddSingleton<AkakceSearchService>();
builder.Services.AddSingleton<HepsiburadaBarcodeSearchService>();

// Bulk Image Services
builder.Services.AddSingleton<BulkImageExcelReader>();
builder.Services.AddSingleton<BulkImageProcessingService>();
builder.Services.AddSingleton<BulkImageExcelExporter>();

// MVC
builder.Services.AddControllersWithViews();

// ============================================
// EPPLUS LICENSE
// ============================================
try
{
    ExcelPackage.License.SetNonCommercialOrganization("Personal");
    Console.WriteLine("[EPPlus] License set to NonCommercial");
}
catch (Exception ex)
{
    Console.WriteLine($"[EPPlus] License setup failed: {ex.Message}");
    try
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        Console.WriteLine("[EPPlus] License context set using legacy method");
    }
    catch (Exception ex2)
    {
        Console.WriteLine($"[EPPlus] Legacy license failed too: {ex2.Message}");
    }
}

var app = builder.Build();

// ============================================
// MIDDLEWARE PIPELINE
// ============================================

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

// Main scraper endpoint (Trendyol/Hepsiburada)
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
                request.ScanVariants
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
                sessionId
            );
        }
        catch (Exception ex)
        {
            await SseHelper.SendErrorAsync(writer, ex.Message);
        }
    }, "text/event-stream");
});

// Download endpoint
app.MapGet("/api/download/{fileName}", (string fileName) =>
{
    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
    
    if (!File.Exists(filePath))
    {
        return Results.NotFound();
    }
    
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

app.MapGet("/bulk-image", async (IWebHostEnvironment env) =>
{
    var filePath = Path.Combine(env.WebRootPath, "pages", "bulk-image.html");
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
Console.WriteLine("   - Bulk Image Uploader: http://localhost:5000/bulk-image");
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

public record AkakceCategoryRequest(
    string CategoryUrl,
    int MaxProducts,
    string? SessionId,
    int StartFrom = 1,
    bool ScanVariants = false
);

