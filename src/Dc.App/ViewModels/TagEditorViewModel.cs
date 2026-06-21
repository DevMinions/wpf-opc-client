using System.Collections.ObjectModel;
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
            selected = rows.FirstOrDefault(r => r.Group.Id == existing.GroupId);
            _title = selected is null ? "编辑 Tag" : $"编辑 Tag · 分组：{selected.Group.Name}";
        }
        _selectedGroupRow = selected;

        ShowGroupSelector = selected is null;
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

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Item)) errors.Add("Item 不能为空");
        if (Group is null) errors.Add("必须选择所属分组");
        return errors;
    }

    public Tag ToEntity() => new()
    {
        Id = OriginalId ?? string.Empty,
        Item = Item.Trim(),
        DataType = DataType.Code,
        GroupId = Group!.Id,
        TaskId = Group!.TaskId
    };

    // 真实 Tag 结果(本 task 占位);虚拟分支在 Task 4 补全。
    public TagEditResult ToResult() => new(ToEntity(), null, Array.Empty<FormulaInput>());
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
