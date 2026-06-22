using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class TagValueTransformFactoryTests
{
    private readonly TagValueTransformFactory _f = new();

    [Fact]
    public void Create_NoFormulaNoScale_ReturnsNoOp()
    {
        var cfg = new TransformConfig(
            new Dictionary<string, ScaleConfig> { ["t1"] = new ScaleConfig(null, null) },
            new Dictionary<string, string> { ["t1"] = "A" },
            Array.Empty<FormulaConfig>());
        var t = _f.Create("t1", cfg);
        Assert.Same(NoOpTransform.Instance, t);
    }

    [Fact]
    public void Create_WithScale_ReturnsRealTransform()
    {
        var cfg = new TransformConfig(
            new Dictionary<string, ScaleConfig> { ["t1"] = new ScaleConfig(2.0, 0) },
            new Dictionary<string, string> { ["t1"] = "A" },
            Array.Empty<FormulaConfig>());
        var t = _f.Create("t1", cfg);
        Assert.IsType<TagValueTransform>(t);
    }

    [Fact]
    public void Create_WithFormula_ReturnsRealTransform()
    {
        var cfg = new TransformConfig(
            new Dictionary<string, ScaleConfig> { ["t1"] = new ScaleConfig(null, null) },
            new Dictionary<string, string> { ["t1"] = "A" },
            new[] { new FormulaConfig("f1", "OUT", "A*2", new[] { new FormulaInputConfig("A", "t1") }) });
        var t = _f.Create("t1", cfg);
        Assert.IsType<TagValueTransform>(t);
    }
}
