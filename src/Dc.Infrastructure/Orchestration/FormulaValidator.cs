using DynamicExpresso;

namespace Dc.Infrastructure.Orchestration;

public sealed class FormulaValidator : IFormulaValidator
{
    // 可数值化的数据类型码集合。调用方约定：Double=6, Float=5, Int32=3/4, Int16=1/2, Bool=0。
    // 不在此集合（如 String）→ 拒绝作为公式输入。
    private static readonly HashSet<int> NumericTypeCodes = new() { 0, 1, 2, 3, 4, 5, 6 };

    public bool Validate(string expression, IReadOnlyDictionary<string, int> aliasToDataType, out string? error)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "表达式不能为空";
            return false;
        }

        foreach (var (alias, code) in aliasToDataType)
        {
            if (!NumericTypeCodes.Contains(code))
            {
                error = $"输入 '{alias}' 的数据类型不可数值化，不能用于公式";
                return false;
            }
        }

        try
        {
            var interp = new Interpreter();
            var parameters = aliasToDataType
                .Select(kv => new Parameter(kv.Key, typeof(double)))
                .ToArray();
            interp.Parse(expression, parameters); // 语法/未定义变量在此抛
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"表达式无效：{ex.Message}";
            return false;
        }
    }
}
