namespace Dc.Infrastructure.Orchestration;

// 从 DB 加载的静态快照，task 启动时一次性构建，运行期不变（热加 Tag 不带缩放/公式信息）。
public sealed record ScaleConfig(double? ScaleFactor, double? Offset);

public sealed record FormulaInputConfig(string Alias, string SourceTagId);

public sealed record FormulaConfig(
    string FormulaId,
    string OutputItem,                 // 虚拟 Tag 的 Item（= Formula.Name），产出 TagValue 用它
    string Expression,
    IReadOnlyList<FormulaInputConfig> Inputs);

public sealed record TransformConfig(
    IReadOnlyDictionary<string, ScaleConfig> ScaleByTagId,  // 真实 TagId → 缩放
    IReadOnlyDictionary<string, string> ItemByTagId,        // 真实 TagId → Item（含热加前的真实 Tag）
    IReadOnlyList<FormulaConfig> Formulas);
