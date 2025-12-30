using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Drawing;

namespace Scrapper.Services;

/// <summary>
/// Exports the processed Excel with CDN URLs replacing original image URLs
/// Preserves the original structure and only updates cells that were processed
/// Non-image columns remain completely untouched
/// </summary>
public class BulkImageExcelExporter
{
    public void Export(BulkImageExcelReader.ExcelData excelData, string filePath)
    {
        try
        {
            Console.WriteLine($"[BulkImageExporter] ========================================");
            Console.WriteLine($"[BulkImageExporter] Creating Excel: {filePath}");
            Console.WriteLine($"[BulkImageExporter] Rows: {excelData.AllCells.Count}, Columns: {excelData.TotalColumns}");
            Console.WriteLine($"[BulkImageExporter] Image columns: {excelData.ImageColumns.Count} ({string.Join(", ", excelData.ImageColumns.OrderBy(x => x))})");
            Console.WriteLine($"[BulkImageExporter] Processed images: {excelData.ImageCells.Count}");

            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("Processed Images");

            // Write ALL cells preserving original structure - no columns are skipped
            for (int row = 0; row < excelData.AllCells.Count; row++)
            {
                var rowData = excelData.AllCells[row];
                for (int col = 0; col < rowData.Count; col++)
                {
                    var value = rowData[col];
                    if (!string.IsNullOrEmpty(value))
                    {
                        worksheet.Cells[row + 1, col + 1].Value = value;
                    }
                }
            }

            // Style header row if present
            if (excelData.HasHeader && excelData.AllCells.Count > 0)
            {
                using var headerRange = worksheet.Cells[1, 1, 1, excelData.TotalColumns];
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
                headerRange.Style.Fill.BackgroundColor.SetColor(Color.LightBlue);
                headerRange.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            }

            // Highlight ONLY the cells that were processed (image URLs)
            // Non-image cells remain with default white background
            int successCount = 0;
            int failCount = 0;

            foreach (var imageCell in excelData.ImageCells)
            {
                var cell = worksheet.Cells[imageCell.Row, imageCell.Column];
                
                if (imageCell.IsProcessed && !string.IsNullOrEmpty(imageCell.CdnUrl))
                {
                    // Successfully processed - replace with CDN URL and light green background
                    cell.Value = imageCell.CdnUrl;
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    cell.Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
                    cell.Style.Font.Color.SetColor(Color.DarkGreen);
                    successCount++;
                    
                    Console.WriteLine($"[BulkImageExporter] ? Row {imageCell.Row}, Col {imageCell.Column}: {imageCell.CdnUrl.Substring(0, Math.Min(50, imageCell.CdnUrl.Length))}...");
                }
                else if (imageCell.IsProcessed && !string.IsNullOrEmpty(imageCell.Error))
                {
                    // Failed - keep original URL, light red background, add error comment
                    cell.Value = imageCell.OriginalUrl;
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    cell.Style.Fill.BackgroundColor.SetColor(Color.LightCoral);
                    cell.Style.Font.Color.SetColor(Color.DarkRed);
                    
                    // Add error as comment
                    var comment = cell.AddComment($"? Error: {imageCell.Error}\n\nOriginal URL kept: {imageCell.OriginalUrl}");
                    comment.AutoFit = true;
                    failCount++;
                    
                    Console.WriteLine($"[BulkImageExporter] ? Row {imageCell.Row}, Col {imageCell.Column}: {imageCell.Error}");
                }
            }

            // Add summary sheet with detailed statistics
            var summarySheet = package.Workbook.Worksheets.Add("Summary");
            int summaryRow = 1;
            
            // Title
            summarySheet.Cells[summaryRow, 1].Value = "?? Bulk Image Processing Summary";
            summarySheet.Cells[summaryRow, 1].Style.Font.Bold = true;
            summarySheet.Cells[summaryRow, 1].Style.Font.Size = 16;
            summaryRow += 2;

            // Processing statistics
            summarySheet.Cells[summaryRow, 1].Value = "Total Images Found:";
            summarySheet.Cells[summaryRow, 2].Value = excelData.ImageCells.Count;
            summarySheet.Cells[summaryRow, 2].Style.Font.Bold = true;
            summaryRow++;

            summarySheet.Cells[summaryRow, 1].Value = "Successfully Processed:";
            summarySheet.Cells[summaryRow, 2].Value = successCount;
            summarySheet.Cells[summaryRow, 2].Style.Font.Color.SetColor(Color.Green);
            summarySheet.Cells[summaryRow, 2].Style.Font.Bold = true;
            summaryRow++;

            summarySheet.Cells[summaryRow, 1].Value = "Failed:";
            summarySheet.Cells[summaryRow, 2].Value = failCount;
            if (failCount > 0)
            {
                summarySheet.Cells[summaryRow, 2].Style.Font.Color.SetColor(Color.Red);
                summarySheet.Cells[summaryRow, 2].Style.Font.Bold = true;
            }
            summaryRow += 2;

            // Column information
            summarySheet.Cells[summaryRow, 1].Value = "Total Columns in File:";
            summarySheet.Cells[summaryRow, 2].Value = excelData.TotalColumns;
            summaryRow++;
            
            summarySheet.Cells[summaryRow, 1].Value = "Image Columns (processed):";
            summarySheet.Cells[summaryRow, 2].Value = string.Join(", ", excelData.ImageColumns.OrderBy(x => x));
            summarySheet.Cells[summaryRow, 2].Style.Font.Color.SetColor(Color.Green);
            summaryRow++;
            
            var dataOnlyColumns = excelData.DataColumns.Except(excelData.ImageColumns).OrderBy(x => x).ToList();
            summarySheet.Cells[summaryRow, 1].Value = "Data Columns (unchanged):";
            summarySheet.Cells[summaryRow, 2].Value = dataOnlyColumns.Any() ? string.Join(", ", dataOnlyColumns) : "None";
            summarySheet.Cells[summaryRow, 2].Style.Font.Color.SetColor(Color.Blue);
            summaryRow += 2;

            // Processing details
            summarySheet.Cells[summaryRow, 1].Value = "Processing Date:";
            summarySheet.Cells[summaryRow, 2].Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            summaryRow++;

            summarySheet.Cells[summaryRow, 1].Value = "Total Rows:";
            summarySheet.Cells[summaryRow, 2].Value = excelData.TotalRows;
            summaryRow += 2;

            // Legend
            summarySheet.Cells[summaryRow, 1].Value = "Legend:";
            summarySheet.Cells[summaryRow, 1].Style.Font.Bold = true;
            summaryRow++;
            
            summarySheet.Cells[summaryRow, 1].Value = "? Green cells = Successfully uploaded to CDN (URL replaced)";
            summaryRow++;
            
            summarySheet.Cells[summaryRow, 1].Value = "? Red cells = Failed (original URL kept, hover for error)";
            summaryRow++;
            
            summarySheet.Cells[summaryRow, 1].Value = "? White cells = Non-image data (preserved exactly as is)";
            summaryRow++;
            
            summarySheet.Cells[summaryRow, 1].Value = "?? Blue header = Original header row";
            summaryRow += 2;

            // Column breakdown
            if (excelData.ImageColumns.Count > 0)
            {
                summarySheet.Cells[summaryRow, 1].Value = "?? Processed Columns Detail:";
                summarySheet.Cells[summaryRow, 1].Style.Font.Bold = true;
                summaryRow++;
                
                summarySheet.Cells[summaryRow, 1].Value = "Column #";
                summarySheet.Cells[summaryRow, 2].Value = "Header Name";
                summarySheet.Cells[summaryRow, 3].Value = "Images Found";
                summarySheet.Cells[summaryRow, 4].Value = "Success";
                summarySheet.Cells[summaryRow, 5].Value = "Failed";
                
                using var detailHeaderRange = summarySheet.Cells[summaryRow, 1, summaryRow, 5];
                detailHeaderRange.Style.Font.Bold = true;
                detailHeaderRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
                detailHeaderRange.Style.Fill.BackgroundColor.SetColor(Color.LightGray);
                summaryRow++;

                foreach (var col in excelData.ImageColumns.OrderBy(x => x))
                {
                    var imagesInCol = excelData.ImageCells.Where(img => img.Column == col).ToList();
                    var successInCol = imagesInCol.Count(img => img.IsProcessed && !string.IsNullOrEmpty(img.CdnUrl));
                    var failedInCol = imagesInCol.Count(img => img.IsProcessed && !string.IsNullOrEmpty(img.Error));
                    
                    var header = excelData.HasHeader && excelData.Headers.Count >= col 
                        ? excelData.Headers[col - 1] 
                        : $"Column {col}";
                    
                    summarySheet.Cells[summaryRow, 1].Value = col;
                    summarySheet.Cells[summaryRow, 2].Value = header;
                    summarySheet.Cells[summaryRow, 3].Value = imagesInCol.Count;
                    summarySheet.Cells[summaryRow, 4].Value = successInCol;
                    summarySheet.Cells[summaryRow, 4].Style.Font.Color.SetColor(Color.Green);
                    summarySheet.Cells[summaryRow, 5].Value = failedInCol;
                    if (failedInCol > 0)
                        summarySheet.Cells[summaryRow, 5].Style.Font.Color.SetColor(Color.Red);
                    
                    summaryRow++;
                }
            }

            // Auto-fit columns in summary
            summarySheet.Cells.AutoFitColumns();
            summarySheet.Column(2).Width = Math.Min(60, Math.Max(30, summarySheet.Column(2).Width));

            // Auto-fit columns in main sheet (with max width)
            worksheet.Cells.AutoFitColumns();
            for (int i = 1; i <= excelData.TotalColumns; i++)
            {
                // Image columns get more width for URLs
                if (excelData.ImageColumns.Contains(i))
                {
                    worksheet.Column(i).Width = Math.Min(70, Math.Max(50, worksheet.Column(i).Width));
                }
                else
                {
                    // Data columns use reasonable width
                    worksheet.Column(i).Width = Math.Min(40, Math.Max(15, worksheet.Column(i).Width));
                }
            }

            // Freeze top row if there's a header
            if (excelData.HasHeader)
            {
                worksheet.View.FreezePanes(2, 1);
            }

            // Save
            var file = new FileInfo(filePath);
            package.SaveAs(file);

            Console.WriteLine($"[BulkImageExporter] ? Excel saved successfully");
            Console.WriteLine($"[BulkImageExporter] ?? Success: {successCount}, Failed: {failCount}");
            Console.WriteLine($"[BulkImageExporter] ?? File: {filePath}");
            Console.WriteLine($"[BulkImageExporter] ========================================");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BulkImageExporter] ? Error: {ex.Message}");
            Console.WriteLine($"[BulkImageExporter] Stack trace: {ex.StackTrace}");
            throw;
        }
    }
}
