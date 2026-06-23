using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Services;
using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;

namespace Dc.App.ViewModels;

public partial class TagEditorViewModel : ObservableObject
{
    private readonly string _taskId; // 分组层已去除:Tag 直接挂任务,编辑器固定此任务
    private readonly IBrowseDialog? _browseDialog;
    private readonly Func<string, CollectorTask?>? _taskLookup;
    private readonly IReadOnlyCollection<Tag>? _taskTags;
    private readonly IReadOnlyCollection<Formula>? _existingFormulas;
    private readonly IFormulaValidator? _formulaValidator;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _item = string.Empty;
    [ObservableProperty] private OpcDataTypeOption _dataType = OpcDataTypeOption.FromCode(0);
    [ObservableProperty] private bool _isVirtual;
    [ObservableProperty] private string _scaleFactor = string.Empty;
    [ObservableProperty] private string _offset = string.Empty;
    [ObservableProperty] private string _formulaName = string.Empty;
    [ObservableProperty] private string _expression = string.Empty;
    [ObservableProperty] private string _outputUnit = string.Empty;

    // 实时校验:无错才可保存(对齐任务编辑器),首条错误内联红字提示。
    [ObservableProperty] private bool _canSave;
    [ObservableProperty] private string _validationError = string.Empty;

    public ObservableCollection<InputBindingRow> InputBindings { get; } = new();
    public ObservableCollection<Tag> AvailableInputTags { get; } = new();

    public string? OriginalId { get; }
    public IReadOnlyList<OpcDataTypeOption> DataTypeOptions => OpcDataTypeOption.All;

    public TagEditorViewModel(
        string taskId,
        Tag? existing,
        IBrowseDialog? browseDialog = null,
        Func<string, CollectorTask?>? taskLookup = null,
        IReadOnlyCollection<Tag>? taskTags = null,
        IReadOnlyCollection<Formula>? existingFormulas = null,
        IFormulaValidator? formulaValidator = null)
    {
        _taskId = taskId;
        _taskTags = taskTags;
        _existingFormulas = existingFormulas;
        _formulaValidator = formulaValidator;
        _browseDialog = browseDialog;
        _taskLookup = taskLookup;

        if (existing is null)
        {
            _title = "新建 Tag";
        }
        else
        {
            OriginalId = existing.Id;
            _item = existing.Item;
            _dataType = OpcDataTypeOption.FromCode(existing.DataType);
            _scaleFactor = existing.ScaleFactor.HasValue
                ? existing.ScaleFactor.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            _offset = existing.Offset.HasValue
                ? existing.Offset.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            _title = "编辑 Tag";
        }

        Formula? existingVirtualFormula = null;
        // 编辑已存在虚拟 Tag:回填公式字段并预选输入。
        if (existing is not null && existing.IsVirtual && _existingFormulas is not null)
        {
            existingVirtualFormula = _existingFormulas.FirstOrDefault(x => x.OutputTagId == existing.Id);
            if (existingVirtualFormula is not null)
            {
                _isVirtual = true;
                _formulaName = existingVirtualFormula.Name;
                _expression = existingVirtualFormula.Expression;
                _outputUnit = existingVirtualFormula.OutputUnit ?? string.Empty;
            }
        }

        RefreshAvailableInputTags();
        if (existingVirtualFormula is not null)
        {
            foreach (var input in existingVirtualFormula.Inputs)
            {
                var row = new InputBindingRow(input.Alias)
                {
                    SelectedTag = AvailableInputTags.FirstOrDefault(t => t.Id == input.SourceTagId)
                };
                row.PropertyChanged += OnInputRowChanged;
                InputBindings.Add(row);
            }
        }
        else if (_isVirtual)
        {
            RebuildInputBindings();
        }
        Revalidate(); // 初始态:新建真实 Tag(Item 空)→ 保存禁用,直到填合法
    }

    // 实时校验:复用 Validate(),无错才可保存,首条错误内联提示。
    private void Revalidate()
    {
        var errs = Validate();
        CanSave = errs.Count == 0;
        ValidationError = errs.Count == 0 ? string.Empty : errs[0];
    }

    private void OnInputRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InputBindingRow.SelectedTag)) Revalidate();
    }

    public bool CanBrowse => _browseDialog is not null;

    // 与 Infrastructure FormulaBuiltins 内容一致(那里 internal static,App 引不到)。
    // 语义正确性最终由 IFormulaValidator.Validate(内部用真实 FormulaBuiltins)兜底。
    private static readonly HashSet<string> BuiltinNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SQRT","ABS","SIN","COS","TAN","ASIN","ACOS","ATAN","EXP","LOG","LOG10",
        "FLOOR","CEILING","POW","MIN","MAX","ROUND","IF","AVG","SUM","PI","E"
    };

    /// <summary>
    /// 扫描表达式标识符,排除内置函数/常量,去重保序(首次出现顺序)。
    /// 仅用于生成输入映射行 UI;最终校验由 IFormulaValidator.Validate 兜底。
    /// </summary>
    public static IReadOnlyList<string> ExtractAliases(string expression)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (Match m in Regex.Matches(expression ?? string.Empty, @"[A-Za-z_][A-Za-z0-9_]*"))
        {
            var name = m.Value;
            if (BuiltinNames.Contains(name)) continue;
            if (seen.Add(name)) ordered.Add(name);
        }
        return ordered;
    }

    [RelayCommand]
    private void Browse()
    {
        if (_browseDialog is null) return;
        var task = _taskLookup?.Invoke(_taskId);
        string? nodeId;
        if (task is null)
        {
            nodeId = _browseDialog.PickNodeId();
        }
        else
        {
            // 镜像 DbTaskLauncher 的任务→连接映射:UA 端点在 Server(opc.tcp);DA/AE 用 Node=host、
            // Server=ProgID、Clsid 兜底。把协议+端点预填给浏览对话框并自动连接,用户直接看到地址树。
            var protocol = (OpcProtocol)task.Type;
            string? serverUri = task.Node;
            string? progId = task.Server;
            if (protocol == OpcProtocol.Ua && task.Server.StartsWith("opc.tcp", StringComparison.OrdinalIgnoreCase))
            {
                serverUri = task.Server;
                progId = null;
            }
            nodeId = _browseDialog.PickNodeId(protocol, serverUri, progId, task.Clsid);
        }
        if (!string.IsNullOrWhiteSpace(nodeId)) Item = nodeId;
    }

    partial void OnIsVirtualChanged(bool value)
    {
        if (value) RebuildInputBindings(); // 内部 Revalidate
        else { InputBindings.Clear(); Revalidate(); }
    }

    partial void OnExpressionChanged(string value)
    {
        if (IsVirtual) RebuildInputBindings(); // 内部 Revalidate
    }

    // 实时校验:这些字段变化即重算可保存性。
    partial void OnItemChanged(string value) => Revalidate();
    partial void OnScaleFactorChanged(string value) => Revalidate();
    partial void OnOffsetChanged(string value) => Revalidate();
    partial void OnFormulaNameChanged(string value) => Revalidate();

    private void RefreshAvailableInputTags()
    {
        AvailableInputTags.Clear();
        if (_taskTags is null) return;
        foreach (var t in _taskTags.Where(t => !t.IsVirtual && t.Id != OriginalId))
            AvailableInputTags.Add(t);
    }

    // 表达式变化:保留仍存在别名的已选 Tag,移除消失别名,追加新别名(null)。
    private void RebuildInputBindings()
    {
        var aliases = ExtractAliases(Expression);
        var prevByAlias = InputBindings.ToDictionary(r => r.Alias, r => r.SelectedTag, StringComparer.OrdinalIgnoreCase);
        foreach (var r in InputBindings) r.PropertyChanged -= OnInputRowChanged; // 退订旧行,避免重复触发
        InputBindings.Clear();
        foreach (var alias in aliases)
        {
            var row = new InputBindingRow(alias);
            if (prevByAlias.TryGetValue(alias, out var sel)) row.SelectedTag = sel;
            row.PropertyChanged += OnInputRowChanged;
            InputBindings.Add(row);
        }
        Revalidate();
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!IsVirtual)
        {
            if (string.IsNullOrWhiteSpace(Item)) errors.Add("Item 不能为空");
            if (!string.IsNullOrWhiteSpace(ScaleFactor)
                && !double.TryParse(ScaleFactor.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                errors.Add("缩放系数必须是数字");
            if (!string.IsNullOrWhiteSpace(Offset)
                && !double.TryParse(Offset.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                errors.Add("偏移量必须是数字");
            return errors;
        }

        // 虚拟模式
        if (string.IsNullOrWhiteSpace(FormulaName)) errors.Add("公式名不能为空");
        else if (_taskTags is not null)
        {
            // 任务内唯一(排除自身):比对其余虚拟 Tag 的 Item(虚拟 Tag Item=公式名)
            var dup = _taskTags.Any(t => t.Id != OriginalId
                && t.IsVirtual
                && string.Equals(t.Item, FormulaName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (dup) errors.Add("公式名在任务内已存在");
        }
        if (string.IsNullOrWhiteSpace(Expression)) errors.Add("表达式不能为空");

        // 每个提取出的变量必须选了 Tag
        foreach (var row in InputBindings.Where(r => r.SelectedTag is null))
            errors.Add($"变量 {row.Alias} 未选择输入测点");

        // 类型可数值化 + 表达式语法
        var aliasToDataType = InputBindings
            .Where(r => r.SelectedTag is not null)
            .ToDictionary(r => r.Alias, r => r.SelectedTag!.DataType);
        if (_formulaValidator is not null
            && !_formulaValidator.Validate(Expression, aliasToDataType, out var ferr))
            errors.Add(ferr!);

        return errors;
    }

    public TagEditResult ToResult()
    {
        var tag = new Tag
        {
            Id = OriginalId ?? string.Empty,
            Item = IsVirtual ? FormulaName.Trim() : Item.Trim(),
            DataType = DataType.Code,
            TaskId = _taskId,
            IsVirtual = IsVirtual,
            ScaleFactor = IsVirtual ? null : ParseDouble(ScaleFactor),
            Offset = IsVirtual ? null : ParseDouble(Offset)
        };

        if (!IsVirtual)
            return new TagEditResult(tag, null, Array.Empty<FormulaInput>());

        var formula = new Formula
        {
            Id = string.Empty, // 调用方生成
            Name = FormulaName.Trim(),
            Expression = Expression,
            OutputTagId = tag.Id, // 调用方在持久化时回填真实 Id
            OutputUnit = string.IsNullOrWhiteSpace(OutputUnit) ? null : OutputUnit.Trim(),
            TaskId = _taskId
        };
        var inputs = InputBindings
            .Where(r => r.SelectedTag is not null)
            .Select(r => new FormulaInput
            {
                Id = string.Empty,
                FormulaId = string.Empty, // 调用方回填
                Alias = r.Alias,
                SourceTagId = r.SelectedTag!.Id
            })
            .ToList();
        return new TagEditResult(tag, formula, inputs);
    }

    private static double? ParseDouble(string s)
        => double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>
/// 输入映射行:从表达式提取的别名(只读)+ 用户选的同任务真实 Tag。
/// </summary>
public sealed partial class InputBindingRow : ObservableObject
{
    public string Alias { get; }
    [ObservableProperty] private Tag? _selectedTag;

    public InputBindingRow(string alias) => Alias = alias;
}
