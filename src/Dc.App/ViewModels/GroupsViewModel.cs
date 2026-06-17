using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Services;
using Dc.Domain.Entities;
using Dc.App.ViewModels.Workspace;
using Dc.Infrastructure.Orchestration;
using Dc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dc.App.ViewModels;

public partial class GroupsViewModel : ObservableObject, IEmbeddableGroupPanel
{
    private readonly IDbContextFactory<DcDbContext> _dbFactory;
    private readonly IGroupEditorDialog _editor;
    private readonly TaskOrchestrator _orchestrator;

    [ObservableProperty] private string _title = "分组管理";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private Group? _selectedGroup;
    [ObservableProperty] private CollectorTask? _taskFilter;
    [ObservableProperty] private bool _isEmbedded;

    // 嵌入主从视图时隐藏标题 + 筛选区域，避免把右侧按钮挤出视区
    public bool ShowFullToolbar => !IsEmbedded;
    public string EmbeddedTitle => TaskFilter is null ? "分组 ▸ (未选任务)" : $"分组 ▸ {TaskFilter.Server}";
    partial void OnIsEmbeddedChanged(bool value) => OnPropertyChanged(nameof(ShowFullToolbar));
    partial void OnTaskFilterChanged(CollectorTask? value)
    {
        OnPropertyChanged(nameof(EmbeddedTitle));
        _ = LoadAsync();
    }

    public ObservableCollection<Group> Groups { get; } = new();
    public ObservableCollection<CollectorTask> AvailableTasks { get; } = new();

    public GroupsViewModel(IDbContextFactory<DcDbContext> dbFactory, IGroupEditorDialog editor, TaskOrchestrator orchestrator)
    {
        _dbFactory = dbFactory;
        _editor = editor;
        _orchestrator = orchestrator;
        _ = LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var tasks = await db.Tasks.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();
            AvailableTasks.Clear();
            foreach (var t in tasks) AvailableTasks.Add(t);

            var q = db.Groups.AsNoTracking().AsQueryable();
            if (TaskFilter is not null) q = q.Where(g => g.TaskId == TaskFilter.Id);
            var list = await q.OrderBy(g => g.CreatedAt).ToListAsync();
            Groups.Clear();
            foreach (var g in list) Groups.Add(g);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (AvailableTasks.Count == 0)
        {
            MessageDialog.Show("提示", "请先创建任务，分组必须挂在任务下", MessageDialogKind.Warning);
            return;
        }
        var edited = _editor.Edit(AvailableTasks, null, TaskFilter);
        if (edited is null) return;

        edited.Id = UlidGenerator.NewId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Groups.Add(edited);
        await db.SaveChangesAsync();
        Groups.Add(edited);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (SelectedGroup is null) return;
        var edited = _editor.Edit(AvailableTasks, SelectedGroup);
        if (edited is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Groups.FirstOrDefaultAsync(g => g.Id == edited.Id);
        if (entity is null) return;
        entity.Name = edited.Name;
        entity.TaskId = edited.TaskId;
        await db.SaveChangesAsync();

        var idx = Groups.IndexOf(SelectedGroup);
        if (idx >= 0)
        {
            Groups[idx] = entity;
            SelectedGroup = entity; // 替换后重选，避免下游 TagsPanel.GroupFilter 被清空
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var group = SelectedGroup;
        if (group is null) return;

        var confirm = MessageDialog.Confirm("删除确认",
            $"确定删除分组 {group.Name} ({group.Id})？\n会同时清除该分组下的所有 Tag。",
            MessageDialogKind.Warning);
        if (!confirm) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        // 查出待删 Tag 的 item，热卸载用
        var tagsInGroup = await db.Tags.AsNoTracking()
            .Where(t => t.GroupId == group.Id)
            .Select(t => new { t.TaskId, t.Item })
            .ToListAsync();

        await db.Groups.Where(g => g.Id == group.Id).ExecuteDeleteAsync();
        await db.Tags.Where(t => t.GroupId == group.Id).ExecuteDeleteAsync();

        if (_orchestrator.RunningTaskIds.Contains(group.TaskId) && tagsInGroup.Count > 0)
        {
            try
            {
                await _orchestrator.RemoveTagsAsync(group.TaskId,
                    tagsInGroup.Select(t => t.Item).ToArray());
            }
            catch { /* swallow */ }
        }

        Groups.Remove(group);
    }


    private bool HasSelection() => SelectedGroup is not null;

    partial void OnSelectedGroupChanged(Group? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
    }
}
