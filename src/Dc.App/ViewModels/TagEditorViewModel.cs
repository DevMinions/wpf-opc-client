using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Services;
using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;
using Group = Dc.Domain.Entities.Group;

namespace Dc.App.ViewModels;

public partial class TagEditorViewModel : ObservableObject
{
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

    public ObservableCollection<InputBindingRow> InputBindings { get; } = new();
    public ObservableCollection<Tag> AvailableInputTags { get; } = new();

    // 下拉项包装：Group 实体 + 人类可读 Display。此前 ItemTemplate 直接绑 {TaskId}(26 位 ULID),
    // 折叠态显示「组名 — 01KVAS6GHVSDC5B62ZTXXY596Z」。现 Display 用任务显示名;同任务下只显组名。
    public ObservableCollection<GroupRow> AvailableGroups { get; } = new();
    [ObservableProperty] private GroupRow? _selectedGroupRow;

    public Group? Group => SelectedGroupRow?.Group;

    public string? OriginalId { get; }
    public IReadOnlyList<OpcDataTypeOption> DataTypeOptions => OpcDataTypeOption.All;
    public bool ShowGroupSelector { get; }

    public TagEditorViewModel(
        IEnumerable<Group> groups,
        Tag? existing,
        Group? defaultGroup = null,
        IBrowseDialog? browseDialog = null,
        Func<string, CollectorTask?>? taskLookup = null,
        IReadOnlyCollection<Tag>? taskTags = null,
        IReadOnlyCollection<Formula>? existingFormulas = null,
        IFormulaValidator? formulaValidator = null)
    {
        _taskTags = taskTags;
        _existingFormulas = existingFormulas;
        _formulaValidator = formulaValidator;
        _browseDialog = browseDialog;
        _taskLookup = taskLookup;

        // 任务名解析:用 CollectorTask.DisplayName(Name → Server → Id);无 taskLookup 则回退空(只显组名)。
        var rows = groups.Select(g =>
        {
            var task = taskLookup?.Invoke(g.TaskId);
            var taskName = task is not null ? task.DisplayName : null;
            return new GroupRow(g, taskName);
        }).ToList();
        var multiTask = rows.Select(r => r.Group.TaskId).Distinct().Count() > 1;
        foreach (var r in rows) r.FinalizeDisplay(multiTask);
        foreach (var r in rows) AvailableGroups.Add(r);

        GroupRow? selected;
        if (existing is null)
        {
            selected = defaultGroup is null ? null : rows.FirstOrDefault(r => r.Group.Id == defaultGroup.Id);
            _title = selected is null ? "新建 Tag" : $"新建 Tag · 分组：{selected.Group.Name}";
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
            selected = rows.FirstOrDefault(r => r.Group.Id == existing.GroupId);
            _title = selected is null ? "编辑 Tag" : $"编辑 Tag · 分组：{selected.Group.Name}";
        }
        _selectedGroupRow = selected;

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

        ShowGroupSelector = selected is null;
        RefreshAvailableInputTags();
        if (existingVirtualFormula is not null)
        {
            foreach (var input in existingVirtualFormula.Inputs)
            {
                var row = new InputBindingRow(input.Alias)
                {
                    SelectedTag = AvailableInputTags.FirstOrDefault(t => t.Id == input.SourceTagId)
                };
                InputBindings.Add(row);
            }
        }
        else if (_isVirtual)
        {
            RebuildInputBindings();
        }
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
        string? initialUri = null;
        if (Group is not null && _taskLookup is not null)
        {
            var task = _taskLookup(Group.TaskId);
            if (task is not null && !string.IsNullOrWhiteSpace(task.Node))
                initialUri = task.Node;
        }
        var nodeId = _browseDialog.PickNodeId(initialUri);
        if (!string.IsNullOrWhiteSpace(nodeId)) Item = nodeId;
    }

    partial void OnSelectedGroupRowChanged(GroupRow? value)
    {
        // 分组定 → TaskId 定 → 刷新可选输入 Tag(同任务真实,排除自身虚拟)。
        OnPropertyChanged(nameof(Group));
        RefreshAvailableInputTags();
    }

    partial void OnIsVirtualChanged(bool value)
    {
        if (value) RebuildInputBindings();
        else InputBindings.Clear();
    }

    partial void OnExpressionChanged(string value)
    {
        if (IsVirtual) RebuildInputBindings();
    }

    private void RefreshAvailableInputTags()
    {
        AvailableInputTags.Clear();
        if (_taskTags is null || Group is null) return;
        foreach (var t in _taskTags.Where(t => !t.IsVirtual && t.Id != OriginalId))
            AvailableInputTags.Add(t);
    }

    // 表达式变化:保留仍存在别名的已选 Tag,移除消失别名,追加新别名(null)。
    private void RebuildInputBindings()
    {
        var aliases = ExtractAliases(Expression);
        var prevByAlias = InputBindings.ToDictionary(r => r.Alias, r => r.SelectedTag, StringComparer.OrdinalIgnoreCase);
        InputBindings.Clear();
        foreach (var alias in aliases)
        {
            var row = new InputBindingRow(alias);
            if (prevByAlias.TryGetValue(alias, out var sel)) row.SelectedTag = sel;
            InputBindings.Add(row);
        }
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Group is null) errors.Add("必须选择所属分组");

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
            GroupId = Group!.Id,
            TaskId = Group!.TaskId,
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
            TaskId = Group!.TaskId
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
/// 分组下拉项:Group 实体 + 可读 Display。Display 在 FinalizeDisplay 后确定:
/// 跨任务(独立 Tag 管理页)显示「组名 — 任务名」,同任务(嵌入工作区)只显组名。
/// </summary>
public sealed class GroupRow
{
    public Group Group { get; }
    public string Display { get; private set; }
    private readonly string? _taskName;

    public GroupRow(Group group, string? taskName)
    {
        Group = group;
        _taskName = taskName;
        Display = group.Name;
    }

    internal void FinalizeDisplay(bool multiTask)
    {
        Display = multiTask && !string.IsNullOrWhiteSpace(_taskName)
            ? $"{Group.Name} — {_taskName}"
            : Group.Name;
    }
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
