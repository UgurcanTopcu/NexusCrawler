# ?? CDN Image Folder Structure Implementation

## ? Solution: Site + Product ID Structure

We've implemented a hierarchical folder structure for organizing product images on the CDN:

```
/ftproot/
??? trendyol/
?   ??? 123456789/
?   ?   ??? image_1.jpg  (main image)
?   ?   ??? image_2.jpg
?   ?   ??? image_3.jpg
?   ??? 987654321/
?   ?   ??? image_1.jpg
?   ?   ??? image_2.jpg
?   ??? ...
??? hepsiburada/
?   ??? HBCV0000870EF8/
?   ?   ??? image_1.jpg
?   ?   ??? image_2.jpg
?   ?   ??? image_3.jpg
?   ??? ...
```

## ?? Why This Solution?

### ? Advantages:
1. **Unique & Reliable**: Product IDs are always present in URLs
   - Trendyol: `https://www.trendyol.com/product-name-p-123456789`
   - Hepsiburada: `https://www.hepsiburada.com/product-name-p-HBCV0000870EF8`

2. **Organized by Platform**: Easy to manage and maintain
   - Separate folders for each e-commerce site
   - Clear visual distinction

3. **Prevents Duplicates**: Same product won't be uploaded twice
   - CDN cache checks `site/productId` before uploading

4. **Simple Filenames**: Sequential numbering (image_1, image_2, ...)
   - No need for timestamps or complex names
   - Clean and predictable

5. **Better than Barcode**:
   - Many products don't have barcodes
   - Barcode extraction is unreliable
   - Product ID is always available

## ?? Implementation Details

### 1. ProductInfo Model (ProductInfo.cs)
```csharp
public class ProductInfo
{
    // NEW: Product identification
    public string ProductId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // "trendyol" or "hepsiburada"
    
    // ...existing properties...
}
```

### 2. Product ID Extraction

**TrendyolScraper.cs**:
```csharp
// Extract from URL: https://www.trendyol.com/product-name-p-123456789
var match = Regex.Match(productUrl, @"-p-(\d+)", RegexOptions.IgnoreCase);
if (match.Success)
{
    product.ProductId = match.Groups[1].Value; // "123456789"
    product.Source = "trendyol";
}
```

**HepsiburadaScraper.cs**:
```csharp
// Extract from URL: https://www.hepsiburada.com/product-name-p-HBCV0000870EF8
var match = Regex.Match(productUrl, @"-(pm?)-([A-Z0-9]+)$", RegexOptions.IgnoreCase);
if (match.Success)
{
    product.ProductId = match.Groups[2].Value; // "HBCV0000870EF8"
    product.Source = "hepsiburada";
}
```

### 3. FTP Upload with Folder Structure (FtpUploadService.cs)
```csharp
public async Task<string?> UploadImageAsync(
    byte[] imageData, 
    string fileName, 
    string site = "",      // "trendyol" or "hepsiburada"
    string productId = "", // "123456789" or "HBCV0000870EF8"
    Action<int>? onProgress = null)
{
    // Build path: /ftproot/trendyol/123456789/image_1.jpg
    string remotePath = _config.RemotePath ?? "/";
    
    if (!string.IsNullOrEmpty(site))
    {
        remotePath = $"{remotePath.TrimEnd('/')}/{site}";
        // Create site directory if needed
        session.CreateDirectory(remotePath);
    }
    
    if (!string.IsNullOrEmpty(productId))
    {
        remotePath = $"{remotePath}/{productId}";
        // Create product directory if needed
        session.CreateDirectory(remotePath);
    }
    
    var remoteFilePath = $"{remotePath}/{fileName}";
    // Upload to: /ftproot/trendyol/123456789/image_1.jpg
}
```

### 4. Image Processing (ImageProcessingService.cs)
```csharp
public async Task<string?> ProcessAndUploadImageAsync(
    string imageUrl, 
    ProductInfo product,  // Pass entire product object
    int imageIndex = 0)
{
    var fileName = $"image_{imageIndex + 1}.jpg";
    
    var cdnUrl = await _ftpService.UploadImageAsync(
        resizedData, 
        fileName, 
        product.Source,    // "trendyol"
        product.ProductId  // "123456789"
    );
    
    return cdnUrl;
}
```

### 5. CDN Cache Check (CdnCacheService.cs)
```csharp
public string GenerateCdnUrl(string site, string productId, int imageIndex)
{
    var fileName = $"image_{imageIndex + 1}.jpg";
    var remotePath = _config.RemotePath ?? "/";
    
    // Generate: https://mmstr.sm.mncdn.com/images/products/trendyol/123456789/image_1.jpg
    return $"{_config.BaseUrl}{remotePath.TrimEnd('/')}/{site}/{productId}/{fileName}";
}

public async Task<(string? mainImage, List<string> additionalImages)> FindExistingImagesAsync(
    string site,
    string productId, 
    int maxImagesToCheck = 10)
{
    // Check if images already exist on CDN
    // URL: https://mmstr.sm.mncdn.com/images/products/trendyol/123456789/image_1.jpg
    var mainUrl = GenerateCdnUrl(site, productId, 0);
    if (await ImageExistsAsync(mainUrl))
    {
        // Image exists, return it without re-uploading
    }
}
```

## ?? Example CDN URLs

### Trendyol Product
```
Product URL: https://www.trendyol.com/maybelline-new-york-fit-me-fondoten-p-123456
Product ID: 123456
Source: trendyol

CDN URLs:
https://mmstr.sm.mncdn.com/images/products/trendyol/123456/image_1.jpg
https://mmstr.sm.mncdn.com/images/products/trendyol/123456/image_2.jpg
https://mmstr.sm.mncdn.com/images/products/trendyol/123456/image_3.jpg
```

### Hepsiburada Product
```
Product URL: https://www.hepsiburada.com/apple-ipad-air-p-HBCV0000870EF8
Product ID: HBCV0000870EF8
Source: hepsiburada

CDN URLs:
https://mmstr.sm.mncdn.com/images/products/hepsiburada/HBCV0000870EF8/image_1.jpg
https://mmstr.sm.mncdn.com/images/products/hepsiburada/HBCV0000870EF8/image_2.jpg
```

## ?? Workflow

1. **Scrape Product**: Extract product details including URL
2. **Extract ID**: Parse product ID from URL using regex
3. **Check Cache**: Look for existing images on CDN at `site/productId/`
4. **Upload if Needed**: If not found, download ? resize ? upload to `site/productId/image_N.jpg`
5. **Store CDN URL**: Save final CDN URL in ProductInfo

## ?? Benefits in Practice

### Before (Flat Structure):
```
/ftproot/maybelline_fit_me_fondoten_20241215_143052_1.jpg
/ftproot/maybelline_fit_me_fondoten_20241215_143052_2.jpg
```
? Hard to find specific products
? Duplicate uploads possible
? No organization by platform

### After (Hierarchical Structure):
```
/ftproot/trendyol/123456/image_1.jpg
/ftproot/trendyol/123456/image_2.jpg
```
? Easy to locate by product ID
? Automatic duplicate prevention
? Organized by platform
? Clean, predictable naming

## ?? Notes

- Product ID extraction happens automatically during scraping
- FTP service creates folders automatically if they don't exist
- CDN cache prevents re-uploading same product images
- Filenames are simplified (no timestamps, no product names)
- Both Selenium and Scrape.do methods supported

## ?? Result

A clean, organized, and efficient CDN storage system that:
- Prevents duplicate uploads
- Makes images easy to find
- Organizes by platform automatically
- Uses reliable product IDs instead of unreliable barcodes
