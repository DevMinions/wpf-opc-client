using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dc.App.Dashboard;

namespace Dc.App.ViewModels.Dashboard;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IDashboardOrchestratorView _orch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _heartbeatTimeout;

    private HealthSnapshot? _previousSnapshot;
    private Dictionary<string, (long Count, DateTimeOffset At)> _previousValueCounts = new();

    private const int MaxSparklinePoints = 60;

    [ObservableProperty] private int _healthScore = 100;
    [ObservableProperty] private string _runningTasksDisplay = "0";
    [ObservableProperty] private string _totalTasksDisplay = "0";
    [ObservableProperty] private int _activeTags;
    [ObservableProperty] private double _messagesPerSecond;
    [ObservableProperty] private long _errorsTotal;
    [ObservableProperty] private long _queueBackedFrames;
    [ObservableProperty] private string _uptimeDisplay = "—";
    [ObservableProperty] private string _messagesPerSecondDisplay = "0";
    [ObservableProperty] private string _errorsTotalDisplay = "0";

    public ObservableCollection<AlertItem> Alerts { get; } = new();
    public ObservableCollection<TaskRowSummary> Tasks { get; } = new();

    /// <summary>Sparkline 速率历史（最近 60 个点）</summary>
    public ObservableCollection<double> SparklineRates { get; } = new();

    public DashboardViewModel(
        IDashboardOrchestratorView orchestratorView,
        Func<DateTimeOffset> clock,
        TimeSpan heartbeatTimeout)
    {
        _orch = orchestratorView;
        _clock = clock;
        _heartbeatTimeout = heartbeatTimeout;
    }

    public void Refresh()
    {
        var now = _clock();
        var diagnostics = _orch.GetDiagnostics();
        var running = _orch.RunningTaskIds;

        var snap = HealthEvaluator.Evaluate(
            _previousSnapshot,
            _previousValueCounts.Count == 0 ? null : _previousValueCounts,
            diagnostics,
            running,
            now,
            _heartbeatTimeout);

        HealthScore = snap.HealthScore;
        RunningTasksDisplay = snap.RunningTasks.ToString();
        TotalTasksDisplay = snap.TotalTasks.ToString();
        ActiveTags = snap.ActiveTags;
        MessagesPerSecond = snap.MessagesPerSecond;
        MessagesPerSecondDisplay = FormatRate(snap.MessagesPerSecond);

        // Sparkline history
        SparklineRates.Add(snap.MessagesPerSecond);
        while (SparklineRates.Count > MaxSparklinePoints) SparklineRates.RemoveAt(0);
        ErrorsTotal = snap.ErrorsTotal;
        ErrorsTotalDisplay = snap.ErrorsTotal.ToString();
        QueueBackedFrames = snap.QueueBackedFrames;
        UptimeDisplay = FormatUptime(snap.Uptime);

        Alerts.Clear();
        foreach (var a in snap.Alerts) Alerts.Add(a);

        Tasks.Clear();
        foreach (var t in snap.Tasks) Tasks.Add(t);

        _previousSnapshot = snap;
        _previousValueCounts = diagnostics.ToDictionary(
            d => d.TaskId,
            d => (d.ValueCount, now),
            StringComparer.Ordinal);
    }

    private static string FormatRate(double rate)
    {
        if (rate >= 1000) return $"{rate / 1000:F1}k";
        return rate.ToString("F0");
    }

    private static string FormatUptime(TimeSpan? span)
    {
        if (span is null) return "—";
        var s = span.Value;
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d {s.Hours}h";
        if (s.TotalHours >= 1) return $"{s.Hours}h {s.Minutes}m";
        return $"{s.Minutes}m {s.Seconds}s";
    }

    private System.Windows.Threading.DispatcherTimer? _timer;

    public void Start(System.Windows.Threading.Dispatcher dispatcher)
    {
        if (_timer is not null) return;
        _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => Refresh();
        Refresh();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}
