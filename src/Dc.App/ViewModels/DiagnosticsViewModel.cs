using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.ViewModels.Workspace;
using Dc.Infrastructure.Orchestration;
using Dc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dc.App.ViewModels;

public partial class DiagnosticsViewModel : ObservableObject, IDisposable, IEmbeddableDiagPanel
{
    private readonly TaskOrchestrator _orchestrator;
    private readonly Action<string>? _navigate;
    private readonly IDbContextFactory<DcDbContext>? _dbFactory; // 解析任务可读名;未注入(测试)时列回退显示 id
    private IReadOnlyDictionary<string, string> _taskNames = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, DiagnosticsRowViewModel> _rowIndex = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    [ObservableProperty] private string _title = "诊断";
    [ObservableProperty] private bool _showNavigateCta;

    public string? NavigateCtaText => ShowNavigateCta ? "去采集任务" : null;

    partial void OnShowNavigateCtaChanged(bool value) => OnPropertyChanged(nameof(NavigateCtaText));
    [ObservableProperty] private int _refreshIntervalSec = 2;
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private string? _taskScope;

    // 顶部汇总指标卡
    [ObservableProperty] private int _activeTaskCount;
    [ObservableProperty] private double _totalRate;
    [ObservableProperty] private long _totalErrors;
    [ObservableProperty] private int _totalRestarts;

    public ObservableCollection<DiagnosticsRowViewModel> Rows { get; } = new();

    public DiagnosticsViewModel(TaskOrchestrator orchestrator,
        Action<string>? navigate = null, bool showNavigateCta = false,
        IDbContextFactory<DcDbContext>? dbFactory = null)
    {
        _orchestrator = orchestrator;
        _navigate = navigate;
        _dbFactory = dbFactory;
        ShowNavigateCta = showNavigateCta;
        _timer = new DispatcherTimer(DispatcherPriority.Background,
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromSeconds(RefreshIntervalSec)
        };
        _timer.Tick += (_, _) => { if (AutoRefresh) Refresh(); };
        // 不在 ctor 启动：单例 VM 否则全程空转轮询。改由 view Loaded/Unloaded 调 Start/Stop。
    }

    private bool _running;

    /// <summary>页面可见时（view Loaded）：启动轮询 + 立即刷新。幂等。</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _timer.Start();
        Refresh();
        _ = LoadTaskNamesAsync(); // 加载任务可读名(列显示);加载后回填现有行
    }

    private string ResolveTaskName(string taskId) => _taskNames.GetValueOrDefault(taskId, taskId);

    private async Task LoadTaskNamesAsync()
    {
        if (_dbFactory is null) return;
        try
        {
            _taskNames = await TaskNames.LoadAsync(_dbFactory);
            foreach (var row in Rows) row.TaskName = ResolveTaskName(row.TaskId);
        }
        catch { /* 名字解析失败不影响诊断,列回退显示 id */ }
    }

    /// <summary>页面隐藏时（view Unloaded）：停止轮询。幂等。</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _timer.Stop();
    }

    /// <summary>Returns true when <paramref name="taskId"/> matches the active scope (null = all tasks).</summary>
    public static bool MatchesScope(string? scope, string taskId) =>
        scope is null || string.Equals(scope, taskId, StringComparison.Ordinal);

    [RelayCommand]
    private void NavigateToWorkspace() => _navigate?.Invoke("workspace");

    [RelayCommand]
    public void Refresh()
    {
        var diags = _orchestrator.GetDiagnostics()
            .Where(d => MatchesScope(TaskScope, d.TaskId));
        var seen = new HashSet<string>();
        foreach (var d in diags)
        {
            seen.Add(d.TaskId);
            if (!_rowIndex.TryGetValue(d.TaskId, out var row))
            {
                row = new DiagnosticsRowViewModel();
                _rowIndex[d.TaskId] = row;
                Rows.Add(row);
            }
            row.TickRecovery(); // 先递减上一帧「已恢复」倒计时（新建行 ticksLeft=0 时 no-op）
            row.Apply(d);       // 再 Apply：可能重新触发并重置 ticks，避免同帧 off-by-one 少一帧绿闪
            row.TaskName = ResolveTaskName(d.TaskId); // 可读任务名(列显示);未知 id 回退显示 id
        }
        // Remove rows for tasks no longer in scope or no longer running
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Rows[i].TaskId))
            {
                _rowIndex.Remove(Rows[i].TaskId);
                Rows.RemoveAt(i);
            }
        }

        // 汇总指标
        ActiveTaskCount = Rows.Count;
        TotalRate = Rows.Sum(r => r.ValuesPerSecond);
        TotalErrors = Rows.Sum(r => r.PublishErrorCount);
        TotalRestarts = Rows.Sum(r => r.RestartCount);
    }

    partial void OnTaskScopeChanged(string? value) => Refresh();

    partial void OnRefreshIntervalSecChanged(int value)
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, value));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
