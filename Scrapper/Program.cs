using Scrapper.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddSingleton<TrendyolScraperService>();
builder.Services.AddSingleton<HepsiburadaScraperService>();

var app = builder.Build();

// Serve static files
app.UseStaticFiles();

// Default route - serve index.html
app.MapGet("/", () => Results.Content("""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Scrapper</title>
    <style>
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }
        
        body {
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            min-height: 100vh;
            display: flex;
            justify-content: center;
            align-items: center;
            padding: 20px;
        }
        
        .container {
            background: white;
            border-radius: 20px;
            box-shadow: 0 20px 60px rgba(0,0,0,0.3);
            max-width: 800px;
            width: 100%;
            padding: 40px;
        }
        
        h1 {
            color: #333;
            margin-bottom: 10px;
            font-size: 2.5em;
        }
        
        .subtitle {
            color: #666;
            margin-bottom: 30px;
            font-size: 1.1em;
        }
        
        .form-group {
            margin-bottom: 25px;
        }
        
        label {
            display: block;
            margin-bottom: 8px;
            color: #555;
            font-weight: 600;
            font-size: 0.95em;
        }
        
        input[type="text"],
        input[type="number"] {
            width: 100%;
            padding: 12px 15px;
            border: 2px solid #e0e0e0;
            border-radius: 8px;
            font-size: 16px;
            transition: all 0.3s;
        }
        
        input[type="text"]:focus,
        input[type="number"]:focus {
            outline: none;
            border-color: #667eea;
            box-shadow: 0 0 0 3px rgba(102, 126, 234, 0.1);
        }
        
        .input-hint {
            font-size: 0.85em;
            color: #999;
            margin-top: 5px;
        }
        
        button {
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            border: none;
            padding: 15px 40px;
            font-size: 1.1em;
            font-weight: 600;
            border-radius: 8px;
            cursor: pointer;
            transition: all 0.3s;
            width: 100%;
        }
        
        button:hover:not(:disabled) {
            transform: translateY(-2px);
            box-shadow: 0 10px 20px rgba(102, 126, 234, 0.3);
        }
        
        button:disabled {
            opacity: 0.6;
            cursor: not-allowed;
        }
        
        #progress {
            margin-top: 30px;
            display: none;
        }
        
        .progress-bar {
            background: #f0f0f0;
            border-radius: 10px;
            height: 30px;
            overflow: hidden;
            margin-bottom: 15px;
        }
        
        .progress-fill {
            background: linear-gradient(90deg, #667eea 0%, #764ba2 100%);
            height: 100%;
            width: 0%;
            transition: width 0.3s;
            display: flex;
            align-items: center;
            justify-content: center;
            color: white;
            font-weight: 600;
        }
        
        #status {
            color: #555;
            font-size: 0.95em;
            line-height: 1.6;
        }
        
        .status-item {
            padding: 8px 0;
            border-bottom: 1px solid #f0f0f0;
        }
        
        .status-item:last-child {
            border-bottom: none;
        }
        
        .success {
            color: #4CAF50;
            font-weight: 600;
        }
        
        .error {
            color: #f44336;
            font-weight: 600;
        }
        
        .download-link {
            display: inline-block;
            background: #4CAF50;
            color: white;
            padding: 12px 30px;
            text-decoration: none;
            border-radius: 8px;
            margin-top: 15px;
            font-weight: 600;
            transition: all 0.3s;
        }
        
        .download-link:hover {
            background: #45a049;
            transform: translateY(-2px);
            box-shadow: 0 5px 15px rgba(76, 175, 80, 0.3);
        }
        
        .example {
            background: #f8f9fa;
            padding: 15px;
            border-radius: 8px;
            margin-top: 10px;
            font-size: 0.9em;
        }
        
        .example strong {
            color: #667eea;
        }
        
        .checkbox-label {
            display: flex;
            align-items: center;
            margin-bottom: 10px;
        }
    </style>
</head>
<body>
    <div class="container">
        <h1>🛍️ Scrapper</h1>
        <p class="subtitle">Extract product data from Trendyol or Hepsiburada</p>
        
        <form id="scraperForm">
            <div class="form-group">
                <label for="platform">Platform</label>
                <select 
                    id="platform" 
                    name="platform"
                    style="width: 100%; padding: 12px 15px; border: 2px solid #e0e0e0; border-radius: 8px; font-size: 16px;"
                    onchange="updateExample()"
                >
                    <option value="trendyol">Trendyol</option>
                    <option value="hepsiburada">Hepsiburada</option>
                </select>
                <div class="input-hint">Choose which platform to scrape</div>
            </div>
            
            <div class="form-group">
                <label for="categoryUrl">Category URL</label>
                <input 
                    type="text" 
                    id="categoryUrl" 
                    name="categoryUrl" 
                    placeholder="https://www.trendyol.com/yuz-kremi-x-c1122"
                    value="https://www.trendyol.com/yuz-kremi-x-c1122"
                    required
                >
                <div class="input-hint">Enter the full category page URL</div>
            </div>
            
            <div class="form-group">
                <label for="maxProducts">Maximum Products</label>
                <input 
                    type="number" 
                    id="maxProducts" 
                    name="maxProducts" 
                    min="1" 
                    max="200" 
                    value="20"
                    required
                >
                <div class="input-hint">Choose between 1 and 200 products (more products = longer time)</div>
            </div>
            
            <div class="form-group">
                <label for="scrapeMethod">Scraping Method</label>
                <select 
                    id="scrapeMethod" 
                    name="scrapeMethod"
                    style="width: 100%; padding: 12px 15px; border: 2px solid #e0e0e0; border-radius: 8px; font-size: 16px;"
                >
                    <option value="Selenium">Selenium (Original - Free)</option>
                    <option value="ScrapeDo">Scrape.do API (Fast - Paid)</option>
                </select>
                <div class="input-hint">Selenium is free but slower. Scrape.do is faster but uses API credits.</div>
            </div>
            
            <div class="form-group">
                <label for="template">Export Template (Optional)</label>
                <select 
                    id="template" 
                    name="template"
                    style="width: 100%; padding: 12px 15px; border: 2px solid #e0e0e0; border-radius: 8px; font-size: 16px;"
                >
                    <option value="">Default (No Template)</option>
                    <option value="trendyol_kettle">📋 Trendyol Kettle (MediaMarkt)</option>
                </select>
                <div class="input-hint">Select a template to format the Excel file for specific platforms (e.g., MediaMarkt, Amazon)</div>
            </div>
            
            <div class="form-group">
                <div class="checkbox-label">
                    <input 
                        type="checkbox" 
                        id="excludePrice" 
                        name="excludePrice"
                        style="width: auto; margin-right: 8px;"
                    >
                    <label for="excludePrice" style="margin: 0;">Exclude Price from Excel</label>
                </div>
                
                <div class="checkbox-label">
                    <input 
                        type="checkbox" 
                        id="processImages" 
                        name="processImages"
                        checked
                        style="width: auto; margin-right: 8px;"
                    >
                    <label for="processImages" style="margin: 0;">Process & Upload Images to CDN (1000x1000)</label>
                </div>
                <div class="input-hint">Resize images to 1000x1000 and upload to CDN (increases scraping time)</div>
            </div>
            
            <div class="example" id="exampleUrls">
                <strong>Example URLs:</strong><br>
                • Face Cream: https://www.trendyol.com/yuz-kremi-x-c1122<br>
                • Skincare: https://www.trendyol.com/cilt-bakimi-x-c1121<br>
                • Makeup: https://www.trendyol.com/makyaj-x-c1123
            </div>
            
            <button type="submit" id="startBtn">Start Scraping</button>
        </form>
        
        <div id="progress">
            <div class="progress-bar">
                <div class="progress-fill" id="progressFill">0%</div>
            </div>
            <div id="status"></div>
        </div>
    </div>
    
    <script>
        function updateExample() {
            const platform = document.getElementById('platform').value;
            const exampleUrls = document.getElementById('exampleUrls');
            const categoryUrl = document.getElementById('categoryUrl');
            
            if (platform === 'hepsiburada') {
                exampleUrls.innerHTML = `
                    <strong>Example URLs (Hepsiburada):</strong><br>
                    • Tablets: https://www.hepsiburada.com/apple-tablet-xc-3008012-b8849<br>
                    • Laptops: https://www.hepsiburada.com/laptop-notebook-dizustu-bilgisayarlar-c-98<br>
                    • Phones: https://www.hepsiburada.com/cep-telefonlari-c-371965
                `;
                categoryUrl.placeholder = 'https://www.hepsiburada.com/apple-tablet-xc-3008012-b8849';
                categoryUrl.value = 'https://www.hepsiburada.com/apple-tablet-xc-3008012-b8849';
            } else {
                exampleUrls.innerHTML = `
                    <strong>Example URLs (Trendyol):</strong><br>
                    • Face Cream: https://www.trendyol.com/yuz-kremi-x-c1122<br>
                    • Skincare: https://www.trendyol.com/cilt-bakimi-x-c1121<br>
                    • Makeup: https://www.trendyol.com/makyaj-x-c1123
                `;
                categoryUrl.placeholder = 'https://www.trendyol.com/yuz-kremi-x-c1122';
                categoryUrl.value = 'https://www.trendyol.com/yuz-kremi-x-c1122';
            }
        }
        
        const form = document.getElementById('scraperForm');
        const startBtn = document.getElementById('startBtn');
        const progress = document.getElementById('progress');
        const progressFill = document.getElementById('progressFill');
        const status = document.getElementById('status');
        
        form.addEventListener('submit', async (e) => {
            e.preventDefault();
            
            const platform = document.getElementById('platform').value;
            const categoryUrl = document.getElementById('categoryUrl').value;
            const maxProducts = document.getElementById('maxProducts').value;
            const excludePrice = document.getElementById('excludePrice').checked;
            const processImages = document.getElementById('processImages').checked;
            const scrapeMethod = document.getElementById('scrapeMethod').value;
            const templateName = document.getElementById('template').value; // NEW: Get template
            
            // Disable form
            startBtn.disabled = true;
            startBtn.textContent = 'Scraping...';
            progress.style.display = 'block';
            status.innerHTML = '<div class="status-item">Starting scraper...</div>';
            
            try {
                const response = await fetch('/api/scrape', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                    },
                    body: JSON.stringify({
                        platform: platform,
                        categoryUrl: categoryUrl,
                        maxProducts: parseInt(maxProducts),
                        excludePrice: excludePrice,
                        processImages: processImages,
                        scrapeMethod: scrapeMethod,
                        templateName: templateName || null // NEW: Send template
                    })
                });
                
                if (!response.ok) {
                    throw new Error('Scraping failed');
                }
                
                const reader = response.body.getReader();
                const decoder = new TextDecoder();
                
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    
                    const text = decoder.decode(value);
                    const lines = text.split('\n');
                    
                    for (const line of lines) {
                        if (line.startsWith('data: ')) {
                            const data = JSON.parse(line.substring(6));
                            
                            if (data.progress !== undefined) {
                                progressFill.style.width = data.progress + '%';
                                progressFill.textContent = data.progress + '%';
                            }
                            
                            if (data.message) {
                                const statusItem = document.createElement('div');
                                statusItem.className = 'status-item';
                                if (data.type === 'success') {
                                    statusItem.className += ' success';
                                } else if (data.type === 'error') {
                                    statusItem.className += ' error';
                                }
                                statusItem.textContent = data.message;
                                status.insertBefore(statusItem, status.firstChild);
                            }
                            
                            if (data.downloadUrl) {
                                const link = document.createElement('a');
                                link.href = data.downloadUrl;
                                link.className = 'download-link';
                                link.textContent = '📥 Download Excel File';
                                link.download = data.fileName;
                                status.insertBefore(link, status.firstChild);
                            }
                            
                            if (data.complete) {
                                startBtn.disabled = false;
                                startBtn.textContent = 'Start Scraping';
                            }
                        }
                    }
                }
            } catch (error) {
                status.innerHTML = '<div class="status-item error">Error: ' + error.message + '</div>';
                startBtn.disabled = false;
                startBtn.textContent = 'Start Scraping';
            }
        });
    </script>
</body>
</html>
""", "text/html"));

// API endpoint for scraping
app.MapPost("/api/scrape", async (ScraperRequest request, TrendyolScraperService trendyolService, HepsiburadaScraperService hepsiburadaService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        
        // Parse scrape method from request
        Scrapper.Models.ScrapeMethod scrapeMethod = request.ScrapeMethod.ToLower() == "scrapedo" 
            ? Scrapper.Models.ScrapeMethod.ScrapeDo 
            : Scrapper.Models.ScrapeMethod.Selenium;
        
        // Choose service based on platform
        if (request.Platform.ToLower() == "hepsiburada")
        {
            await hepsiburadaService.ScrapeWithProgressAsync(
                request.CategoryUrl,
                request.MaxProducts,
                request.ExcludePrice,
                scrapeMethod,
                request.ProcessImages,
                async (progress, message, type) =>
                {
                    var data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        progress,
                        message,
                        type
                    });
                    await writer.WriteLineAsync($"data: {data}\n");
                    await writer.FlushAsync();
                }
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
                request.TemplateName, // NEW: Pass template name
                async (progress, message, type) =>
                {
                    var data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        progress,
                        message,
                        type
                    });
                    await writer.WriteLineAsync($"data: {data}\n");
                    await writer.FlushAsync();
                }
            );
        }
    }, "text/event-stream");
});

// NEW: Get available templates
app.MapGet("/api/templates", () =>
{
    var templateService = new Scrapper.Services.TemplateService();
    var templates = templateService.GetTemplateInfo();
    return Results.Ok(templates);
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

Console.WriteLine("🚀 Scrapper Web Application");
Console.WriteLine("📍 Open your browser and navigate to: http://localhost:5000");
Console.WriteLine("Press Ctrl+C to stop the server");

app.Run("http://localhost:5000");

public record ScraperRequest(
    string Platform, 
    string CategoryUrl, 
    int MaxProducts, 
    bool ExcludePrice, 
    bool ProcessImages, 
    string ScrapeMethod,
    string? TemplateName // NEW: Optional template name
);
