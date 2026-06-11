using Dc.App.ViewModels;
using Dc.Opc.Abstractions;

namespace Dc.App.Tests.ViewModels;

public class BrowseNodeRowViewModelTests
{
    private static BrowseNodeRowViewModel Row()
        => new(new OpcNode("ns=2;s=A", "A", OpcNodeKind.Item, false));

    [Fact]
    public void SetValue_Good_FormatsText_SetsQuality_IsGood()
    {
        var row = Row();
        row.SetValue(new OpcNodeValue("Int32", 42, 0xC0, DateTimeOffset.UtcNow));
        Assert.Equal("42", row.ValueText);
        Assert.Equal((ushort)0xC0, row.Quality);
        Assert.True(row.HasValue);
        Assert.True(row.IsGood);
    }

    [Fact]
    public void SetValue_Null_ShowsDash_NotHasValue_NotGood()
    {
        var row = Row();
        row.SetValue(null);
        Assert.Equal("—", row.ValueText);
        Assert.False(row.HasValue);
        Assert.False(row.IsGood);
    }

    [Fact]
    public void SetValue_BadQualityNullValue_HasValueButDash_NotGood()
    {
        var row = Row();
        row.SetValue(new OpcNodeValue("Int32", null, 0x00, null));
        Assert.Equal("—", row.ValueText);
        Assert.True(row.HasValue);
        Assert.False(row.IsGood);
    }
}
