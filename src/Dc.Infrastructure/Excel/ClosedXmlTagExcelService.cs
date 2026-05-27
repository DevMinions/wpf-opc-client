using ClosedXML.Excel;
using Dc.Domain.Entities;

namespace Dc.Infrastructure.Excel;

public sealed class ClosedXmlTagExcelService : ITagExcelService
{
    private const string SheetName = "Tags";

    public IReadOnlyList<TagImportRow> Read(Stream excelStream)
    {
        using var workbook = new XLWorkbook(excelStream);
        var sheet = workbook.Worksheets.FirstOrDefault(s => s.Name == SheetName)
                    ?? workbook.Worksheets.First();

        var headerRow = sheet.Row(1);
        var itemCol = FindColumn(headerRow, "Item") ?? 1;
        var typeCol = FindColumn(headerRow, "DataType") ?? 2;
        var groupCol = FindColumn(headerRow, "GroupName") ?? 3;

        var rows = new List<TagImportRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= lastRow; r++)
        {
            var item = sheet.Cell(r, itemCol).GetString().Trim();
            if (string.IsNullOrEmpty(item)) continue;

            // InvariantCulture：DataType 是数值代码，解析不应随机器区域设置变化。
            int.TryParse(
                sheet.Cell(r, typeCol).GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var dataType);
            var groupName = sheet.Cell(r, groupCol).GetString().Trim();

            rows.Add(new TagImportRow(item, dataType, groupName));
        }
        return rows;
    }

    public void Write(IEnumerable<Tag> tags, IReadOnlyDictionary<string, string> groupIdToName, Stream output)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(SheetName);

        sheet.Cell(1, 1).Value = "Item";
        sheet.Cell(1, 2).Value = "DataType";
        sheet.Cell(1, 3).Value = "GroupName";
        sheet.Cell(1, 4).Value = "TaskId";
        sheet.Row(1).Style.Font.Bold = true;

        int row = 2;
        foreach (var tag in tags)
        {
            sheet.Cell(row, 1).Value = tag.Item;
            sheet.Cell(row, 2).Value = tag.DataType;
            sheet.Cell(row, 3).Value = groupIdToName.GetValueOrDefault(tag.GroupId, string.Empty);
            sheet.Cell(row, 4).Value = tag.TaskId;
            row++;
        }
        sheet.Columns().AdjustToContents();
        workbook.SaveAs(output);
    }

    private static int? FindColumn(IXLRow headerRow, string name)
    {
        foreach (var cell in headerRow.CellsUsed())
        {
            if (string.Equals(cell.GetString().Trim(), name, StringComparison.OrdinalIgnoreCase))
                return cell.Address.ColumnNumber;
        }
        return null;
    }
}
