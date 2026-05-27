using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests;

public class TagValueTests
{
    [Theory]
    [InlineData(0xC0, true, false, false)]   // Good
    [InlineData(0xC1, true, false, false)]   // Good + sub-status
    [InlineData(0x40, false, true, false)]   // Uncertain
    [InlineData(0x00, false, false, true)]   // Bad
    [InlineData(0x18, false, false, true)]   // Bad + sub-status
    [InlineData(0xFF, true, false, false)]   // 全 1 — 顶 2 位是 11 即 Good
    [InlineData(0x80, false, false, false)]  // 10 — 未来保留（OPC DA 规范），既不是 Good/Uncertain/Bad
    public void Quality_Bitmask_ParsedCorrectly(int quality, bool good, bool uncertain, bool bad)
    {
        var v = new TagValue("x", null, (ushort)quality, DateTimeOffset.UtcNow);
        Assert.Equal(good, v.IsGood);
        Assert.Equal(uncertain, v.IsUncertain);
        Assert.Equal(bad, v.IsBad);
    }
}
