using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Dashboard;
using Dc.App.Services;
using Dc.App.ViewModels.Dashboard;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.ViewModels.Workspace;

public sealed partial class TaskWorkspaceViewModel : ObservableObject
{
    private readonly IWorkspaceTaskSource _source;
    private readonly IDashboardOrchestratorView _orch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly TaskOrchestrator? _orchestrator;
    private readonly ITaskEditorDialog? _editor;
    private readonly Dc.App.Services.IConfirmDialog _confirm;
    private readonly Dc.App.Services.INotificationService _notify;
    private Dictionary<string, Dc.Domain.Entities.CollectorTask> _tasksById = new();

    public ObservableCollection<TaskMasterRow> AllTasks { get; } = new();
    public ICollectionView FilteredTasks { get; }

    public WorkspaceOverviewViewModel Overview { get; }
    public IEmbeddableTagPanel TagsPanel { get; }
    public IEmbeddableGroupPanel GroupsPanel { get; }
    public IEmbeddableLivePanel LivePanel { get; }
    public IEmbeddableDiagPanel DiagPanel { get; }
    public WorkspaceConfigViewModel Config { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private WorkspaceStatusFilter _statusFilter = WorkspaceStatusFilter.All;
    [ObservableProperty] private TaskMasterRow? _selectedTask;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _stoppedCount;
    [ObservableProperty] private int _alertCount;
    [ObservableProperty] private int _selectedGroupCount; // tab badge：分组
    [ObservableProperty] private int _selectedTagCount;   // tab badge：Tag
    [ObservableProperty] private string _selectedTab = "overview";
    [ObservableProperty] private object? _currentTabContent;

    private System.Windows.Threading.DispatcherTimer? _timer;

    public TaskWorkspaceViewModel(
        IWorkspaceTaskSource source,
        IDashboardOrchestratorView orchestratorView,
        Func<DateTimeOffset> clock,
        TimeSpan heartbeatTimeout,
        WorkspaceOverviewViewModel overview,
        IEmbeddableTagPanel tagsPanel,
        TaskOrchestrator? orchestrator = null,
        ITaskEditorDialog? editor = null,
        IEmbeddableGroupPanel? groupsPanel = null,
        IEmbeddableLivePanel? livePanel = null,
        IEmbeddableDiagPanel? diagPanel = null,
        WorkspaceConfigViewModel? config = null,
        Dc.App.Services.IConfirmDialog? confirm = null,
        Dc.App.Services.INotificationService? notify = null)
    {
        _source = source;
        _orch = orchestratorView;
        _clock = clock;
        _heartbeatTimeout = heartbeatTimeout;
        _orchestrator = orchestrator;
        _editor = editor;
        _confirm = confirm ?? new DenyConfirm();
        _notify = notify ?? new NullNotification();
        Overview = overview;
        TagsPanel = tagsPanel;
        TagsPanel.IsEmbedded = true;
        // Tag 面板无分组时,空状态 CTA 请求跳到「分组」页签创建分组。
        TagsPanel.NavigateToGroupsRequested += () => SelectedTab = "groups";

        GroupsPanel = groupsPanel ?? new NullGroupPanel();
        LivePanel = livePanel ?? new NullLivePanel();
        DiagPanel = diagPanel ?? new NullDiagPanel();
        Config = config ?? new WorkspaceConfigViewModel(editor ?? new NullTaskEditorDialog());

        GroupsPanel.IsEmbedded = true;
        GroupsPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IEmbeddableGroupPanel.SelectedGroup)
                && GroupsPanel.SelectedGroup is not null)
            {
                TagsPanel.GroupFilter = GroupsPanel.SelectedGroup;
                SelectedTab = "tags";
            }
        };
        // 分组面板无任务时,空状态 CTA 请求跳到任务列表新建任务。
        GroupsPanel.NavigateToTasksRequested += () => SelectedTab = "overview";
        // 任务编辑持久化走 VM(main 的任务 CRUD);PersistEditedAsync 内部已 LoadAsync 刷新列表。
        Config.Edited += async edited => await PersistEditedAsync(edited);

        FilteredTasks = CollectionViewSource.GetDefaultView(AllTasks);
        FilteredTasks.Filter = FilterRow;
        CurrentTabContent = Overview;
    }

    partial void OnSearchTextChanged(string value) => FilteredTasks.Refresh();
    partial void OnStatusFilterChanged(WorkspaceStatusFilter value) => FilteredTasks.Refresh();

    partial void OnSelectedTaskChanged(TaskMasterRow? value)
    {
        Overview.SetTask(value?.TaskId);
        if (value is not null)
        {
            TagsPanel.TaskScope = value.TaskId;
            _ = TagsPanel.LoadAsync();
        }
        SelectedTab = "overview";
        UpdateTabContent();
        Overview.Sample();

        var task = value is null ? null : _tasksById.GetValueOrDefault(value.TaskId);
        GroupsPanel.TaskFilter = task;
        _ = GroupsPanel.LoadAsync();
        LivePanel.TaskFilter = value?.TaskId;
        DiagPanel.TaskScope = value?.TaskId;
        Config.SetTask(task);

        if (value is null) { SelectedGroupCount = 0; SelectedTagCount = 0; }
        else _ = LoadCountsAsync(value.TaskId);
    }

    private async Task LoadCountsAsync(string taskId)
    {
        var (g, t) = await _source.GetCountsAsync(taskId);
        SelectedGroupCount = g;
        SelectedTagCount = t;
    }

    partial void OnSelectedTabChanged(string value) => UpdateTabContent();

    private void UpdateTabContent()
    {
        CurrentTabContent = SelectedTab switch
        {
            "tags"        => (object)TagsPanel,
            "groups"      => GroupsPanel,
            "livedata"    => LivePanel,
            "diagnostics" => DiagPanel,
            "config"      => Config,
            _             => Overview
        };
    }

    public void Start(System.Windows.Threading.Dispatcher dispatcher)
    {
        if (_timer is not null) return;
        _timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) =>
        {
            if (SelectedTab == "overview") Overview.Sample();
        };
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private bool FilterRow(object obj)
    {
        if (obj is not TaskMasterRow row) return false;
        if (StatusFilter == WorkspaceStatusFilter.Running && !row.IsRunning) return false;
        if (StatusFilter == WorkspaceStatusFilter.Stopped && row.IsRunning) return false;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            if (row.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && row.TaskId.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }
        return true;
    }

    public async Task LoadAsync()
    {
        var prevSelectedId = SelectedTask?.TaskId; // 重建后保留选中（启动/停止后不应回到未选中态）
        var tasks = await _source.LoadTasksAsync();
        _tasksById = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);

        var running = new HashSet<string>(_orch.RunningTaskIds, StringComparer.Ordinal);
        var diagnostics = _orch.GetDiagnostics();
        var diagByTask = diagnostics.ToDictionary(d => d.TaskId, StringComparer.Ordinal);

        // 任务列表 badge 用「已配置 Tag 数」(DB 口径,与运行状态无关)——
        // 此前用诊断的 SubscribedTagCount,但诊断只对运行中任务存在,导致已停止但已配置 Tag 的任务
        // 误显「未配置 Tag」。rate/s 仍取诊断(停止时自然 0)。
        var configuredTagCounts = await _source.GetConfiguredTagCountsAsync(tasks.Select(t => t.Id).ToList());

        AllTasks.Clear();
        foreach (var t in tasks)
        {
            // 任务可读名:统一用 CollectorTask.DisplayName(Name → Server → Id)。
            var row = new TaskMasterRow(t.Id, t.DisplayName, ProtocolLabel(t.Type))
            {
                IsRunning = running.Contains(t.Id),
                TagCount = configuredTagCounts.TryGetValue(t.Id, out var c) ? c : 0
            };
            AllTasks.Add(row);
        }

        RunningCount = AllTasks.Count(r => r.IsRunning);
        StoppedCount = AllTasks.Count - RunningCount;

        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: diagnostics,
            runningTaskIds: _orch.RunningTaskIds,
            now: _clock(),
            heartbeatTimeout: _heartbeatTimeout);
        AlertCount = snap.Alerts.Count;

        var alertTaskIds = snap.Alerts.Select(a => a.TaskId).ToHashSet(StringComparer.Ordinal);
        foreach (var row in AllTasks) row.HasAlert = alertTaskIds.Contains(row.TaskId);

        FilteredTasks.Refresh();

        // 重建后恢复选中：按 TaskId 找回对应新行（启动/停止/刷新都经此处）
        if (prevSelectedId is not null)
            SelectedTask = AllTasks.FirstOrDefault(r => r.TaskId == prevSelectedId);
    }

    private static string ProtocolLabel(byte type) => type switch
    {
        1 => "DA",
        2 => "UA",
        3 => "AE",
        _ => "?"
    };

    // ── Lifecycle commands ──────────────────────────────────────────────────

    public async Task StartSelectedAsync()
    {
        if (SelectedTask is null || _orchestrator is null) return;
        var (task, _, formulas) = await _source.GetTaskWithTagsAsync(SelectedTask.TaskId);
        if (task is null) return;

        // 映射口径与无头 Cli 共用（DbTaskLauncher 单一来源）：用带公式的重载，
        // 内部 MapRealTags 过滤虚拟 Tag 不进订阅器 + BuildTransformConfig 让缩放/公式生效。
        var req = DbTaskLauncher.ToStartRequest(task, formulas);

        try
        {
            await _orchestrator.StartAsync(req);
        }
        catch (Exception ex)
        {
            _notify.ShowError("任务启动失败", ex.Message);
        }
        await LoadAsync();
    }

    public async Task StopSelectedAsync()
    {
        if (SelectedTask is null || _orchestrator is null) return;
        await _orchestrator.StopAsync(SelectedTask.TaskId);
        await LoadAsync();
    }

    public async Task RestartSelectedAsync()
    {
        await StopSelectedAsync();
        await StartSelectedAsync();
    }

    [RelayCommand]
    public async Task NewTaskAsync()
    {
        if (_editor is null) return;
        var edited = _editor.Edit(null);
        if (edited is null) return;

        edited.Id = Dc.Infrastructure.Persistence.UlidGenerator.NewId();
        await _source.SaveNewTaskAsync(edited);
        await LoadAsync();
    }

    private async Task PersistEditedAsync(Dc.Domain.Entities.CollectorTask edited)
    {
        await _source.UpdateTaskAsync(edited);
        await LoadAsync();
    }

    public async Task EditSelectedAsync()
    {
        if (SelectedTask is null || _editor is null) return;
        var task = _tasksById.GetValueOrDefault(SelectedTask.TaskId);
        if (task is null) return;
        var edited = _editor.Edit(task);
        if (edited is null) return;
        await PersistEditedAsync(edited);
    }

    public async Task DeleteSelectedAsync()
    {
        if (SelectedTask is null) return;
        var id = SelectedTask.TaskId;
        var name = SelectedTask.Name;

        // 先确认，确认后才产生任何副作用（取消 = 真 no-op，不停不删）
        var (g, t) = await _source.GetCountsAsync(id);
        if (!_confirm.Confirm("删除任务",
                $"将删除任务「{name}」及其 {g} 个分组、{t} 个 Tag，不可恢复。确定删除？"))
            return;

        // 确认后：运行中先停，再级联删
        if (_orchestrator is not null && _orch.RunningTaskIds.Contains(id))
            await _orchestrator.StopAsync(id);

        await _source.DeleteTaskCascadeAsync(id);
        SelectedTask = null;
        await LoadAsync();
    }

    /// <summary>
    /// Delegates Excel import to the embedded TagsPanel, then switches to the Tags tab.
    /// </summary>
    public async Task ImportAsync()
    {
        await TagsPanel.ImportAsync();
        // LoadAsync 会恢复选中并经 OnSelectedTaskChanged 把页签重置为 overview，
        // 故切到 tags 必须放在 LoadAsync 之后，否则会被覆盖回 overview。
        await LoadAsync();
        SelectedTab = "tags";
    }

    // ── Null-object stubs for optional panels ──────────────────────────────

    private sealed class NullGroupPanel
        : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IEmbeddableGroupPanel
    {
        public bool IsEmbedded { get; set; }
        private Dc.Domain.Entities.CollectorTask? _taskFilter;
        public Dc.Domain.Entities.CollectorTask? TaskFilter
        {
            get => _taskFilter;
            set => SetProperty(ref _taskFilter, value);
        }
        public Dc.Domain.Entities.Group? SelectedGroup => null;
        public Task LoadAsync() => Task.CompletedTask;
        public event Action? NavigateToTasksRequested;
    }

    private sealed class NullLivePanel : IEmbeddableLivePanel
    {
        public string? TaskFilter { get; set; }
    }

    private sealed class NullDiagPanel : IEmbeddableDiagPanel
    {
        public string? TaskScope { get; set; }
    }

    private sealed class NullTaskEditorDialog : ITaskEditorDialog
    {
        public Dc.Domain.Entities.CollectorTask? Edit(Dc.Domain.Entities.CollectorTask? existing) => null;
    }

    // 未注入确认框时 fail-safe 拒绝删除：避免漏接 DI 导致无确认静默删除。
    // 生产经 ServiceRegistration 注入 WpfConfirmDialog（任务 8）；测试显式传 FakeConfirm。
    private sealed class DenyConfirm : Dc.App.Services.IConfirmDialog
    {
        public bool Confirm(string title, string message) => false;
    }

    // 未注入通知服务时兜底：测试/未接线场景不弹不崩。
    // 生产经 ServiceRegistration 注入（任务 3/4）。
    private sealed class NullNotification : Dc.App.Services.INotificationService
    {
        public void ShowError(string title, string message) { }
    }
}
