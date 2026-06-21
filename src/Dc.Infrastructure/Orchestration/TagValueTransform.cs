using Dc.Opc.Abstractions;
using DynamicExpresso;
using System.Globalization;

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
                _ => Convert.ToDouble(v, CultureInfo.InvariantCulture)
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
        if (!_inputsByTagId.TryGetValue(sourceTagId, out var refs)) return;

        foreach (var (formulaId, alias) in refs)
        {
            var rt = _formulaById[formulaId];
            if (rt.IsFailed) continue;

            // 更新输入槽（用工程量值 + 质量）
            if (TryToDouble(engineering.Value, out var num))
            {
                var prevSeenGood = rt.Inputs.GetValueOrDefault(alias).seenGood;
                var seenGood = engineering.Quality == 0xC0 || prevSeenGood;
                rt.Inputs[alias] = (num, engineering.Quality, seenGood);
            }
            else
            {
                var prevSeenGood = rt.Inputs.GetValueOrDefault(alias).seenGood;
                rt.Inputs[alias] = (0, 0x00, prevSeenGood);
            }

            // 就绪门控：所有输入 seenGood
            if (!rt.IsReady)
            {
                if (rt.Config.Inputs.All(i => rt.Inputs.TryGetValue(i.Alias, out var s) && s.seenGood))
                    rt.IsReady = true;
                else
                    continue;
            }

            // 求值
            var virtualValue = TryEvaluate(rt);
            if (virtualValue is null) continue; // 异常/Inf → 不产出

            var quality = WorstQuality(rt);
            outputs.Add(new TagValue(rt.Config.OutputItem, virtualValue, quality, engineering.Timestamp));
        }
    }

    private static double? TryEvaluate(FormulaRuntime rt)
    {
        try
        {
            var args = rt.Config.Inputs
                .Select(i => (object)rt.Inputs[i.Alias].value)
                .ToArray();
            var result = rt.Lambda.Invoke(args);
            var d = Convert.ToDouble(result, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return null;
            return d;
        }
        catch
        {
            return null; // 节流日志在 Task（可选）后续；此处静默不产出
        }
    }

    private static ushort WorstQuality(FormulaRuntime rt)
    {
        ushort worst = 0xC0;
        foreach (var i in rt.Config.Inputs)
        {
            var q = rt.Inputs[i.Alias].quality;
            // 取最差：Bad(0x00) > Uncertain(0x40) > Good(0xC0)（按高 2 位：00 < 01 < 11）
            if ((q & 0xC0) < (worst & 0xC0)) worst = q;
        }
        return worst;
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

    private sealed class FormulaRuntime
    {
        public FormulaConfig Config { get; }
        public Lambda Lambda { get; }
        public bool IsReady { get; set; }
        public bool IsFailed { get; set; }
        public Dictionary<string, (double value, ushort quality, bool seenGood)> Inputs { get; } = new();

        public FormulaRuntime(FormulaConfig c)
        {
            Config = c;
            var interp = new Interpreter();
            RegisterBuiltins(interp);
            var parameters = c.Inputs
                .Select(i => new Parameter(i.Alias, typeof(double)))
                .ToArray();
            Lambda = interp.Parse(c.Expression, parameters);
        }

        private static void RegisterBuiltins(Interpreter interp)
        {
            interp.SetFunction("SQRT", new Func<double, double>(Math.Sqrt));
            interp.SetFunction("ABS", new Func<double, double>(Math.Abs));
            interp.SetFunction("SIN", new Func<double, double>(Math.Sin));
            interp.SetFunction("COS", new Func<double, double>(Math.Cos));
            interp.SetFunction("TAN", new Func<double, double>(Math.Tan));
            interp.SetFunction("EXP", new Func<double, double>(Math.Exp));
            interp.SetFunction("LOG", new Func<double, double>(Math.Log));
            interp.SetFunction("LOG10", new Func<double, double>(Math.Log10));
            interp.SetFunction("FLOOR", new Func<double, double>(Math.Floor));
            interp.SetFunction("CEILING", new Func<double, double>(Math.Ceiling));
            interp.SetFunction("POW", new Func<double, double, double>(Math.Pow));
            interp.SetFunction("MIN", new Func<double, double, double>(Math.Min));
            interp.SetFunction("MAX", new Func<double, double, double>(Math.Max));
            interp.SetFunction("ROUND", new Func<double, double, double>((v, d) => Math.Round(v, (int)d)));
            interp.SetVariable("PI", Math.PI);
            interp.SetVariable("E", Math.E);
        }
    }
}
