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

    private static TagValueTransform BuildWithFormula(
        Dictionary<string, ScaleConfig> scale,
        Dictionary<string, string> itemByTagId,
        params FormulaConfig[] formulas) =>
        new(new TransformConfig(scale, itemByTagId, formulas));

    private static FormulaConfig Formula(string id, string outItem, string expr, params (string alias, string tagId)[] inputs) =>
        new(id, outItem, expr, inputs.Select(i => new FormulaInputConfig(i.alias, i.tagId)).ToArray());

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

    [Fact]
    public void Formula_NotReady_WhenInputOnlyBad_NoOutput()
    {
        var t = BuildWithFormula(new() { ["t1"] = new(null, null) }, new() { ["t1"] = "A" },
            Formula("f1", "OUT", "A * 2", ("A", "t1")));
        var outp = t.Apply(V("A", 10.0, 0x00)); // Bad
        Assert.Single(outp); // 仅真值，无虚拟
        Assert.Equal("A", outp[0].Item);
    }

    [Fact]
    public void Formula_Ready_AfterGoodInput_ProducesVirtual()
    {
        var t = BuildWithFormula(new() { ["t1"] = new(null, null) }, new() { ["t1"] = "A" },
            Formula("f1", "OUT", "A * 2", ("A", "t1")));
        var outp = t.Apply(V("A", 10.0, 0xC0)); // Good → 就绪 + 立即算
        Assert.Equal(2, outp.Count);
        Assert.Equal("A", outp[0].Item);
        Assert.Equal("OUT", outp[1].Item);
        Assert.Equal(20.0, outp[1].Value);
        Assert.Equal(0xC0, outp[1].Quality);
    }

    [Fact]
    public void Formula_MultiInput_NotReadyUntilAllGood()
    {
        var t = BuildWithFormula(
            new() { ["t1"] = new(null, null), ["t2"] = new(null, null) },
            new() { ["t1"] = "A", ["t2"] = "B" },
            Formula("f1", "OUT", "A + B", ("A", "t1"), ("B", "t2")));

        var o1 = t.Apply(V("A", 1.0, 0xC0));
        Assert.Single(o1); // 仅 A，未就绪

        var o2 = t.Apply(V("B", 2.0, 0xC0));
        Assert.Equal(2, o2.Count); // B 真值 + 虚拟
        Assert.Equal("OUT", o2[1].Item);
        Assert.Equal(3.0, o2[1].Value);
    }

    [Fact]
    public void Formula_QualityPropagation_BadInputMakesVirtualBad()
    {
        var t = BuildWithFormula(
            new() { ["t1"] = new(null, null), ["t2"] = new(null, null) },
            new() { ["t1"] = "A", ["t2"] = "B" },
            Formula("f1", "OUT", "A + B", ("A", "t1"), ("B", "t2")));

        t.Apply(V("A", 1.0, 0xC0));
        t.Apply(V("B", 2.0, 0xC0)); // 所有输入已见 Good → 就绪

        var o = t.Apply(V("B", 2.0, 0x00)); // Ready 后 B=Bad
        Assert.Equal(2, o.Count);
        Assert.Equal(0x00, o[1].Quality); // 虚拟值 Bad
    }

    [Fact]
    public void Formula_QualityPropagation_UncertainInputMakesVirtualUncertain()
    {
        var t = BuildWithFormula(
            new() { ["t1"] = new(null, null), ["t2"] = new(null, null) },
            new() { ["t1"] = "A", ["t2"] = "B" },
            Formula("f1", "OUT", "A + B", ("A", "t1"), ("B", "t2")));

        t.Apply(V("A", 1.0, 0xC0));
        t.Apply(V("B", 2.0, 0xC0)); // 所有输入已见 Good → 就绪

        var o = t.Apply(V("B", 2.0, 0x40)); // Ready 后 Uncertain
        Assert.Equal(0x40, o[1].Quality);
    }

    [Fact]
    public void Formula_EvalException_NoOutput_StaysReady()
    {
        var t = BuildWithFormula(
            new() { ["t1"] = new(null, null), ["t2"] = new(null, null) },
            new() { ["t1"] = "A", ["t2"] = "B" },
            Formula("f1", "OUT", "A / B", ("A", "t1"), ("B", "t2")));

        t.Apply(V("A", 1.0, 0xC0));
        var o1 = t.Apply(V("B", 1.0, 0xC0)); // 1/1=1
        Assert.Equal(1.0, o1[1].Value);

        // B=0 → 除零。DynamicExpresso 抛或返 Inf；我们捕获异常/Inf 均不产出虚拟值。
        var o2 = t.Apply(V("B", 0.0, 0xC0));
        Assert.Single(o2); // 仅真值 B，无虚拟（异常路径不产出）

        // 恢复后仍可算 → 状态保持 Ready
        var o3 = t.Apply(V("B", 2.0, 0xC0));
        Assert.Equal(2, o3.Count);
        Assert.Equal(0.5, o3[1].Value);
    }
}
