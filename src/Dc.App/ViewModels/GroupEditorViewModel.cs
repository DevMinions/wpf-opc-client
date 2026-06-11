using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dc.Domain.Entities;

namespace Dc.App.ViewModels;

public partial class GroupEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private CollectorTask? _task;

    public string? OriginalId { get; }
    public bool ShowTaskSelector { get; }
    public ObservableCollection<CollectorTask> AvailableTasks { get; } = new();

    public GroupEditorViewModel(IEnumerable<CollectorTask> tasks, Group? existing, CollectorTask? defaultTask = null)
    {
        foreach (var t in tasks) AvailableTasks.Add(t);

        if (existing is null)
        {
            _task = defaultTask;
            _title = _task is null ? "新建分组" : $"新建分组 · 任务：{_task.Server}";
        }
        else
        {
            OriginalId = existing.Id;
            _name = existing.Name;
            _task = AvailableTasks.FirstOrDefault(t => t.Id == existing.TaskId);
            _title = _task is null ? "编辑分组" : $"编辑分组 · 任务：{_task.Server}";
        }

        ShowTaskSelector = _task is null;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("名称不能为空");
        if (Task is null) errors.Add("必须选择所属任务");
        return errors;
    }

    public Group ToEntity() => new()
    {
        Id = OriginalId ?? string.Empty,
        Name = Name.Trim(),
        TaskId = Task!.Id
    };
}
