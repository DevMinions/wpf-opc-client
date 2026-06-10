using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.ViewModels.Workspace;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.ViewModels;

public partial class DiagnosticsViewModel : ObservableObject, IDisposable, IEmbeddableDiagPanel
{
    private readonly TaskOrchestrator _orchestrator;
    private readonly Action<string>? _navigate;
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
        Action<string>? navigate = null, bool showNavigateCta = false)
    {
        _orchestrator = orchestrator;
        _navigate = navigate;
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
            row.Apply(d);
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
