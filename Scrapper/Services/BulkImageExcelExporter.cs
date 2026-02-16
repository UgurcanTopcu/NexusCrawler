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




            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("Processed Images");

            // Write ALL original cells preserving structure
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

            // Add new columns for resized images AFTER the original columns
            // Create header for new columns if original has header
            int newColumnsStartIndex = excelData.TotalColumns + 1;
            
            if (excelData.HasHeader)
            {
                foreach (var imageCol in excelData.ImageColumns.OrderBy(x => x))
                {
                    var originalHeader = excelData.Headers.Count >= imageCol 
                        ? excelData.Headers[imageCol - 1] 
                        : $"Column {imageCol}";
                    
                    worksheet.Cells[1, newColumnsStartIndex].Value = $"{originalHeader} (Resized)";
                    worksheet.Cells[1, newColumnsStartIndex].Style.Font.Bold = true;
                    worksheet.Cells[1, newColumnsStartIndex].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    worksheet.Cells[1, newColumnsStartIndex].Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
                    newColumnsStartIndex++;
                }
            }

            // Map each image column to its new resized column
            var imageColumnMapping = new Dictionary<int, int>();
            int mappingIndex = excelData.TotalColumns + 1;
            foreach (var imageCol in excelData.ImageColumns.OrderBy(x => x))
            {
                imageColumnMapping[imageCol] = mappingIndex++;
            }

            // Fill in the resized URLs in the new columns
            int successCount = 0;
            int failCount = 0;

            foreach (var imageCell in excelData.ImageCells)
            {
                // Original column keeps original URL (already written above)
                var originalCell = worksheet.Cells[imageCell.Row, imageCell.Column];
                
                // New column gets the resized URL
                if (imageColumnMapping.TryGetValue(imageCell.Column, out var newColIndex))
                {
                    var resizedCell = worksheet.Cells[imageCell.Row, newColIndex];
                    
                    if (imageCell.IsProcessed && !string.IsNullOrEmpty(imageCell.CdnUrl))
                    {
                        // Successfully processed - put CDN URL in new column
                        resizedCell.Value = imageCell.CdnUrl;
                        resizedCell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                        resizedCell.Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
                        resizedCell.Style.Font.Color.SetColor(Color.DarkGreen);
                        
                        // Mark original cell with light green border to show it was processed
                        originalCell.Style.Border.BorderAround(ExcelBorderStyle.Thin, Color.Green);
                        successCount++;
                    }
                    else if (imageCell.IsProcessed && !string.IsNullOrEmpty(imageCell.Error))
                    {
                        // Failed - put error in new column
                        resizedCell.Value = $"ERROR: {imageCell.Error}";
                        resizedCell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                        resizedCell.Style.Fill.BackgroundColor.SetColor(Color.LightCoral);
                        resizedCell.Style.Font.Color.SetColor(Color.DarkRed);
                        
                        // Mark original cell with red border to show it failed
                        originalCell.Style.Border.BorderAround(ExcelBorderStyle.Thin, Color.Red);
                        failCount++;
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
            
            // Original columns
            for (int i = 1; i <= excelData.TotalColumns; i++)
            {
                if (excelData.ImageColumns.Contains(i))
                {
                    // Original image columns
                    worksheet.Column(i).Width = Math.Min(70, Math.Max(50, worksheet.Column(i).Width));
                }
                else
                {
                    // Data columns
                    worksheet.Column(i).Width = Math.Min(40, Math.Max(15, worksheet.Column(i).Width));
                }
            }
            
            // New resized image columns
            for (int i = excelData.TotalColumns + 1; i < excelData.TotalColumns + excelData.ImageColumns.Count + 1; i++)
            {
                worksheet.Column(i).Width = Math.Min(80, Math.Max(60, worksheet.Column(i).Width));
            }

            // Freeze top row if there's a header
            if (excelData.HasHeader)
            {
                worksheet.View.FreezePanes(2, 1);
            }

            // Save
            var file = new FileInfo(filePath);
            package.SaveAs(file);



        }
        catch (Exception ex)
        {

            throw;
        }
    }

    /// <summary>
    /// Export URL list processing results
    /// </summary>
    public void ExportUrlResults(List<(string originalUrl, string? convertedUrl, bool success, string? error)> results, string filePath)
    {
        try
        {
            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("Converted URLs");

            // Headers
            worksheet.Cells[1, 1].Value = "Original URL (Before Resizing)";
            worksheet.Cells[1, 2].Value = "Status";
            worksheet.Cells[1, 3].Value = "Resized URL (After - wsrv.nl 1000x1000)";
            worksheet.Cells[1, 4].Value = "Error Message";

            // Style headers
            using (var headerRange = worksheet.Cells[1, 1, 1, 4])
            {
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
                headerRange.Style.Fill.BackgroundColor.SetColor(Color.LightBlue);
                headerRange.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            }

            // Data rows
            int row = 2;
            foreach (var result in results)
            {
                // Original URL
                worksheet.Cells[row, 1].Value = result.originalUrl;
                
                // Status
                worksheet.Cells[row, 2].Value = result.success ? "? Success" : "? Failed";
                
                // Converted URL
                worksheet.Cells[row, 3].Value = result.convertedUrl ?? "";
                
                // Error
                worksheet.Cells[row, 4].Value = result.error ?? "";

                // Color code the status
                if (result.success)
                {
                    worksheet.Cells[row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    worksheet.Cells[row, 2].Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
                    worksheet.Cells[row, 2].Style.Font.Color.SetColor(Color.DarkGreen);
                    
                    worksheet.Cells[row, 3].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    worksheet.Cells[row, 3].Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
                    worksheet.Cells[row, 3].Style.Font.Color.SetColor(Color.DarkGreen);
                    
                    // Add green border to original URL
                    worksheet.Cells[row, 1].Style.Border.BorderAround(ExcelBorderStyle.Thin, Color.Green);
                }
                else
                {
                    worksheet.Cells[row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    worksheet.Cells[row, 2].Style.Fill.BackgroundColor.SetColor(Color.LightCoral);
                    worksheet.Cells[row, 2].Style.Font.Color.SetColor(Color.DarkRed);
                    
                    worksheet.Cells[row, 4].Style.Font.Color.SetColor(Color.DarkRed);
                    
                    // Add red border to original URL
                    worksheet.Cells[row, 1].Style.Border.BorderAround(ExcelBorderStyle.Thin, Color.Red);
                }

                row++;
            }

            // Auto-fit columns
            worksheet.Cells.AutoFitColumns();
            worksheet.Column(1).Width = Math.Min(70, Math.Max(40, worksheet.Column(1).Width)); // Original URL
            worksheet.Column(3).Width = Math.Min(80, Math.Max(50, worksheet.Column(3).Width)); // Resized URL

            // Freeze top row
            worksheet.View.FreezePanes(2, 1);

            // Save
            var file = new FileInfo(filePath);
            package.SaveAs(file);

            Console.WriteLine($"[BulkImageExporter] Exported {results.Count} URL results to {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BulkImageExporter] Error exporting URL results: {ex.Message}");
            throw;
        }
    }
}
