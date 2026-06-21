using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class FormulaValidatorTests
{
    private readonly FormulaValidator _v = new();

    // 数据类型码沿用 OpcDataTypeOption：5=Float64, 3=Int32, 11=Boolean, 16=Int8；8=String, 7=DateTime。
    private static readonly int Numeric = 5;
    private static readonly int StringType = 8;
    private static readonly int DateTimeType = 7;
    private static readonly int BooleanType = 11;
    private static readonly int Int8Type = 16;

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
    public void DateTime_Input_Rejected()
    {
        var ok = _v.Validate("T + 1", new Dictionary<string, int> { ["T"] = DateTimeType }, out var err);
        Assert.False(ok);
        Assert.Contains("数值", err!);
    }

    [Fact]
    public void Boolean_Input_Accepted()
    {
        var ok = _v.Validate("T + 1", new Dictionary<string, int> { ["T"] = BooleanType }, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void Integer_Input_Accepted()
    {
        var ok = _v.Validate("T + 1", new Dictionary<string, int> { ["T"] = Int8Type }, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void Builtin_Formula_Validates()
    {
        var ok = _v.Validate("SQRT(A) + 1", new Dictionary<string, int> { ["A"] = Numeric }, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void Spec_Builtins_WithConditionalAndAggregates_Validate()
    {
        var ok = _v.Validate("IF(A, AVG(A, B), SUM(A, B)) + ASIN(0) + ACOS(1) + ATAN(0)",
            new Dictionary<string, int> { ["A"] = Numeric, ["B"] = 3 }, out var err);
        Assert.True(ok, err);
        Assert.Null(err);
    }

    [Fact]
    public void Syntax_Error_Fails()
    {
        var ok = _v.Validate("T +", new Dictionary<string, int> { ["T"] = Numeric }, out var err);
        Assert.False(ok);
        Assert.NotNull(err);
    }
}
