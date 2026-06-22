namespace Dc.Infrastructure.Orchestration;

public interface IFormulaValidator
{
    // aliasToDataType: 表达式里每个变量名 → 该输入 Tag 的数据类型码。
    // 非数值类型码 → 拒绝。返回是否合法 + 错误信息。
    bool Validate(string expression, IReadOnlyDictionary<string, int> aliasToDataType, out string? error);
}
