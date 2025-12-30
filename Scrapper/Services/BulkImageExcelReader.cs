using OfficeOpenXml;

namespace Scrapper.Services;

/// <summary>
/// Reads image URLs from an Excel file for bulk processing
/// Preserves the original structure and identifies cells with image URLs
/// Supports multiple columns with images while leaving non-image columns untouched
/// </summary>
public class BulkImageExcelReader
{
    /// <summary>
    /// Represents an image URL found in the Excel file
    /// </summary>
    public class ImageCell
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public string OriginalUrl { get; set; } = string.Empty;
        public string? CdnUrl { get; set; }
        public bool IsProcessed { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Represents the entire Excel file with all cells
    /// </summary>
    public class ExcelData
    {
        public List<List<string?>> AllCells { get; set; } = new();
        public List<ImageCell> ImageCells { get; set; } = new();
        public int TotalRows { get; set; }
        public int TotalColumns { get; set; }
        public List<string> Headers { get; set; } = new();
        public bool HasHeader { get; set; }
        
        /// <summary>
        /// Tracks which columns contain images
        /// </summary>
        public HashSet<int> ImageColumns { get; set; } = new();
        
        /// <summary>
        /// Tracks which columns contain non-image data (should be preserved)
        /// </summary>
        public HashSet<int> DataColumns { get; set; } = new();
    }

    /// <summary>
    /// Read Excel file and identify all image URLs across all columns
    /// Non-image columns are preserved exactly as they are
    /// </summary>
    public ExcelData ReadExcel(Stream stream, bool hasHeader = true)
    {
        var data = new ExcelData { HasHeader = hasHeader };

        try
        {
            using var package = new ExcelPackage(stream);
            var worksheet = package.Workbook.Worksheets.FirstOrDefault();
            
            if (worksheet == null)
            {
                Console.WriteLine("[BulkImageReader] No worksheet found");
                return data;
            }

            var dimension = worksheet.Dimension;
            if (dimension == null)
            {
                Console.WriteLine("[BulkImageReader] Worksheet is empty");
                return data;
            }

            data.TotalRows = dimension.End.Row;
            data.TotalColumns = dimension.End.Column;

            Console.WriteLine($"[BulkImageReader] ========================================");
            Console.WriteLine($"[BulkImageReader] Reading Excel: {data.TotalRows} rows x {data.TotalColumns} columns");

            // Read all cells and identify image URLs
            for (int row = 1; row <= data.TotalRows; row++)
            {
                var rowData = new List<string?>();
                
                for (int col = 1; col <= data.TotalColumns; col++)
                {
                    var cellValue = worksheet.Cells[row, col].Value?.ToString()?.Trim();
                    rowData.Add(cellValue);

                    // Track which columns have data
                    if (!string.IsNullOrWhiteSpace(cellValue))
                    {
                        data.DataColumns.Add(col);
                    }

                    // Check if this cell contains an image URL
                    if (!string.IsNullOrWhiteSpace(cellValue) && IsImageUrl(cellValue))
                    {
                        // Skip header row for image processing
                        if (hasHeader && row == 1)
                        {
                            // Mark as image column
                            data.ImageColumns.Add(col);
                            
                            // Store header
                            if (data.Headers.Count < col)
                            {
                                for (int i = data.Headers.Count; i < col; i++)
                                    data.Headers.Add($"Column{i + 1}");
                            }
                            data.Headers[col - 1] = cellValue;
                            continue;
                        }

                        // Add to image cells for processing
                        data.ImageCells.Add(new ImageCell
                        {
                            Row = row,
                            Column = col,
                            OriginalUrl = cellValue
                        });
                        
                        // Mark as image column
                        data.ImageColumns.Add(col);
                    }
                }

                data.AllCells.Add(rowData);
            }

            // Collect headers from first row if hasHeader
            if (hasHeader && data.AllCells.Count > 0)
            {
                data.Headers = data.AllCells[0]
                    .Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Column{i + 1}" : h!)
                    .ToList();
            }

            Console.WriteLine($"[BulkImageReader] Found {data.ImageCells.Count} image URLs across {data.ImageColumns.Count} columns");
            Console.WriteLine($"[BulkImageReader] Image columns: {string.Join(", ", data.ImageColumns.OrderBy(x => x))}");
            Console.WriteLine($"[BulkImageReader] Data columns (preserved): {string.Join(", ", data.DataColumns.Except(data.ImageColumns).OrderBy(x => x))}");
            
            // Log sample URLs from different columns
            var samplesByColumn = data.ImageCells
                .GroupBy(img => img.Column)
                .OrderBy(g => g.Key)
                .Take(5);
            
            foreach (var columnGroup in samplesByColumn)
            {
                var sample = columnGroup.First();
                var header = hasHeader && data.Headers.Count >= sample.Column ? data.Headers[sample.Column - 1] : $"Col{sample.Column}";
                Console.WriteLine($"[BulkImageReader] Sample from {header} (Col {sample.Column}): {sample.OriginalUrl.Substring(0, Math.Min(60, sample.OriginalUrl.Length))}...");
            }
            
            Console.WriteLine($"[BulkImageReader] ========================================");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BulkImageReader] Error reading Excel: {ex.Message}");
            throw;
        }

        return data;
    }

    /// <summary>
    /// Enhanced check if a string is a valid image URL
    /// Detects URLs from various e-commerce platforms and CDN services
    /// </summary>
    private bool IsImageUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmedValue = value.Trim();

        // Must start with http:// or https://
        if (!trimmedValue.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmedValue.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        var lowerValue = trimmedValue.ToLower();
        
        // === PATTERN 1: Common image file extensions ===
        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".avif", ".tif", ".tiff" };
        if (imageExtensions.Any(ext => lowerValue.EndsWith(ext)))
            return true;

        // === PATTERN 2: E-commerce CDN patterns ===
        // Turkish e-commerce sites
        if (lowerValue.Contains("dsmcdn.com") ||          // Trendyol
            lowerValue.Contains("mncdn.com") ||           // MediaMarkt Turkey
            lowerValue.Contains("hepsiburada.net") ||     // Hepsiburada
            lowerValue.Contains("trendyol.com") ||        // Trendyol direct
            lowerValue.Contains("akakce.com") ||          // Akakce
            lowerValue.Contains("n11cdn.com") ||          // N11
            lowerValue.Contains("gittigidiyor.com"))      // GittiGidiyor
            return true;

        // International e-commerce
        if (lowerValue.Contains("amazon") ||
            lowerValue.Contains("aliexpress") ||
            lowerValue.Contains("ebay") ||
            lowerValue.Contains("shopify"))
            return true;

        // === PATTERN 3: Common CDN service providers ===
        if (lowerValue.Contains("cloudinary.com") ||      // Cloudinary
            lowerValue.Contains("imgix.net") ||           // Imgix
            lowerValue.Contains("cloudfront.net") ||      // AWS CloudFront
            lowerValue.Contains("akamaized.net") ||       // Akamai
            lowerValue.Contains("fastly.net") ||          // Fastly
            lowerValue.Contains("cloudflare.com"))        // Cloudflare
            return true;

        // === PATTERN 4: Generic CDN/image subdomains ===
        if (lowerValue.Contains("cdn.") ||
            lowerValue.Contains("images.") ||
            lowerValue.Contains("img.") ||
            lowerValue.Contains("static.") ||
            lowerValue.Contains("media.") ||
            lowerValue.Contains("assets."))
            return true;

        // === PATTERN 5: URL path contains image-related segments ===
        if (lowerValue.Contains("/image") ||
            lowerValue.Contains("/images/") ||
            lowerValue.Contains("/img/") ||
            lowerValue.Contains("/photo") ||
            lowerValue.Contains("/pictures") ||
            lowerValue.Contains("/product") ||
            lowerValue.Contains("/products/") ||
            lowerValue.Contains("/media/") ||
            lowerValue.Contains("/assets/") ||
            lowerValue.Contains("/uploads/") ||
            lowerValue.Contains("/gallery/"))
            return true;

        // === PATTERN 6: Query parameters suggest image ===
        if (lowerValue.Contains("?image") ||
            lowerValue.Contains("&image") ||
            lowerValue.Contains("?img") ||
            lowerValue.Contains("&img") ||
            lowerValue.Contains("?photo") ||
            lowerValue.Contains("&photo"))
            return true;

        // === PATTERN 7: Product image patterns (with numbers) ===
        // Many e-commerce sites use patterns like: /p123456789_image_1.jpg or /product-12345.html?img=1
        if (System.Text.RegularExpressions.Regex.IsMatch(lowerValue, @"/p\d+.*image|/product.*\d+|/item.*\d+"))
            return true;

        return false;
    }
}
