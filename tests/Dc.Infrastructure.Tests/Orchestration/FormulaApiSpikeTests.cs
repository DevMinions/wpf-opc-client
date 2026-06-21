using DynamicExpresso;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

// 确认 DynamicExpresso API 形态（Parse/Lambda.Invoke + SetFunction + 三元），
// 供后续 FormulaValidator/TagValueTransform 依赖。探针用例，验证后保留。
public class FormulaApiSpikeTests
{
    [Fact]
    public void Parse_And_Invoke_Caches_Lambda()
    {
        var interp = new Interpreter();
        var lambda = interp.Parse("T * 1.8 + 32", new Parameter("T", typeof(double)));
        Assert.Equal(212.0, (double)lambda.Invoke(100.0));
        Assert.Equal(32.0, (double)lambda.Invoke(0.0));
    }

    [Fact]
    public void SetFunction_Registers_Custom_Function()
    {
        var interp = new Interpreter();
        interp.SetFunction("SQRT", new Func<double, double>(Math.Sqrt));
        Assert.Equal(3.0, interp.Eval<double>("SQRT(9)"));
    }

    [Fact]
    public void Ternary_Is_Supported()
    {
        var interp = new Interpreter();
        Assert.Equal(1.0, interp.Eval<double>("T > 0 ? 1 : 0", new Parameter("T", 5.0)));
    }
}
