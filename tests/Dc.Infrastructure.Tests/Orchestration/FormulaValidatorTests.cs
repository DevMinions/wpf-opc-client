using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class FormulaValidatorTests
{
    private readonly FormulaValidator _v = new();

    // 数据类型码沿用 OpcDataTypeOption：6=Double 等。校验器只关心"是否可数值化"。
    private static readonly int Numeric = 6;
    private static readonly int StringType = 8; // 假定 8=String；校验器按"非数值型即拒绝"判

    [Fact]
    public void Valid_Expression_Passes()
    {
        var ok = _v.Validate("T * 1.8 + 32", new Dictionary<string, int> { ["T"] = Numeric }, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void Undefined_Variable_Fails()
    {
        var ok = _v.Validate("T + P", new Dictionary<string, int> { ["T"] = Numeric }, out var err); // P 未声明
        Assert.False(ok);
        Assert.Contains("P", err!);
    }

    [Fact]
    public void String_Input_Rejected()
    {
        var ok = _v.Validate("T + 1", new Dictionary<string, int> { ["T"] = StringType }, out var err);
        Assert.False(ok);
        Assert.Contains("数值", err!);
    }

    [Fact]
    public void Syntax_Error_Fails()
    {
        var ok = _v.Validate("T +", new Dictionary<string, int> { ["T"] = Numeric }, out var err);
        Assert.False(ok);
        Assert.NotNull(err);
    }
}
