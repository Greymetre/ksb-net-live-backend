using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Api.Filters;

/// <summary>
/// One consistent grid look for every XLSX the CRM exports, applied centrally so no
/// individual export has to repeat it: a thin border round every used cell, every cell
/// left-aligned, and a bold white header on a blue fill two points larger than the body.
/// Styling failures never fail the export - the original file is returned untouched.
/// </summary>
public sealed class ExcelGridStyleFilter : IAsyncResultFilter
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is FileContentResult file
            && string.Equals(file.ContentType, Xlsx, StringComparison.OrdinalIgnoreCase)
            && file.FileContents is { Length: > 0 })
        {
            try
            {
                context.Result = new FileContentResult(Restyle(file.FileContents), file.ContentType)
                {
                    FileDownloadName = file.FileDownloadName,
                    LastModified = file.LastModified,
                    EntityTag = file.EntityTag,
                    EnableRangeProcessing = file.EnableRangeProcessing,
                };
            }
            catch
            {
                // leave the original file as-is on any styling error
            }
        }

        await next();
    }

    private static byte[] Restyle(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(input);
        foreach (var sheet in workbook.Worksheets)
        {
            var used = sheet.RangeUsed();
            if (used is null) continue;

            var lastRow = used.LastRow().RowNumber();
            var lastCol = used.LastColumn().ColumnNumber();

            var body = sheet.Range(1, 1, lastRow, lastCol);
            body.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            body.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            body.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            var header = sheet.Range(1, 1, 1, lastCol);
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("1E88E5");
            header.Style.Font.Bold = true;
            header.Style.Font.FontColor = XLColor.White;
            header.Style.Font.FontSize = 12;
            header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }
}
