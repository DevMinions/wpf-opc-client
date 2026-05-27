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
    public ObservableCollection<CollectorTask> AvailableTasks { get; } = new();

    public GroupEditorViewModel(IEnumerable<CollectorTask> tasks, Group? existing, CollectorTask? defaultTask = null)
    {
        foreach (var t in tasks) AvailableTasks.Add(t);

        if (existing is null)
        {
            _title = "新建分组";
            _task = defaultTask ?? AvailableTasks.FirstOrDefault();
        }
        else
        {
            _title = "编辑分组";
            OriginalId = existing.Id;
            _name = existing.Name;
            _task = AvailableTasks.FirstOrDefault(t => t.Id == existing.TaskId);
        }
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
