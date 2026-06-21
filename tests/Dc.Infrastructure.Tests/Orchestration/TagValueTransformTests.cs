using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class TagValueTransformTests
{
    private static TagValue V(string item, object? val, ushort q = 0xC0) =>
        new(item, val, q, DateTimeOffset.UtcNow);

    private static TagValueTransform BuildScaleOnly(
        Dictionary<string, ScaleConfig> scale,
        Dictionary<string, string> itemByTagId) =>
        new(new TransformConfig(scale, itemByTagId, Array.Empty<FormulaConfig>()));

    [Fact]
    public void Apply_Scales_RealTag_EngineeringValue()
    {
        var cfg = BuildScaleOnly(
            new() { ["t1"] = new(0.1, 0) },
            new() { ["t1"] = "A" });
        var outp = cfg.Apply(V("A", 255.0));
        Assert.Single(outp);
        Assert.Equal(25.5, outp[0].Value);
        Assert.Equal("A", outp[0].Item);
        Assert.Equal(0xC0, outp[0].Quality);
    }

    [Fact]
    public void Apply_NoScale_PassesThrough()
    {
        var cfg = BuildScaleOnly(new() { ["t1"] = new(null, null) }, new() { ["t1"] = "A" });
        var outp = cfg.Apply(V("A", 42.0));
        Assert.Equal(42.0, outp[0].Value);
    }

    [Fact]
    public void Apply_NonNumeric_Passthrough_NoScale()
    {
        var cfg = BuildScaleOnly(new() { ["t1"] = new(2.0, 0) }, new() { ["t1"] = "A" });
        var outp = cfg.Apply(V("A", "hello")); // String 不可缩放
        Assert.Equal("hello", outp[0].Value);
    }

    [Fact]
    public void Apply_NaN_Result_MarkedUncertain()
    {
        var cfg = BuildScaleOnly(new() { ["t1"] = new(0.0, 0) }, new() { ["t1"] = "A" }); // 0 * x = 0 不 NaN
        // 构造 NaN：用 double.NaN 原值
        var outp = cfg.Apply(V("A", double.NaN));
        // NaN 透传为值，质量降为 Uncertain
        Assert.Equal(0x40, outp[0].Quality);
    }

    [Fact]
    public void Apply_UnknownItem_PassesThrough()
    {
        var cfg = BuildScaleOnly(new() { ["t1"] = new(2.0, 0) }, new() { ["t1"] = "A" });
        var outp = cfg.Apply(V("UNKNOWN", 1.0));
        Assert.Single(outp);
        Assert.Equal(1.0, outp[0].Value);
    }
}
