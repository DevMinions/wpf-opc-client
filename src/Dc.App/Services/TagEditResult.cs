using Dc.Domain.Entities;

namespace Dc.App.Services;

/// <summary>
/// Tag 编辑器返回结果:真实 Tag 只带 Tag(Formula=null);虚拟 Tag 带 Tag+Formula+Inputs。
/// 持久化由调用方(TagsViewModel)负责,编辑器只出数据。
/// </summary>
public sealed record TagEditResult(
    Tag Tag,
    Formula? Formula,
    IReadOnlyList<FormulaInput> Inputs);
