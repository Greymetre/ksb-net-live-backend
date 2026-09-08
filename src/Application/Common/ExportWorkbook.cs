using Application.DTOs.MasterData;
using ClosedXML.Excel;

namespace Application.Common;

/// <summary>
/// Builds the .xlsx a download hands back: a bold heading row taken from the column
/// names, then the rows, with the columns widened to fit.
///
/// NewInvoiceService, RedemptionService and MasterDataService each carry their own copy
/// of this. They are left alone here - moving three working exports is a change of its
/// own - but a new export should use this rather than make a fourth copy.
/// </summary>
public static class ExportWorkbook
{
    public static MasterDataFileDto Create(string fileName, string[] headings, IEnumerable<object?[]> rows)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Sheet1");
        worksheet.Style.Font.FontName = "Calibri";
        worksheet.Style.Font.FontSize = 9;

        for (var column = 0; column < headings.Length; column++)
        {
            worksheet.Cell(1, column + 1).Value = TitleCase(headings[column].Replace("_", " "));
            worksheet.Cell(1, column + 1).Style.Font.Bold = true;
        }

        var rowNumber = 2;
        foreach (var row in rows)
        {
            for (var column = 0; column < row.Length; column++)
            {
                SetCellValue(worksheet.Cell(rowNumber, column + 1), row[column]);
            }

            rowNumber++;
        }

        worksheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new MasterDataFileDto { FileName = fileName, Content = stream.ToArray() };
    }

    private static string TitleCase(string value) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Trim().ToLowerInvariant());

    private static void SetCellValue(IXLCell cell, object? value)
    {
        if (value is ExportHyperlink link)
        {
            cell.Value = link.Text;
            cell.SetHyperlink(new XLHyperlink(new Uri(link.Url)));
            return;
        }

        cell.Value = XLCellValue.FromObject(value);
    }
}
