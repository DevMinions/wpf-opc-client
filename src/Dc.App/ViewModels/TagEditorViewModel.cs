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
    [ObservableProperty] private Group? _group;

    public string? OriginalId { get; }
    public ObservableCollection<Group> AvailableGroups { get; } = new();
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

        foreach (var g in groups) AvailableGroups.Add(g);

        if (existing is null)
        {
            _group = defaultGroup;
            _title = _group is null ? "新建 Tag" : $"新建 Tag · 分组：{_group.Name}";
        }
        else
        {
            OriginalId = existing.Id;
            _item = existing.Item;
            _dataType = OpcDataTypeOption.FromCode(existing.DataType);
            _group = AvailableGroups.FirstOrDefault(g => g.Id == existing.GroupId);
            _title = _group is null ? "编辑 Tag" : $"编辑 Tag · 分组：{_group.Name}";
        }

        ShowGroupSelector = _group is null;
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
