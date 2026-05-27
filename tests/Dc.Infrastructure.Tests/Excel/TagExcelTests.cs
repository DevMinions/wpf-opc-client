using Dc.Domain.Entities;
using Dc.Infrastructure.Excel;
using Xunit;

namespace Dc.Infrastructure.Tests.Excel;

public class TagExcelTests
{
    [Fact]
    public void WriteAndRead_Roundtrip()
    {
        var service = new ClosedXmlTagExcelService();
        var tags = new[]
        {
            new Tag { Id = "id1", Item = "Random.Int1", DataType = 2, GroupId = "g1", TaskId = "task-1" },
            new Tag { Id = "id2", Item = "Random.Real8", DataType = 5, GroupId = "g1", TaskId = "task-1" },
            new Tag { Id = "id3", Item = "中文.Item", DataType = 8, GroupId = "g2", TaskId = "task-2" }
        };
        var groupMap = new Dictionary<string, string>
        {
            ["g1"] = "DemoGroup",
            ["g2"] = "中文分组"
        };

        using var ms = new MemoryStream();
        service.Write(tags, groupMap, ms);
        ms.Position = 0;

        var rows = service.Read(ms);

        Assert.Equal(3, rows.Count);
        Assert.Equal("Random.Int1", rows[0].Item);
        Assert.Equal(2, rows[0].DataType);
        Assert.Equal("DemoGroup", rows[0].GroupName);
        Assert.Equal("中文.Item", rows[2].Item);
        Assert.Equal("中文分组", rows[2].GroupName);
    }

    [Fact]
    public void Read_SkipsEmptyItemRows()
    {
        var service = new ClosedXmlTagExcelService();
        var tags = new[]
        {
            new Tag { Id = "id1", Item = "A", DataType = 1, GroupId = "g1", TaskId = "t" }
        };
        using var ms = new MemoryStream();
        service.Write(tags, new Dictionary<string, string> { ["g1"] = "G" }, ms);
        ms.Position = 0;

        // 二次写入数据：插入一个空 item 行
        using var workbook = new ClosedXML.Excel.XLWorkbook(ms);
        var sheet = workbook.Worksheet("Tags");
        sheet.Cell(3, 1).Value = "";
        sheet.Cell(3, 2).Value = 9;
        sheet.Cell(3, 3).Value = "Whatever";
        sheet.Cell(4, 1).Value = "B";
        sheet.Cell(4, 2).Value = 2;
        sheet.Cell(4, 3).Value = "G";
        using var ms2 = new MemoryStream();
        workbook.SaveAs(ms2);
        ms2.Position = 0;

        var rows = service.Read(ms2);
        Assert.Equal(2, rows.Count);
        Assert.Equal("A", rows[0].Item);
        Assert.Equal("B", rows[1].Item);
    }

    [Fact]
    public void Read_HandlesHeaderOrderInvariance()
    {
        using var ms = new MemoryStream();
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var s = wb.AddWorksheet("Tags");
            // 故意打乱列顺序
            s.Cell(1, 1).Value = "GroupName";
            s.Cell(1, 2).Value = "Item";
            s.Cell(1, 3).Value = "DataType";
            s.Cell(2, 1).Value = "GroupA";
            s.Cell(2, 2).Value = "MyItem";
            s.Cell(2, 3).Value = 4;
            wb.SaveAs(ms);
        }
        ms.Position = 0;

        var service = new ClosedXmlTagExcelService();
        var rows = service.Read(ms);

        var row = Assert.Single(rows);
        Assert.Equal("MyItem", row.Item);
        Assert.Equal(4, row.DataType);
        Assert.Equal("GroupA", row.GroupName);
    }
}
