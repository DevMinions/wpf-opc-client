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
    [ObservableProperty] private TagRow? _selectedTag;
    [ObservableProperty] private Group? _groupFilter;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isEmbedded;
    [ObservableProperty] private string? _taskScope;

    /// <inheritdoc />
    public event Action? NavigateToGroupsRequested;

    // 嵌入主从视图时隐藏标题 + 筛选区域，避免把右侧按钮挤出视区
    public bool ShowFullToolbar => !IsEmbedded;
    public string EmbeddedTitle => GroupFilter is null ? "Tag ▸ (未选分组)" : $"Tag ▸ {GroupFilter.Name}";

    // 无分组时无法新建 Tag——禁用「新建」并用空状态引导,而非弹阻塞 MessageBox 把用户挡在死路上。
    // CTA 仅内嵌模式显示(有「分组」页签可跳);独立页只给文字引导。
    public string? CreateGroupCtaText => IsEmbedded ? "去创建分组" : null;
    partial void OnIsEmbeddedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFullToolbar));
        OnPropertyChanged(nameof(CreateGroupCtaText));
    }

    public ObservableCollection<TagRow> Tags { get; } = new();
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
            NewCommand.NotifyCanExecuteChanged(); // 分组数变化 → 「新建」可用性 + 空状态刷新
            var groupNameById = groups.ToDictionary(g => g.Id, g => g.Name);

            var tasks = await db.Tasks.AsNoTracking().ToListAsync();
            _taskById.Clear();
            foreach (var t in tasks) _taskById[t.Id] = t;
            // 任务名:优先 Name,回落 Server(UA URL/DA ProgID),再回落 Id——与列表口径一致。
            string TaskName(string tid) =>
                _taskById.TryGetValue(tid, out var t) && !string.IsNullOrWhiteSpace(t.Name) ? t.Name!
                : _taskById.TryGetValue(tid, out var t2) && !string.IsNullOrWhiteSpace(t2.Server) ? t2.Server
                : tid;

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
            foreach (var t in list)
            {
                var gName = groupNameById.TryGetValue(t.GroupId, out var gn) ? gn : t.GroupId;
                Tags.Add(new TagRow(t, gName, TaskName(t.TaskId)));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    // 无分组时禁用(空状态引导去创建分组),双重防御。
    private bool CanNew => AvailableGroups.Count > 0;

    [RelayCommand(CanExecute = nameof(CanNew))]
    private async Task NewAsync()
    {
        if (AvailableGroups.Count == 0) return;
        var edited = _editor.Edit(AvailableGroups, null, GroupFilter, taskId => _taskById.TryGetValue(taskId, out var t) ? t : null);
        if (edited is null) return;

        edited.Id = UlidGenerator.NewId();
        await using var db = await _dbFactory.CreateDbContextAsync();
        try
        {
            db.Tags.Add(edited);
            await db.SaveChangesAsync();
            Tags.Add(ToRow(edited));
            await TryHotAddAsync(edited);
        }
        catch (DbUpdateException ex)
        {
            MessageDialog.Show("错误", $"保存失败（可能 Item 已存在）：{ex.InnerException?.Message ?? ex.Message}", MessageDialogKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (SelectedTag is null) return;
        var edited = _editor.Edit(AvailableGroups, SelectedTag.Tag, null, taskId => _taskById.TryGetValue(taskId, out var t) ? t : null);
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
            var row = ToRow(entity);
            Tags[idx] = row;
            SelectedTag = row; // 替换后重选，保持高亮一致
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var row = SelectedTag;
        if (row is null) return;
        var tag = row.Tag;

        var confirm = MessageDialog.Confirm("删除确认", $"确定删除 Tag {tag.Item}？", MessageDialogKind.Warning);
        if (!confirm) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Tags.Where(t => t.Id == tag.Id).ExecuteDeleteAsync();
        await TryHotRemoveAsync(tag.TaskId, tag.Item);
        Tags.Remove(row);
    }

    // Tag → TagRow:分组名/任务名解析与 LoadAsync 同口径(名 → Server → Id)。
    private TagRow ToRow(Tag t)
    {
        var gName = AvailableGroups.FirstOrDefault(g => g.Id == t.GroupId)?.Name ?? t.GroupId;
        var taskName = _taskById.TryGetValue(t.TaskId, out var tk) && !string.IsNullOrWhiteSpace(tk.Name) ? tk.Name!
            : _taskById.TryGetValue(t.TaskId, out var tk2) && !string.IsNullOrWhiteSpace(tk2.Server) ? tk2.Server
            : t.TaskId;
        return new TagRow(t, gName, taskName);
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
            MessageDialog.Show("错误", $"读取失败: {ex.Message}", MessageDialogKind.Error);
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
        MessageDialog.Show("导入结果", msg, errors.Count > 0 ? MessageDialogKind.Warning : MessageDialogKind.Success);
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
            MessageDialog.Show("导出成功", $"已导出 {list.Count} 条到 {path}", MessageDialogKind.Success);
        }
        catch (Exception ex)
        {
            MessageDialog.Show("错误", $"导出失败: {ex.Message}", MessageDialogKind.Error);
        }
    }

    // 空状态 CTA:请求宿主(采集任务工作区)切到「分组」页签。独立页无宿主,事件无人订阅→按钮不显示。
    [RelayCommand]
    private void GoCreateGroup() => NavigateToGroupsRequested?.Invoke();

    partial void OnGroupFilterChanged(Group? value)
    {
        OnPropertyChanged(nameof(EmbeddedTitle));
        _ = LoadAsync();
    }

    private bool HasSelection() => SelectedTag is not null;

    partial void OnSelectedTagChanged(TagRow? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
    }
}
