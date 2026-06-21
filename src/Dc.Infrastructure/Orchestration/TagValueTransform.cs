using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

public sealed class TagValueTransform : ITagValueTransform
{
    private readonly IReadOnlyDictionary<string, ScaleConfig> _scaleByTagId;
    private readonly Dictionary<string, string> _tagIdByItem;        // 反查：Item → TagId
    private readonly Dictionary<string, string> _itemByTagId;
    private readonly IReadOnlyList<FormulaConfig> _formulas;
    private readonly Dictionary<string, FormulaRuntime> _formulaById;
    private readonly Dictionary<string, List<(string formulaId, string alias)>> _inputsByTagId; // 真实 TagId → 引用它的公式

    public TagValueTransform(TransformConfig config)
    {
        _scaleByTagId = config.ScaleByTagId;
        _itemByTagId = new Dictionary<string, string>(config.ItemByTagId);
        _tagIdByItem = config.ItemByTagId.ToDictionary(kv => kv.Value, kv => kv.Key);
        _formulas = config.Formulas;
        _inputsByTagId = new Dictionary<string, List<(string, string)>>();

        _formulaById = new Dictionary<string, FormulaRuntime>();
        foreach (var f in _formulas)
        {
            var rt = new FormulaRuntime(f);
            _formulaById[f.FormulaId] = rt;
            foreach (var inp in f.Inputs)
            {
                if (!_inputsByTagId.TryGetValue(inp.SourceTagId, out var list))
                {
                    list = new List<(string, string)>();
                    _inputsByTagId[inp.SourceTagId] = list;
                }
                list.Add((f.FormulaId, inp.Alias));
            }
        }
        // 公式求值器在 Task 6 接续构建。
    }

    public IReadOnlyList<TagValue> Apply(TagValue raw)
    {
        // 解析真实 TagId
        if (!_tagIdByItem.TryGetValue(raw.Item, out var tagId))
        {
            return new[] { raw }; // 未知 Item（热加未登记）→ 透传
        }

        // 1) 缩放产出工程量
        var engineering = ApplyScale(raw, tagId);

        var outputs = new List<TagValue> { engineering };

        // 2) 公式求值（Task 6 接续）
        EvaluateFormulas(engineering, tagId, outputs);

        return outputs;
    }

    private TagValue ApplyScale(TagValue raw, string tagId)
    {
        if (!_scaleByTagId.TryGetValue(tagId, out var sc)
            || (sc.ScaleFactor is null && sc.Offset is null))
        {
            return raw; // 无缩放配置
        }

        if (!TryToDouble(raw.Value, out var num))
        {
            return raw; // 非数值型，原值透传
        }

        var scaled = num * (sc.ScaleFactor ?? 1.0) + (sc.Offset ?? 0.0);
        var q = raw.Quality;
        if (double.IsNaN(scaled) || double.IsInfinity(scaled))
        {
            q = 0x40; // Uncertain
        }
        return raw with { Value = scaled, Quality = q };
    }

    private static bool TryToDouble(object? v, out double d)
    {
        d = 0;
        if (v is null) return false;
        try
        {
            d = v switch
            {
                double dd => dd,
                float f => f,
                int i => i,
                long l => l,
                short s => s,
                ushort us => us,
                uint ui => ui,
                ulong ul => ul,
                bool b => b ? 1.0 : 0.0,
                _ => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EvaluateFormulas(TagValue engineering, string sourceTagId, List<TagValue> outputs)
    {
        // Task 6 实现。
    }

    public void OnTagsAdded(IEnumerable<TagDescriptor> tags)
    {
        foreach (var t in tags)
        {
            _tagIdByItem[t.Item] = t.Id;
            _itemByTagId[t.Id] = t.Item;
            // 热加 Tag 不带缩放/公式信息：不登记 ScaleByTagId，默认不缩放。
        }
    }

    public void OnTagsRemoved(IEnumerable<TagDescriptor> tags)
    {
        // Task 7 实现（标记依赖公式 Failed）。
        foreach (var t in tags)
        {
            _tagIdByItem.Remove(t.Item);
            _itemByTagId.Remove(t.Id);
        }
    }

    // Task 6 引入的公式运行时状态。
    private sealed class FormulaRuntime
    {
        public FormulaConfig Config { get; }
        public bool IsReady { get; set; }
        public bool IsFailed { get; set; }
        public Dictionary<string, (double value, ushort quality, bool seenGood)> Inputs { get; } = new();
        public FormulaRuntime(FormulaConfig c) { Config = c; }
    }
}
