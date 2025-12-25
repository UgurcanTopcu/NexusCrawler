using OfficeOpenXml;

namespace Scrapper.Services;

/// <summary>
/// Service to read Akakce product URLs from an Excel file
/// </summary>
public class AkakceExcelReader
{
    static AkakceExcelReader()
    {
        ExcelPackage.License.SetNonCommercialPersonal("Your Name");
    }

    /// <summary>
    /// Read product URLs from an Excel file
    /// </summary>
    /// <param name="filePath">Path to the Excel file</param>
    /// <param name="urlColumnIndex">Column index containing URLs (1-based, default is 1 for column A)</param>
    /// <param name="hasHeader">Whether the first row is a header</param>
    /// <returns>List of valid Akakce URLs</returns>
    public List<string> ReadUrls(string filePath, int urlColumnIndex = 1, bool hasHeader = true)
    {
        var urls = new List<string>();
        var invalidUrls = new List<string>();

        try
        {
            Console.WriteLine($"[Excel Reader] Opening file: {filePath}");
            
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException($"Excel file not found: {filePath}");
            }

            using var package = new ExcelPackage(fileInfo);
            
            // Get the first worksheet
            var worksheet = package.Workbook.Worksheets.FirstOrDefault();
            if (worksheet == null)
            {
                throw new Exception("No worksheets found in the Excel file");
            }

            Console.WriteLine($"[Excel Reader] Reading worksheet: {worksheet.Name}");

            // Determine the range
            var dimension = worksheet.Dimension;
            if (dimension == null)
            {
                Console.WriteLine("[Excel Reader] Worksheet is empty");
                return urls;
            }

            int startRow = hasHeader ? 2 : 1;
            int endRow = dimension.End.Row;

            Console.WriteLine($"[Excel Reader] Processing rows {startRow} to {endRow}");

            for (int row = startRow; row <= endRow; row++)
            {
                var cellValue = worksheet.Cells[row, urlColumnIndex].Value?.ToString()?.Trim();
                
                if (string.IsNullOrWhiteSpace(cellValue))
                    continue;

                // Validate URL
                if (AkakceScraper.IsValidAkakceUrl(cellValue))
                {
                    urls.Add(cellValue);
                }
                else
                {
                    invalidUrls.Add(cellValue);
                    Console.WriteLine($"[Excel Reader] Row {row}: Invalid URL - {cellValue.Substring(0, Math.Min(50, cellValue.Length))}...");
                }
            }

            Console.WriteLine($"\n[Excel Reader] ========== SUMMARY ==========");
            Console.WriteLine($"[Excel Reader] Valid URLs found: {urls.Count}");
            Console.WriteLine($"[Excel Reader] Invalid URLs skipped: {invalidUrls.Count}");
            Console.WriteLine($"[Excel Reader] ==============================\n");

            if (invalidUrls.Count > 0 && invalidUrls.Count <= 5)
            {
                Console.WriteLine("[Excel Reader] Invalid URLs:");
                foreach (var url in invalidUrls)
                {
                    Console.WriteLine($"  - {url}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Excel Reader] Error reading file: {ex.Message}");
            throw;
        }

        return urls;
    }

    /// <summary>
    /// Read URLs from a stream (for file uploads)
    /// </summary>
    public List<string> ReadUrlsFromStream(Stream stream, int urlColumnIndex = 1, bool hasHeader = true)
    {
        var urls = new List<string>();
        var invalidUrls = new List<string>();

        try
        {
            using var package = new ExcelPackage(stream);
            
            var worksheet = package.Workbook.Worksheets.FirstOrDefault();
            if (worksheet == null)
            {
                throw new Exception("No worksheets found in the Excel file");
            }

            Console.WriteLine($"[Excel Reader] Reading worksheet from stream: {worksheet.Name}");

            var dimension = worksheet.Dimension;
            if (dimension == null)
            {
                Console.WriteLine("[Excel Reader] Worksheet is empty");
                return urls;
            }

            int startRow = hasHeader ? 2 : 1;
            int endRow = dimension.End.Row;

            Console.WriteLine($"[Excel Reader] Processing rows {startRow} to {endRow}");

            for (int row = startRow; row <= endRow; row++)
            {
                var cellValue = worksheet.Cells[row, urlColumnIndex].Value?.ToString()?.Trim();
                
                if (string.IsNullOrWhiteSpace(cellValue))
                    continue;

                if (AkakceScraper.IsValidAkakceUrl(cellValue))
                {
                    urls.Add(cellValue);
                }
                else
                {
                    invalidUrls.Add(cellValue);
                }
            }

            Console.WriteLine($"[Excel Reader] Valid: {urls.Count}, Invalid: {invalidUrls.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Excel Reader] Error reading stream: {ex.Message}");
            throw;
        }

        return urls;
    }

    /// <summary>
    /// Auto-detect which column contains URLs
    /// </summary>
    public int DetectUrlColumn(Stream stream, bool hasHeader = true)
    {
        try
        {
            using var package = new ExcelPackage(stream);
            var worksheet = package.Workbook.Worksheets.FirstOrDefault();
            
            if (worksheet == null)
                return 1;

            var dimension = worksheet.Dimension;
            if (dimension == null)
                return 1;

            int checkRow = hasHeader ? 2 : 1;
            
            // Check each column in the first data row
            for (int col = 1; col <= Math.Min(dimension.End.Column, 10); col++)
            {
                var cellValue = worksheet.Cells[checkRow, col].Value?.ToString()?.Trim();
                
                if (!string.IsNullOrWhiteSpace(cellValue) && 
                    AkakceScraper.IsValidAkakceUrl(cellValue))
                {
                    Console.WriteLine($"[Excel Reader] Auto-detected URL column: {col}");
                    return col;
                }
            }

            // Also check header row for hints
            if (hasHeader)
            {
                for (int col = 1; col <= Math.Min(dimension.End.Column, 10); col++)
                {
                    var header = worksheet.Cells[1, col].Value?.ToString()?.Trim()?.ToLower();
                    if (header != null && (header.Contains("url") || header.Contains("link") || header.Contains("adres")))
                    {
                        Console.WriteLine($"[Excel Reader] Detected URL column from header: {col}");
                        return col;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Excel Reader] Error detecting column: {ex.Message}");
        }

        return 1; // Default to first column
    }
}
