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
            // 任务名:统一用 CollectorTask.DisplayName(Name → Server → Id)。
            string TaskName(string tid) =>
                _taskById.TryGetValue(tid, out var t) ? t.DisplayName : tid;

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
        var result = await EditTagAsync(existing: null);
        if (result is null) return;
        await PersistNewAsync(result);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (SelectedTag is null) return;
        var existing = SelectedTag.Tag;
        var result = await EditTagAsync(existing: existing);
        if (result is null) return;
        await PersistEditAsync(existing, result);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var row = SelectedTag;
        if (row is null) return;
        var tag = row.Tag;

        // 引用完整性(Q5):真实 Tag 被公式引用 → 拦截。
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var referencingFormulas = await checkDb.FormulaInputs
            .Where(i => i.SourceTagId == tag.Id)
            .Join(checkDb.Formulas, i => i.FormulaId, f => f.Id, (i, f) => f.Name)
            .Distinct().ToListAsync();
        if (referencingFormulas.Count > 0)
        {
            MessageDialog.Show("无法删除",
                $"该测点被公式 {string.Join(", ", referencingFormulas)} 引用,请先修改公式或删除对应虚拟测点。",
                MessageDialogKind.Warning);
            return;
        }

        var confirm = MessageDialog.Confirm("删除确认", $"确定删除 Tag {tag.Item}？", MessageDialogKind.Warning);
        if (!confirm) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        // 虚拟 Tag:级联删其 Formula+Inputs。
        if (tag.IsVirtual)
        {
            var ownFormulas = await db.Formulas.Include(f => f.Inputs)
                .Where(f => f.OutputTagId == tag.Id).ToListAsync();
            if (ownFormulas.Count > 0) db.Formulas.RemoveRange(ownFormulas);
        }
        await db.Tags.Where(t => t.Id == tag.Id).ExecuteDeleteAsync();
        await db.SaveChangesAsync();

        if (!tag.IsVirtual) await TryHotRemoveAsync(tag.TaskId, tag.Item);
        Tags.Remove(row);
    }

    // Tag → TagRow:分组名/任务名解析与 LoadAsync 同口径。
    private TagRow ToRow(Tag t)
    {
        var gName = AvailableGroups.FirstOrDefault(g => g.Id == t.GroupId)?.Name ?? t.GroupId;
        var taskName = _taskById.TryGetValue(t.TaskId, out var tk) ? tk.DisplayName : t.TaskId;
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

    // 占位:Task 7 替换为真实实现。签名固定,供 NewAsync/EditAsync 调用。
    private async Task<TagEditResult?> EditTagAsync(Tag? existing)
    {
        // 任务上下文:优先当前工作台 TaskScope;独立页(无 TaskScope)编辑现有 Tag 时用其 TaskId。
        // 独立页新建虚拟测点无任务上下文 → AvailableInputTags 为空,虚拟创建受限(已知限制)。
        string? taskId = TaskScope ?? existing?.TaskId;
        IReadOnlyCollection<Tag>? taskTags = null;
        IReadOnlyCollection<Formula>? existingFormulas = null;
        if (taskId is not null)
        {
            taskTags = await LoadTaskTagsAsync(taskId);
            if (existing is not null && existing.IsVirtual)
                existingFormulas = await LoadTaskFormulasAsync(taskId);
        }

        return _editor.Edit(AvailableGroups, existing, GroupFilter,
            taskIdLookup => _taskById.TryGetValue(taskIdLookup, out var t) ? t : null,
            taskTags, existingFormulas);
    }

    private async Task<IReadOnlyCollection<Tag>> LoadTaskTagsAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Tags.AsNoTracking().Where(t => t.TaskId == taskId).ToListAsync();
    }

    private async Task<IReadOnlyCollection<Formula>> LoadTaskFormulasAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Formulas.AsNoTracking()
            .Include(f => f.Inputs)
            .Where(f => f.TaskId == taskId)
            .ToListAsync();
    }

    private async Task PersistNewAsync(TagEditResult result)
    {
        var tag = result.Tag;
        tag.Id = UlidGenerator.NewId();
        Formula? formula = null;
        if (result.Formula is not null)
        {
            formula = result.Formula;
            formula.Id = UlidGenerator.NewId();
            formula.OutputTagId = tag.Id;
            foreach (var inp in result.Inputs)
            {
                inp.Id = UlidGenerator.NewId();
                inp.FormulaId = formula.Id;
                formula.Inputs.Add(inp);
            }
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        try
        {
            db.Tags.Add(tag);
            if (formula is not null) db.Formulas.Add(formula); // EF 级联加 Inputs
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            MessageDialog.Show("错误", $"保存失败:{ex.InnerException?.Message ?? ex.Message}", MessageDialogKind.Error);
            return;
        }

        Tags.Add(ToRow(tag));

        // 热同步:真实 Tag 走现有路径;虚拟 Tag 不订阅(Q8),运行中提示重启。
        if (tag.IsVirtual)
        {
            if (IsTaskRunning(tag.TaskId))
                MessageDialog.Show("提示", "虚拟测点已保存,重启任务后生效。", MessageDialogKind.Info);
        }
        else
        {
            await TryHotAddAsync(tag);
        }
    }

    private async Task PersistEditAsync(Tag existing, TagEditResult result)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Tags.FirstOrDefaultAsync(t => t.Id == existing.Id);
        if (entity is null) return;

        var oldItem = entity.Item;
        var oldTaskId = entity.TaskId;
        var oldScale = entity.ScaleFactor;
        var oldOffset = entity.Offset;
        var wasVirtual = entity.IsVirtual;

        entity.Item = result.Tag.Item;
        entity.DataType = result.Tag.DataType;
        entity.GroupId = result.Tag.GroupId;
        entity.TaskId = result.Tag.TaskId;
        entity.IsVirtual = result.Tag.IsVirtual;
        entity.ScaleFactor = result.Tag.ScaleFactor;
        entity.Offset = result.Tag.Offset;

        // 公式变更:删旧(若曾虚拟),加新(若现虚拟)。
        if (wasVirtual)
        {
            var oldFormulas = await db.Formulas.Include(f => f.Inputs)
                .Where(f => f.OutputTagId == entity.Id).ToListAsync();
            if (oldFormulas.Count > 0) db.Formulas.RemoveRange(oldFormulas); // 级联删 Inputs
        }
        if (result.Formula is not null)
        {
            var f = result.Formula;
            f.Id = UlidGenerator.NewId();
            f.OutputTagId = entity.Id;
            foreach (var inp in result.Inputs)
            {
                inp.Id = UlidGenerator.NewId();
                inp.FormulaId = f.Id;
                f.Inputs.Add(inp);
            }
            db.Formulas.Add(f);
        }

        await db.SaveChangesAsync();

        var running = IsTaskRunning(entity.TaskId) || IsTaskRunning(oldTaskId);
        var scaleChanged = !Nullable.Equals(oldScale, entity.ScaleFactor) || !Nullable.Equals(oldOffset, entity.Offset);

        if (!wasVirtual && entity.IsVirtual)
        {
            // 真实 → 虚拟:卸载旧真实订阅;虚拟不订阅,运行中提示重启。
            await TryHotRemoveAsync(oldTaskId, oldItem);
            if (running) MessageDialog.Show("提示", "虚拟测点/公式已保存,重启任务后生效。", MessageDialogKind.Info);
        }
        else if (wasVirtual && !entity.IsVirtual)
        {
            // 虚拟 → 真实:旧虚拟未订阅无需卸载;热加新真实订阅;运行中提示重启(公式已移除)。
            await TryHotAddAsync(entity);
            if (running) MessageDialog.Show("提示", "虚拟测点/公式已保存,重启任务后生效。", MessageDialogKind.Info);
        }
        else if (!entity.IsVirtual && (oldItem != entity.Item || oldTaskId != entity.TaskId))
        {
            // 真实 → 真实(Item/Task 变):先卸旧再挂新。
            await TryHotRemoveAsync(oldTaskId, oldItem);
            await TryHotAddAsync(entity);
            if (running && scaleChanged)
                MessageDialog.Show("提示", "缩放/偏移已保存,重启任务后生效。", MessageDialogKind.Info);
        }
        else if (!entity.IsVirtual && running && scaleChanged)
        {
            // 真实 Tag 仅缩放/偏移变更:运行中 transform 用启动快照,提示重启生效。
            MessageDialog.Show("提示", "缩放/偏移已保存,重启任务后生效。", MessageDialogKind.Info);
        }
        else if (entity.IsVirtual && running)
        {
            // 虚拟 → 虚拟(公式变):不热同步,提示重启。
            MessageDialog.Show("提示", "虚拟测点/公式已保存,重启任务后生效。", MessageDialogKind.Info);
        }

        var idx = Tags.IndexOf(SelectedTag);
        if (idx >= 0)
        {
            var row = ToRow(entity);
            Tags[idx] = row;
            SelectedTag = row;
        }
    }

    private bool HasSelection() => SelectedTag is not null;

    partial void OnSelectedTagChanged(TagRow? value)
    {
        DeleteCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
    }
}
