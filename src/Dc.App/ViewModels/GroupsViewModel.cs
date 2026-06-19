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
    private readonly Dictionary<string, CollectorTask> _taskById = new();

    [ObservableProperty] private string _title = "分组管理";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private GroupListRow? _selectedRow;
    [ObservableProperty] private CollectorTask? _taskFilter;
    [ObservableProperty] private bool _isEmbedded;

    // 嵌入主从视图时隐藏标题 + 筛选区域，避免把右侧按钮挤出视区
    public bool ShowFullToolbar => !IsEmbedded;
    // 内嵌模式(工作区已选任务)下「所属任务」列冗余——上下文已定任务,隐藏。
    public bool ShowTaskColumn => !IsEmbedded;
    public string EmbeddedTitle => TaskFilter is null ? "分组 ▸ (未选任务)" : $"分组 ▸ {TaskDisplayName(TaskFilter)}";
    // 无任务时无法新建分组(分组必须挂任务)。独立页无宿主可跳 → 只文字引导;
    // 内嵌模式 CTA 跳「采集任务」页签(事件经宿主 TaskWorkspaceViewModel 订阅)。
    public string? CreateTaskCtaText => IsEmbedded ? "去新建任务" : null;

    /// <summary>内嵌模式无任务时,请求宿主(采集任务工作区)切到任务列表新建任务。</summary>
    public event Action? NavigateToTasksRequested;

    /// <inheritdoc />
    // IEmbeddableGroupPanel 契约暴露 Group?;实际选中行见 SelectedRow。透传实体给宿主切 Tag tab。
    public Group? SelectedGroup => SelectedRow?.Group;

    public ObservableCollection<GroupListRow> Groups { get; } = new();
    public ObservableCollection<CollectorTask> AvailableTasks { get; } = new();

    public GroupsViewModel(IDbContextFactory<DcDbContext> dbFactory, IGroupEditorDialog editor, TaskOrchestrator orchestrator)
    {
        _dbFactory = dbFactory;
        _editor = editor;
        _orchestrator = orchestrator;
        _ = LoadAsync();
    }

    partial void OnIsEmbeddedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFullToolbar));
        OnPropertyChanged(nameof(ShowTaskColumn));
        OnPropertyChanged(nameof(CreateTaskCtaText));
    }
    partial void OnTaskFilterChanged(CollectorTask? value)
    {
        OnPropertyChanged(nameof(EmbeddedTitle));
        _ = LoadAsync();
    }

    // 任务可读名:统一用 CollectorTask.DisplayName(Name → Server → Id)。
    private static string TaskDisplayName(CollectorTask t) => t.DisplayName;

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
            _taskById.Clear();
            foreach (var t in tasks) { AvailableTasks.Add(t); _taskById[t.Id] = t; }
            NewCommand.NotifyCanExecuteChanged(); // 任务数变化 → 「新建」可用性 + 空状态刷新

            var q = db.Groups.AsNoTracking().AsQueryable();
            if (TaskFilter is not null) q = q.Where(g => g.TaskId == TaskFilter.Id);
            var list = await q.OrderBy(g => g.CreatedAt).ToListAsync();
            Groups.Clear();
            foreach (var g in list)
            {
                var taskName = _taskById.TryGetValue(g.TaskId, out var t) ? TaskDisplayName(t) : g.TaskId;
                Groups.Add(new GroupListRow(g, taskName));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    // 无任务时禁用(空状态引导去新建任务),双重防御。
    private bool CanNew => AvailableTasks.Count > 0;

    [RelayCommand(CanExecute = nameof(CanNew))]
    private async Task NewAsync()
    {
        if (AvailableTasks.Count == 0) return;
        var edited = _editor.Edit(AvailableTasks, null, TaskFilter);
        if (edited is null) return;

        edited.Id = UlidGenerator.NewId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Groups.Add(edited);
        await db.SaveChangesAsync();
        var taskName = _taskById.TryGetValue(edited.TaskId, out var t) ? TaskDisplayName(t) : edited.TaskId;
        Groups.Add(new GroupListRow(edited, taskName));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (SelectedRow is null) return;
        var edited = _editor.Edit(AvailableTasks, SelectedRow.Group);
        if (edited is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Groups.FirstOrDefaultAsync(g => g.Id == edited.Id);
        if (entity is null) return;
        entity.Name = edited.Name;
        entity.TaskId = edited.TaskId;
        await db.SaveChangesAsync();

        var idx = Groups.IndexOf(SelectedRow);
        if (idx >= 0)
        {
            var taskName = _taskById.TryGetValue(entity.TaskId, out var t) ? TaskDisplayName(t) : entity.TaskId;
            var row = new GroupListRow(entity, taskName);
            Groups[idx] = row;
            SelectedRow = row; // 替换后重选，避免下游 TagsPanel.GroupFilter 被清空
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var row = SelectedRow;
        if (row is null) return;
        var group = row.Group;

        // 确认文案不带 ULID(对用户无意义),只显分组名。
        var confirm = MessageDialog.Confirm("删除确认",
            $"确定删除分组 {group.Name}？\n会同时清除该分组下的所有 Tag。",
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

        Groups.Remove(row);
    }

    // 空状态 CTA:请求宿主切到任务列表。独立页无宿主 → 事件无人订阅 → 按钮不显示。
    [RelayCommand]
    private void GoCreateTask() => NavigateToTasksRequested?.Invoke();

    private bool HasSelection() => SelectedRow is not null;

    partial void OnSelectedRowChanged(GroupListRow? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        // 接口契约 SelectedGroup 透传自 SelectedRow.Group;通知宿主(TaskWorkspaceViewModel)切 Tag tab。
        OnPropertyChanged(nameof(SelectedGroup));
    }
}
