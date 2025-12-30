using Scrapper.Services;
using OfficeOpenXml;
using Microsoft.AspNetCore.StaticFiles;

// Configure EPPlus license FIRST - before any ExcelPackage usage
// EPPlus 8.x: Use License.SetLicenseContext or specific license methods
try
{
    // For non-commercial use - choose one of these methods:
    ExcelPackage.License.SetNonCommercialOrganization("Personal");
    // OR
    // ExcelPackage.License.SetNonCommercialPersonal("Your Name");
    
    Console.WriteLine("[EPPlus] License set to NonCommercial");
}
catch (Exception ex)
{
    Console.WriteLine($"[EPPlus] License setup failed: {ex.Message}");
    Console.WriteLine("[EPPlus] Trying alternative license context...");
    
    // Fallback: Try the old LicenseContext property (for EPPlus 5.x-7.x compatibility)
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

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddSingleton<TrendyolScraperService>();
builder.Services.AddSingleton<HepsiburadaScraperService>();
builder.Services.AddSingleton<AkakceScraperService>(); // NEW: Akakce service

// Add services to the container.
builder.Services.AddControllersWithViews();

// Configure static files with UTF-8 encoding
builder.Services.AddDirectoryBrowser();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}


app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Default route - serve index.html with navigation
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
        
        h1 { color: #333; margin-bottom: 10px; font-size: 2.5em; }
        .subtitle { color: #666; margin-bottom: 30px; font-size: 1.1em; }
        
        .nav-buttons { display: flex; gap: 15px; margin-bottom: 30px; }
        
        .nav-btn {
            flex: 1;
            padding: 20px;
            border: 2px solid #667eea;
            border-radius: 12px;
            background: white;
            cursor: pointer;
            transition: all 0.3s;
            text-align: center;
        }
        
        .nav-btn:hover { background: #667eea; color: white; }
        .nav-btn.active {
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            border-color: transparent;
        }
        
        .nav-btn h3 { margin-bottom: 5px; }
        .nav-btn p { font-size: 0.85em; opacity: 0.8; }
        
        .form-group { margin-bottom: 25px; }
        
        label { display: block; margin-bottom: 8px; color: #555; font-weight: 600; font-size: 0.95em; }
        
        input[type="text"], input[type="number"], select {
            width: 100%;
            padding: 12px 15px;
            border: 2px solid #e0e0e0;
            border-radius: 8px;
            font-size: 16px;
            transition: all 0.3s;
        }
        
        input[type="text"]:focus, input[type="number"]:focus, select:focus {
            outline: none;
            border-color: #667eea;
            box-shadow: 0 0 0 3px rgba(102, 126, 234, 0.1);
        }
        
        .input-hint { font-size: 0.85em; color: #999; margin-top: 5px; }
        
        .button-group { display: flex; gap: 10px; margin-top: 20px; }
        
        button {
            padding: 15px 40px;
            font-size: 1.1em;
            font-weight: 600;
            border-radius: 8px;
            cursor: pointer;
            transition: all 0.3s;
            border: none;
        }
        
        button:hover:not(:disabled) { transform: translateY(-2px); }
        button:disabled { opacity: 0.6; cursor: not-allowed; }
        
        #startBtn {
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            flex: 2;
        }
        
        #startBtn:hover:not(:disabled) { box-shadow: 0 10px 20px rgba(102, 126, 234, 0.3); }
        
        #stopBtn {
            background: #f44336;
            color: white;
            flex: 1;
            display: none;
        }
        
        #stopBtn:hover:not(:disabled) { box-shadow: 0 10px 20px rgba(244, 67, 54, 0.3); }
        
        #resetBtn {
            background: #757575;
            color: white;
            flex: 1;
        }
        
        #resetBtn:hover:not(:disabled) { box-shadow: 0 10px 20px rgba(117, 117, 117, 0.3); }
        
        #progress { margin-top: 30px; display: none; }
        
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
        
        #status { color: #555; font-size: 0.95em; line-height: 1.6; max-height: 300px; overflow-y: auto; }
        .status-item { padding: 8px 0; border-bottom: 1px solid #f0f0f0; }
        .status-item:last-child { border-bottom: none; }
        .success { color: #4CAF50; font-weight: 600; }
        .error { color: #f44336; font-weight: 600; }
        .warning { color: #ff9800; font-weight: 600; }
        
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
        
        .download-link:hover { background: #45a049; transform: translateY(-2px); }
        
        .example { background: #f8f9fa; padding: 15px; border-radius: 8px; margin-top: 10px; font-size: 0.9em; }
        .example strong { color: #667eea; }
        
        .checkbox-label { display: flex; align-items: center; margin-bottom: 10px; }
        .checkbox-label input { width: auto; margin-right: 8px; }
        .checkbox-label label { margin: 0; }
    </style>
</head>
<body>
    <div class="container">
        <h1>🔧 Scrapper</h1>
        <p class="subtitle">Extract product data from e-commerce sites</p>
        
        <!-- Navigation Buttons -->
        <div class="nav-buttons">
            <div class="nav-btn active" onclick="window.location.href='/'">
                <h3>🛒 Category Scraper</h3>
                <p>Trendyol & Hepsiburada</p>
            </div>
            <div class="nav-btn" onclick="window.location.href='/akakce'">
                <h3>💰 Akakce Scraper</h3>
                <p>Price Comparison</p>
            </div>
        </div>
        
        <form id="scraperForm">
            <div class="form-group">
                <label for="platform">Platform</label>
                <select id="platform" name="platform" onchange="updateExample()">
                    <option value="trendyol">Trendyol</option>
                    <option value="hepsiburada">Hepsiburada</option>
                </select>
            </div>
            
            <div class="form-group">
                <label for="categoryUrl">Category URL</label>
                <input type="text" id="categoryUrl" name="categoryUrl" 
                    placeholder="https://www.trendyol.com/yuz-kremi-x-c1122"
                    value="https://www.trendyol.com/yuz-kremi-x-c1122" required>
            </div>
            
            <div class="form-group">
                <label for="maxProducts">Maximum Products</label>
                <input type="number" id="maxProducts" name="maxProducts" min="1" max="2000" value="20" required>
                <div class="input-hint">Choose between 1 and 2000 products</div>
            </div>
            
            <div class="form-group">
                <label for="scrapeMethod">Scraping Method</label>
                <select id="scrapeMethod" name="scrapeMethod">
                    <option value="Selenium">Selenium (Free)</option>
                    <option value="ScrapeDo">Scrape.do API (Faster)</option>
                </select>
            </div>
            
            <div class="form-group">
                <label for="template">Export Template</label>
                <select id="template" name="template">
                    <option value="">Default (No Template)</option>
                    <option value="trendyol_kettle">☕ Trendyol Kettle (MediaMarkt)</option>
                    <option value="trendyol_laptop">💻 Trendyol Laptop (MediaMarkt)</option>
                    <option value="trendyol_robot_vacuum">🤖 Trendyol Robot Vacuum (MediaMarkt)</option>
                    <option value="trendyol_dryer">👔 Trendyol Dryer (MediaMarkt)</option>
                    <option value="trendyol_klima">❄️ Trendyol Klima (MediaMarkt)</option>
                    <option value="trendyol_water_heater">🔥 Trendyol Şofben (MediaMarkt)</option>
                    <option value="trendyol_cordless_vacuum_cleaner">🧹 Trendyol Cordless Vacuum Cleaner (MediaMarkt)</option>
                </select>
            </div>
            
            <div class="form-group">
                <div class="checkbox-label">
                    <input type="checkbox" id="excludePrice" name="excludePrice">
                    <label for="excludePrice">Exclude Price from Excel</label>
                </div>
                <div class="checkbox-label">
                    <input type="checkbox" id="processImages" name="processImages" checked>
                    <label for="processImages">Process & Upload Images to CDN</label>
                </div>
            </div>
            
            <div class="example" id="exampleUrls">
                <strong>Example URLs:</strong><br>
                • Face Cream: https://www.trendyol.com/yuz-kremi-x-c1122<br>
                • Skincare: https://www.trendyol.com/cilt-bakimi-x-c1121
            </div>
            
            <div class="button-group">
                <button type="submit" id="startBtn">▶️ Start Scraping</button>
                <button type="button" id="stopBtn" onclick="stopScraping()">⏹️ Stop</button>
                <button type="button" id="resetBtn" onclick="resetForm()">🔄 Reset</button>
            </div>
        </form>
        
        <div id="progress">
            <div class="progress-bar">
                <div class="progress-fill" id="progressFill">0%</div>
            </div>
            <div id="status"></div>
        </div>
    </div>
    
    <script>
        let abortController = null;
        let isRunning = false;
        let sessionId = null;
        
        function updateExample() {
            const platform = document.getElementById('platform').value;
            const exampleUrls = document.getElementById('exampleUrls');
            const categoryUrl = document.getElementById('categoryUrl');
            
            if (platform === 'hepsiburada') {
                exampleUrls.innerHTML = '<strong>Example URLs (Hepsiburada):</strong><br>• Tablets: https://www.hepsiburada.com/apple-tablet-xc-3008012-b8849';
                categoryUrl.value = 'https://www.hepsiburada.com/apple-tablet-xc-3008012-b8849';
            } else {
                exampleUrls.innerHTML = '<strong>Example URLs (Trendyol):</strong><br>• Face Cream: https://www.trendyol.com/yuz-kremi-x-c1122';
                categoryUrl.value = 'https://www.trendyol.com/yuz-kremi-x-c1122';
            }
        }
        
        function resetForm() {
            if (isRunning) {
                stopScraping();
            }
            document.getElementById('scraperForm').reset();
            document.getElementById('progress').style.display = 'none';
            document.getElementById('progressFill').style.width = '0%';
            document.getElementById('progressFill').textContent = '0%';
            document.getElementById('status').innerHTML = '';
            document.getElementById('startBtn').disabled = false;
            document.getElementById('startBtn').textContent = '▶️ Start Scraping';
            document.getElementById('stopBtn').style.display = 'none';
            updateExample();
        }
        
        async function stopScraping() {
            if (sessionId) {
                try {
                    await fetch('/api/stop/' + sessionId, { method: 'POST' });
                } catch (e) { console.log('Stop request failed:', e); }
            }
            if (abortController) {
                abortController.abort();
            }
            isRunning = false;
            document.getElementById('startBtn').disabled = false;
            document.getElementById('startBtn').textContent = '▶️ Start Scraping';
            document.getElementById('stopBtn').style.display = 'none';
            
            const statusItem = document.createElement('div');
            statusItem.className = 'status-item warning';
            statusItem.textContent = '⚠️ Scraping stopped by user. Check for partial results below.';
            document.getElementById('status').insertBefore(statusItem, document.getElementById('status').firstChild);
        }
        
        const form = document.getElementById('scraperForm');
        
        form.addEventListener('submit', async (e) => {
            e.preventDefault();
            
            const platform = document.getElementById('platform').value;
            const categoryUrl = document.getElementById('categoryUrl').value;
            const maxProducts = document.getElementById('maxProducts').value;
            const excludePrice = document.getElementById('excludePrice').checked;
            const processImages = document.getElementById('processImages').checked;
            const scrapeMethod = document.getElementById('scrapeMethod').value;
            const templateName = document.getElementById('template').value;
            
            sessionId = Date.now().toString();
            abortController = new AbortController();
            isRunning = true;
            
            document.getElementById('startBtn').disabled = true;
            document.getElementById('startBtn').textContent = 'Scraping...';
            document.getElementById('stopBtn').style.display = 'block';
            document.getElementById('progress').style.display = 'block';
            document.getElementById('status').innerHTML = '<div class="status-item">Starting scraper...</div>';
            
            try {
                const response = await fetch('/api/scrape', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        platform, categoryUrl, maxProducts: parseInt(maxProducts),
                        excludePrice, processImages, scrapeMethod,
                        templateName: templateName || null,
                        sessionId: sessionId
                    }),
                    signal: abortController.signal
                });
                
                if (!response.ok) throw new Error('Scraping failed');
                
                const reader = response.body.getReader();
                const decoder = new TextDecoder();
                
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    
                    const text = decoder.decode(value);
                    const lines = text.split('\n');
                    
                    for (const line of lines) {
                        if (line.startsWith('data: ')) {
                            try {
                                const data = JSON.parse(line.substring(6));
                                
                                if (data.progress !== undefined) {
                                    document.getElementById('progressFill').style.width = data.progress + '%';
                                    document.getElementById('progressFill').textContent = data.progress + '%';
                                }
                                
                                if (data.message) {
                                    const statusItem = document.createElement('div');
                                    statusItem.className = 'status-item';
                                    if (data.type === 'success') statusItem.className += ' success';
                                    else if (data.type === 'error') statusItem.className += ' error';
                                    statusItem.textContent = data.message;
                                    document.getElementById('status').insertBefore(statusItem, document.getElementById('status').firstChild);
                                }
                                
                                if (data.downloadUrl) {
                                    const link = document.createElement('a');
                                    link.href = data.downloadUrl;
                                    link.className = 'download-link';
                                    link.textContent = '📥 Download Excel File';
                                    link.download = data.fileName;
                                    document.getElementById('status').insertBefore(link, document.getElementById('status').firstChild);
                                }
                                
                                if (data.complete) {
                                    isRunning = false;
                                    document.getElementById('startBtn').disabled = false;
                                    document.getElementById('startBtn').textContent = '▶️ Start Scraping';
                                    document.getElementById('stopBtn').style.display = 'none';
                                }
                            } catch (parseErr) { }
                        }
                    }
                }
            } catch (error) {
                if (error.name !== 'AbortError') {
                    document.getElementById('status').innerHTML = '<div class="status-item error">Error: ' + error.message + '</div>';
                }
                isRunning = false;
                document.getElementById('startBtn').disabled = false;
                document.getElementById('startBtn').textContent = '▶️ Start Scraping';
                document.getElementById('stopBtn').style.display = 'none';
            }
        });
    </script>
</body>
</html>
""", "text/html"));

// NEW: Akakce scraper page
app.MapGet("/akakce", () => Results.Content("""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Akakce Scraper - Price Comparison</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        
        body {
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            background: linear-gradient(135deg, #f093fb 0%, #f5576c 100%);
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
        
        h1 { color: #333; margin-bottom: 10px; font-size: 2.5em; }
        .subtitle { color: #666; margin-bottom: 30px; font-size: 1.1em; }
        
        .nav-buttons { display: flex; gap: 15px; margin-bottom: 30px; }
        
        .nav-btn {
            flex: 1;
            padding: 20px;
            border: 2px solid #f5576c;
            border-radius: 12px;
            background: white;
            cursor: pointer;
            transition: all 0.3s;
            text-align: center;
        }
        
        .nav-btn:hover { background: #f5576c; color: white; }
        .nav-btn.active {
            background: linear-gradient(135deg, #f093fb 0%, #f5576c 100%);
            color: white;
            border-color: transparent;
        }
        
        .nav-btn h3 { margin-bottom: 5px; }
        .nav-btn p { font-size: 0.85em; opacity: 0.8; }
        
        .form-group { margin-bottom: 25px; }
        
        label { display: block; margin-bottom: 8px; color: #555; font-weight: 600; }
        
        select, input[type="text"], input[type="number"] {
            width: 100%;
            padding: 12px 15px;
            border: 2px solid #e0e0e0;
            border-radius: 8px;
            font-size: 16px;
        }
        
        select:focus, input:focus {
            outline: none;
            border-color: #f5576c;
            box-shadow: 0 0 0 3px rgba(245, 87, 108, 0.1);
        }
        
        .input-hint { font-size: 0.85em; color: #999; margin-top: 5px; }
        
        /* Input Mode Toggle */
        .mode-toggle {
            display: flex;
            gap: 10px;
            margin-bottom: 20px;
        }
        
        .mode-btn {
            flex: 1;
            padding: 12px;
            border: 2px solid #e0e0e0;
            border-radius: 8px;
            background: white;
            cursor: pointer;
            text-align: center;
            transition: all 0.3s;
            font-weight: 600;
        }
        
        .mode-btn:hover { border-color: #f5576c; }
        .mode-btn.active {
            background: #f5576c;
            color: white;
            border-color: #f5576c;
        }
        
        .file-upload {
            border: 3px dashed #ddd;
            border-radius: 12px;
            padding: 40px;
            text-align: center;
            cursor: pointer;
            transition: all 0.3s;
            background: #fafafa;
        }
        
        .file-upload:hover, .file-upload.dragover {
            border-color: #f5576c;
            background: #fff5f6;
        }
        
        .file-upload input { display: none; }
        .file-upload .icon { font-size: 3em; margin-bottom: 15px; }
        .file-upload .text { color: #666; }
        .file-upload .filename { color: #f5576c; font-weight: 600; margin-top: 10px; }
        
        .input-section { display: none; }
        .input-section.active { display: block; }
        
        .button-group { display: flex; gap: 10px; margin-top: 20px; }
        
        button {
            padding: 15px 40px;
            font-size: 1.1em;
            font-weight: 600;
            border-radius: 8px;
            cursor: pointer;
            transition: all 0.3s;
            border: none;
        }
        
        button:hover:not(:disabled) { transform: translateY(-2px); }
        button:disabled { opacity: 0.6; cursor: not-allowed; }
        
        #akakceStartBtn {
            background: linear-gradient(135deg, #f093fb 0%, #f5576c 100%);
            color: white;
            flex: 2;
        }
        
        #akakceStopBtn {
            background: #f44336;
            color: white;
            flex: 1;
            display: none;
        }
        
        #akakceResetBtn {
            background: #757575;
            color: white;
            flex: 1;
        }
        
        #progress { margin-top: 30px; display: none; }
        
        .progress-bar {
            background: #f0f0f0;
            border-radius: 10px;
            height: 30px;
            overflow: hidden;
            margin-bottom: 15px;
        }
        
        .progress-fill {
            background: linear-gradient(90deg, #f093fb 0%, #f5576c 100%);
            height: 100%;
            width: 0%;
            transition: width 0.3s;
            display: flex;
            align-items: center;
            justify-content: center;
            color: white;
            font-weight: 600;
        }
        
        #status { color: #555; font-size: 0.95em; max-height: 300px; overflow-y: auto; }
        
        .status-item { padding: 8px 0; border-bottom: 1px solid #f0f0f0; }
        .success { color: #4CAF50; font-weight: 600; }
        .error { color: #f44336; font-weight: 600; }
        
        .download-link {
            display: inline-block;
            background: #4CAF50;
            color: white;
            padding: 12px 30px;
            text-decoration: none;
            border-radius: 8px;
            margin-top: 15px;
            font-weight: 600;
        }
        
        .example {
            background: #f8f9fa;
            padding: 15px;
            border-radius: 8px;
            margin: 15px 0;
            font-size: 0.9em;
        }
        
        .example strong { color: #f5576c; }
    </style>
</head>
<body>
    <div class="container">
        <h1>🤑 Akakce Scraper</h1>
        <p class="subtitle">Compare prices across multiple sellers from Akakce</p>
        
        <div class="nav-buttons">
            <div class="nav-btn" onclick="window.location.href='/'">
                <h3>🛒 Category Scraper</h3>
                <p>Trendyol & Hepsiburada</p>
            </div>
            <div class="nav-btn active">
                <h3>💰 Akakce Scraper</h3>
                <p>Price Comparison</p>
            </div>
        </div>
        
        <form id="akakceForm">
            <!-- Input Mode Toggle -->
            <div class="mode-toggle">
                <div class="mode-btn active" onclick="setMode('url')" id="modeUrl">🔗 Category URL</div>
                <div class="mode-btn" onclick="setMode('file')" id="modeFile">📂 Excel File</div>
            </div>
            
            <!-- URL Input Section -->
            <div class="input-section active" id="urlSection">
                <div class="form-group">
                    <label for="categoryUrl">Akakce Category URL</label>
                    <input type="text" id="categoryUrl" 
                        placeholder="https://www.akakce.com/laptop-notebook.html"
                        value="https://www.akakce.com/laptop-notebook.html">
                    <div class="input-hint">Enter an Akakce category page URL to scrape all products</div>
                </div>
                
                <div class="form-group">
                    <label for="maxProducts">Maximum Products</label>
                    <input type="number" id="maxProducts" min="1" max="2000" value="20">
                    <div class="input-hint">Limit: 1-2000 products (will paginate through category pages)</div>
                </div>
                
                <div class="form-group">
                    <label for="startFrom">Start From</label>
                    <input type="number" id="startFrom" min="1" value="1">
                    <div class="input-hint">Start scraping from this product number</div>
                </div>
                
                <div class="example">
                    <strong>Example Category URLs:</strong><br>
                    • Laptops: https://www.akakce.com/laptop-notebook.html<br>
                    • Phones: https://www.akakce.com/cep-telefonu.html<br>
                    • TVs: https://www.akakce.com/televizyon.html
                </div>
            </div>
            
            <!-- File Upload Section -->
            <div class="input-section" id="fileSection">
                <div class="form-group">
                    <label>Upload Excel File with Akakce Product URLs</label>
                    <div class="file-upload" id="dropZone">
                        <input type="file" id="excelFile" accept=".xlsx,.xls">
                        <div class="icon">📁</div>
                        <div class="text">
                            <strong>Click to upload</strong> or drag and drop<br>
                            Excel file (.xlsx, .xls)
                        </div>
                        <div class="filename" id="fileName"></div>
                    </div>
                    <div class="input-hint">Excel should have Akakce product URLs in the first column</div>
                </div>
                
                <div class="example">
                    <strong>Expected Excel Format:</strong><br>
                    Column A should contain product URLs like:<br>
                    • https://www.akakce.com/cep-telefonu/en-ucuz-samsung-galaxy-s25-ultra-fiyati,917781807.html
                </div>
            </div>
            
            <div class="button-group">
                <button type="submit" id="akakceStartBtn">▶️ Start Scraping</button>
                <button type="button" id="akakceStopBtn" onclick="stopScraping()">⏹️ Stop</button>
                <button type="button" id="akakceResetBtn" onclick="resetForm()">🔄 Reset</button>
            </div>
        </form>
        
        <div id="progress">
            <div class="progress-bar">
                <div class="progress-fill" id="progressFill">0%</div>
            </div>
            <div id="status"></div>
        </div>
    </div>
    
    <script>
        let currentMode = 'url';
        let selectedFile = null;
        let abortController = null;
        let isRunning = false;
        let sessionId = null;
        
        function setMode(mode) {
            currentMode = mode;
            document.getElementById('modeUrl').classList.toggle('active', mode === 'url');
            document.getElementById('modeFile').classList.toggle('active', mode === 'file');
            document.getElementById('urlSection').classList.toggle('active', mode === 'url');
            document.getElementById('fileSection').classList.toggle('active', mode === 'file');
            
            // Update button state
            if (mode === 'url') {
                document.getElementById('akakceStartBtn').disabled = false;
            } else {
                document.getElementById('akakceStartBtn').disabled = !selectedFile;
            }
        }
        
        // File upload handlers
        const dropZone = document.getElementById('dropZone');
        const fileInput = document.getElementById('excelFile');
        const fileName = document.getElementById('fileName');
        
        dropZone.addEventListener('click', () => fileInput.click());
        dropZone.addEventListener('dragover', (e) => { e.preventDefault(); dropZone.classList.add('dragover'); });
        dropZone.addEventListener('dragleave', () => dropZone.classList.remove('dragover'));
        dropZone.addEventListener('drop', (e) => {
            e.preventDefault();
            dropZone.classList.remove('dragover');
            if (e.dataTransfer.files.length > 0) handleFile(e.dataTransfer.files[0]);
        });
        fileInput.addEventListener('change', (e) => {
            if (e.target.files.length > 0) handleFile(e.target.files[0]);
        });
        
        function handleFile(file) {
            if (!file.name.match(/\.(xlsx|xls)$/i)) {
                alert('Please upload an Excel file (.xlsx or .xls)');
                return;
            }
            selectedFile = file;
            fileName.textContent = '✓ ' + file.name;
            if (currentMode === 'file') {
                document.getElementById('akakceStartBtn').disabled = false;
            }
        }
        
        function resetForm() {
            if (isRunning) stopScraping();
            selectedFile = null;
            fileInput.value = '';
            fileName.textContent = '';
            document.getElementById('categoryUrl').value = 'https://www.akakce.com/laptop-notebook.html';
            document.getElementById('maxProducts').value = '10';
            document.getElementById('startFrom').value = '1';
            document.getElementById('akakceStartBtn').disabled = false;
            document.getElementById('akakceStartBtn').textContent = '▶️ Start Scraping';
            document.getElementById('akakceStopBtn').style.display = 'none';
            document.getElementById('progress').style.display = 'none';
            document.getElementById('progressFill').style.width = '0%';
            document.getElementById('progressFill').textContent = '0%';
            document.getElementById('status').innerHTML = '';
            setMode('url');
        }
        
        async function stopScraping() {
            if (sessionId) {
                try { await fetch('/api/akakce/stop/' + sessionId, { method: 'POST' }); } catch (e) {}
            }
            if (abortController) abortController.abort();
            isRunning = false;
            document.getElementById('akakceStartBtn').disabled = false;
            document.getElementById('akakceStartBtn').textContent = '▶️ Start Scraping';
            document.getElementById('akakceStopBtn').style.display = 'none';
            
            const statusItem = document.createElement('div');
            statusItem.className = 'status-item warning';
            statusItem.textContent = '⚠️ Scraping stopped. Check for partial results below.';
            document.getElementById('status').insertBefore(statusItem, document.getElementById('status').firstChild);
        }
        
        document.getElementById('akakceForm').addEventListener('submit', async (e) => {
            e.preventDefault();
            
            sessionId = Date.now().toString();
            abortController = new AbortController();
            isRunning = true;
            
            document.getElementById('akakceStartBtn').disabled = true;
            document.getElementById('akakceStartBtn').textContent = 'Processing...';
            document.getElementById('akakceStopBtn').style.display = 'block';
            document.getElementById('progress').style.display = 'block';
            document.getElementById('status').innerHTML = '<div class="status-item">Starting...</div>';
            
            try {
                let response;
                
                if (currentMode === 'url') {
                    // Category URL mode
                    const categoryUrl = document.getElementById('categoryUrl').value;
                    const maxProducts = document.getElementById('maxProducts').value;
                    const startFrom = document.getElementById('startFrom').value;
                    
                    response = await fetch('/api/akakce/scrape-category', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            categoryUrl: categoryUrl,
                            maxProducts: parseInt(maxProducts),
                            sessionId: sessionId,
                            startFrom: parseInt(startFrom)
                        }),
                        signal: abortController.signal
                    });
                } else {
                    // File upload mode
                    if (!selectedFile) return;
                    
                    const formData = new FormData();
                    formData.append('file', selectedFile);
                    formData.append('scrapeMethod', 'Selenium');
                    formData.append('sessionId', sessionId);
                    
                    response = await fetch('/api/akakce/scrape', {
                        method: 'POST',
                        body: formData,
                        signal: abortController.signal
                    });
                }
                
                if (!response.ok) throw new Error('Scraping failed');
                
                const reader = response.body.getReader();
                const decoder = new TextDecoder();
                
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    
                    const text = decoder.decode(value);
                    const lines = text.split('\n');
                    
                    for (const line of lines) {
                        if (line.startsWith('data: ')) {
                            try {
                                const data = JSON.parse(line.substring(6));
                                
                                if (data.progress !== undefined) {
                                    document.getElementById('progressFill').style.width = data.progress + '%';
                                    document.getElementById('progressFill').textContent = data.progress + '%';
                                }
                                
                                if (data.message) {
                                    const statusItem = document.createElement('div');
                                    statusItem.className = 'status-item';
                                    if (data.type === 'success') statusItem.className += ' success';
                                    else if (data.type === 'error') statusItem.className += ' error';
                                    statusItem.textContent = data.message;
                                    document.getElementById('status').insertBefore(statusItem, document.getElementById('status').firstChild);
                                }
                                
                                if (data.downloadUrl) {
                                    const link = document.createElement('a');
                                    link.href = data.downloadUrl;
                                    link.className = 'download-link';
                                    link.textContent = '📥 Download Results';
                                    link.download = data.fileName;
                                    document.getElementById('status').insertBefore(link, document.getElementById('status').firstChild);
                                }
                                
                                if (data.complete) {
                                    isRunning = false;
                                    document.getElementById('akakceStartBtn').disabled = false;
                                    document.getElementById('akakceStartBtn').textContent = '▶️ Start Scraping';
                                    document.getElementById('akakceStopBtn').style.display = 'none';
                                }
                            } catch (parseErr) {}
                        }
                    }
                }
            } catch (error) {
                if (error.name !== 'AbortError') {
                    document.getElementById('status').innerHTML = '<div class="status-item error">Error: ' + error.message + '</div>';
                }
                isRunning = false;
                document.getElementById('akakceStartBtn').disabled = false;
                document.getElementById('akakceStartBtn').textContent = '▶️ Start Scraping';
                document.getElementById('akakceStopBtn').style.display = 'none';
            }
        });
    </script>
</body>
</html>
""", "text/html"));

// Stop endpoints for cancelling scraping sessions
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
    Console.WriteLine($"[API] Akakce stop requested for session: {sessionId}");
    return Results.Ok(new { message = "Stop signal sent" });
});

// NEW: Akakce category URL scraping endpoint
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
                },
                request.SessionId,
                request.StartFrom
            );
        }
        catch (Exception ex)
        {
            var errorData = System.Text.Json.JsonSerializer.Serialize(new
            {
                progress = 100,
                message = $"Error: {ex.Message}",
                type = "error",
                complete = true
            });
            await writer.WriteLineAsync($"data: {errorData}\n");
            await writer.FlushAsync();
        }
    }, "text/event-stream");
});

// NEW: Akakce scraping API endpoint with file upload
app.MapPost("/api/akakce/scrape", async (HttpRequest request, AkakceScraperService akakceService) =>
{
    return Results.Stream(async (stream) =>
    {
        var writer = new StreamWriter(stream);
        
        try
        {
            // Get the uploaded file
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var scrapeMethodStr = form["scrapeMethod"].ToString();
            var sessionId = form["sessionId"].ToString();
            
            if (file == null || file.Length == 0)
            {
                var errorData = System.Text.Json.JsonSerializer.Serialize(new
                {
                    progress = 100,
                    message = "No file uploaded",
                    type = "error",
                    complete = true
                });
                await writer.WriteLineAsync($"data: {errorData}\n");
                await writer.FlushAsync();
                return;
            }
            
            // Parse scrape method
            var scrapeMethod = scrapeMethodStr.ToLower() == "scrapedo" 
                ? Scrapper.Models.ScrapeMethod.ScrapeDo 
                : Scrapper.Models.ScrapeMethod.Selenium;
            
            // Process the file
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;
            
            await akakceService.ProcessExcelFileAsync(
                memoryStream,
                scrapeMethod,
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
                },
                sessionId
            );
        }
        catch (Exception ex)
        {
            var errorData = System.Text.Json.JsonSerializer.Serialize(new
            {
                progress = 100,
                message = $"Error: {ex.Message}",
                type = "error",
                complete = true
            });
            await writer.WriteLineAsync($"data: {errorData}\n");
            await writer.FlushAsync();
        }
    }, "text/event-stream");
});

// API endpoint for scraping (original)
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
                },
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
                },
                request.SessionId
            );
        }
    }, "text/event-stream");
});

// Get available templates
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

Console.WriteLine("🔧 Scrapper Web Application");
Console.WriteLine("🌐 Open your browser and navigate to: http://localhost:5000");
Console.WriteLine("   - Category Scraper: http://localhost:5000/");
Console.WriteLine("   - Akakce Scraper: http://localhost:5000/akakce");
Console.WriteLine("Press Ctrl+C to stop the server");

app.Run("http://localhost:5000");

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
    int StartFrom = 1
);

