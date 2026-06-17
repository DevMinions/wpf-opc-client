using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Services;
using Dc.App.ViewModels.Workspace;
using Dc.Domain.Entities;
using Dc.Infrastructure.Excel;
using Dc.Infrastructure.Orchestration;
using Dc.Infrastructure.Persistence;
using Dc.Opc.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dc.App.ViewModels;

public partial class TagsViewModel : ObservableObject, IEmbeddableTagPanel
{
    private readonly IDbContextFactory<DcDbContext> _dbFactory;
    private readonly ITagEditorDialog _editor;
    private readonly ITagExcelService _excel;
    private readonly IFilePicker _filePicker;
    private readonly TaskOrchestrator _orchestrator;

    [ObservableProperty] private string _title = "Tag 管理";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private Tag? _selectedTag;
    [ObservableProperty] private Group? _groupFilter;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isEmbedded;
    [ObservableProperty] private string? _taskScope;

    // 嵌入主从视图时隐藏标题 + 筛选区域，避免把右侧按钮挤出视区
    public bool ShowFullToolbar => !IsEmbedded;
    public string EmbeddedTitle => GroupFilter is null ? "Tag ▸ (未选分组)" : $"Tag ▸ {GroupFilter.Name}";
    partial void OnIsEmbeddedChanged(bool value) => OnPropertyChanged(nameof(ShowFullToolbar));

    public ObservableCollection<Tag> Tags { get; } = new();
    public ObservableCollection<Group> AvailableGroups { get; } = new();
    private readonly Dictionary<string, CollectorTask> _taskById = new();

    public TagsViewModel(
        IDbContextFactory<DcDbContext> dbFactory,
        ITagEditorDialog editor,
        ITagExcelService excel,
        IFilePicker filePicker,
        TaskOrchestrator orchestrator)
    {
        _dbFactory = dbFactory;
        _editor = editor;
        _excel = excel;
        _filePicker = filePicker;
        _orchestrator = orchestrator;
        _ = LoadAsync();
    }

    private bool IsTaskRunning(string taskId) =>
        _orchestrator.RunningTaskIds.Contains(taskId);

    private async Task TryHotAddAsync(Tag tag)
    {
        if (!IsTaskRunning(tag.TaskId)) return;
        try
        {
            await _orchestrator.AddTagsAsync(tag.TaskId,
                new[] { new TagDescriptor(tag.Id, tag.Item, tag.DataType) });
        }
        catch { /* 订阅器内部状态不一致时静默；下次重启会修正 */ }
    }

    private async Task TryHotRemoveAsync(string taskId, string item)
    {
        if (!IsTaskRunning(taskId)) return;
        try { await _orchestrator.RemoveTagsAsync(taskId, new[] { item }); }
        catch { /* same */ }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            // 分组按当前任务 TaskScope 过滤——否则 Tag 编辑器的所属分组下拉会列出其它任务的分组，
            // 选中后 tag.TaskId 取自该分组的 TaskId（见 TagEditorViewModel.Build），导致 Tag 静默落到错误任务下、永不订阅。
            var groupsQuery = db.Groups.AsNoTracking().AsQueryable();
            if (TaskScope is not null) groupsQuery = groupsQuery.Where(g => g.TaskId == TaskScope);
            var groups = await groupsQuery.OrderBy(g => g.CreatedAt).ToListAsync();
            AvailableGroups.Clear();
            foreach (var g in groups) AvailableGroups.Add(g);

            var tasks = await db.Tasks.AsNoTracking().ToListAsync();
            _taskById.Clear();
            foreach (var t in tasks) _taskById[t.Id] = t;

            var q = db.Tags.AsNoTracking().AsQueryable();
            if (TaskScope is not null) q = q.Where(t => t.TaskId == TaskScope);
            if (GroupFilter is not null) q = q.Where(t => t.GroupId == GroupFilter.Id);
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var pattern = $"%{SearchText.Trim()}%";
                q = q.Where(t => EF.Functions.Like(t.Item, pattern));
            }
            var list = await q.OrderBy(t => t.Item).Take(500).ToListAsync();
            Tags.Clear();
            foreach (var t in list) Tags.Add(t);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (AvailableGroups.Count == 0)
        {
            System.Windows.MessageBox.Show("请先创建分组，Tag 必须挂在分组下", "提示");
            return;
        }
        var edited = _editor.Edit(AvailableGroups, null, GroupFilter, taskId => _taskById.TryGetValue(taskId, out var t) ? t : null);
        if (edited is null) return;

        edited.Id = UlidGenerator.NewId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        try
        {
            db.Tags.Add(edited);
            await db.SaveChangesAsync();
            Tags.Add(edited);
            await TryHotAddAsync(edited);
        }
        catch (DbUpdateException ex)
        {
            System.Windows.MessageBox.Show($"保存失败（可能 Item 已存在）：{ex.InnerException?.Message ?? ex.Message}", "错误");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (SelectedTag is null) return;
        var edited = _editor.Edit(AvailableGroups, SelectedTag, null, taskId => _taskById.TryGetValue(taskId, out var t) ? t : null);
        if (edited is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Tags.FirstOrDefaultAsync(t => t.Id == edited.Id);
        if (entity is null) return;

        var oldItem = entity.Item;
        var oldTaskId = entity.TaskId;
        entity.Item = edited.Item;
        entity.DataType = edited.DataType;
        entity.GroupId = edited.GroupId;
        entity.TaskId = edited.TaskId;
        await db.SaveChangesAsync();

        // 热同步：先按旧 item/task 卸载，再按新挂载
        if (oldItem != entity.Item || oldTaskId != entity.TaskId)
        {
            await TryHotRemoveAsync(oldTaskId, oldItem);
            await TryHotAddAsync(entity);
        }

        var idx = Tags.IndexOf(SelectedTag);
        if (idx >= 0)
        {
            Tags[idx] = entity;
            SelectedTag = entity; // 替换后重选，保持高亮一致
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var tag = SelectedTag;
        if (tag is null) return;

        var confirm = System.Windows.MessageBox.Show(
            $"确定删除 Tag {tag.Item}？",
            "删除确认", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Tags.Where(t => t.Id == tag.Id).ExecuteDeleteAsync();
        await TryHotRemoveAsync(tag.TaskId, tag.Item);
        Tags.Remove(tag);
    }

    [RelayCommand]
    public async Task ImportAsync()
    {
        var path = _filePicker.PickOpenFile("Excel 工作簿|*.xlsx", "导入 Tag");
        if (path is null) return;

        IReadOnlyList<TagImportRow> rows;
        try
        {
            await using var fs = File.OpenRead(path);
            rows = _excel.Read(fs);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"读取失败: {ex.Message}", "错误");
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Name, g => g);

        var inserted = 0;
        var errors = new List<string>();
        var batch = new List<Tag>();
        foreach (var row in rows)
        {
            if (!groups.TryGetValue(row.GroupName, out var grp))
            {
                errors.Add($"未知分组: {row.GroupName} (Item={row.Item})");
                continue;
            }
            batch.Add(new Tag
            {
                Id = UlidGenerator.NewId(),
                Item = row.Item,
                DataType = row.DataType,
                GroupId = grp.Id,
                TaskId = grp.TaskId
            });
        }

        if (batch.Count > 0)
        {
            try
            {
                db.Tags.AddRange(batch);
                await db.SaveChangesAsync();
                inserted = batch.Count;

                // 热加：按 taskId 分组，给每个运行中的 task 调一次 AddTagsAsync
                foreach (var byTask in batch.GroupBy(t => t.TaskId))
                {
                    if (!IsTaskRunning(byTask.Key)) continue;
                    try
                    {
                        await _orchestrator.AddTagsAsync(byTask.Key,
                            byTask.Select(t => new TagDescriptor(t.Id, t.Item, t.DataType)).ToArray());
                    }
                    catch (Exception ex) { errors.Add($"任务 {byTask.Key} 热加失败: {ex.Message}"); }
                }
            }
            catch (DbUpdateException ex)
            {
                errors.Add($"插入失败（可能有重复 Item）: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        var msg = $"成功导入: {inserted} 条";
        if (errors.Count > 0)
            msg += $"\n错误 ({errors.Count}):\n" + string.Join("\n", errors.Take(8));
        System.Windows.MessageBox.Show(msg, "导入结果");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = _filePicker.PickSaveFile("Excel 工作簿|*.xlsx", $"tags-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx", "导出 Tag");
        if (path is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var groupMap = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name);

        var q = db.Tags.AsNoTracking().AsQueryable();
        if (GroupFilter is not null) q = q.Where(t => t.GroupId == GroupFilter.Id);
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var pattern = $"%{SearchText.Trim()}%";
            q = q.Where(t => EF.Functions.Like(t.Item, pattern));
        }
        var list = await q.OrderBy(t => t.Item).ToListAsync();

        try
        {
            await using var fs = File.Create(path);
            _excel.Write(list, groupMap, fs);
            System.Windows.MessageBox.Show($"已导出 {list.Count} 条到 {path}", "导出成功");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"导出失败: {ex.Message}", "错误");
        }
    }

    partial void OnGroupFilterChanged(Group? value)
    {
        OnPropertyChanged(nameof(EmbeddedTitle));
        _ = LoadAsync();
    }

    private bool HasSelection() => SelectedTag is not null;

    partial void OnSelectedTagChanged(Tag? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
    }
}
