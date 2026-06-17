using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Services;
using Dc.Domain.Entities;

namespace Dc.App.ViewModels;

public partial class TagEditorViewModel : ObservableObject
{
    private readonly IBrowseDialog? _browseDialog;
    private readonly Func<string, CollectorTask?>? _taskLookup;

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
        Func<string, CollectorTask?>? taskLookup = null)
    {
        _browseDialog = browseDialog;
        _taskLookup = taskLookup;

        // 任务名解析:有 taskLookup 才能拿到可读名;无则回退空(只显组名)。
        var rows = groups.Select(g =>
        {
            var taskName = taskLookup?.Invoke(g.TaskId)?.Server;
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
