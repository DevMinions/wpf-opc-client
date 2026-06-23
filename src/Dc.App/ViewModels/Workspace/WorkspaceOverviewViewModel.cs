using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dc.App.ViewModels.Dashboard;

namespace Dc.App.ViewModels.Workspace;

public sealed partial class WorkspaceOverviewViewModel : ObservableObject
{
    private const int MaxPoints = 60;

    private readonly IDashboardOrchestratorView _orch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dc.App.Services.I18n.ILocalizer _loc;

    private string? _taskId;
    private long? _lastValueCount;
    private DateTimeOffset _lastSampleAt;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private long _totalMessages;
    [ObservableProperty] private long _errorCount;
    [ObservableProperty] private int _restartCount;
    [ObservableProperty] private int _subscribedTags;
    [ObservableProperty] private string _uptimeDisplay = "—";
    [ObservableProperty] private string _lastHeartbeatDisplay = "—";

    public ObservableCollection<double> SparklineRates { get; } = new();

    public WorkspaceOverviewViewModel(IDashboardOrchestratorView orchestratorView, Func<DateTimeOffset> clock,
        Dc.App.Services.I18n.ILocalizer? localizer = null)
    {
        _orch = orchestratorView;
        _clock = clock;
        _loc = localizer ?? new Dc.App.Services.I18n.ResourceLocalizer();
    }

    public void SetTask(string? taskId)
    {
        _taskId = taskId;
        _lastValueCount = null;
        SparklineRates.Clear();
    }

    public void Sample()
    {
        if (_taskId is null) return;
        var now = _clock();
        var diag = _orch.GetDiagnostics().FirstOrDefault(d => d.TaskId == _taskId);
        if (diag is null)
        {
            IsRunning = false;
            TotalMessages = 0;
            ErrorCount = 0;
            RestartCount = 0;
            SubscribedTags = 0;
            UptimeDisplay = "—";
            LastHeartbeatDisplay = "—";
            return;
        }

        IsRunning = _orch.RunningTaskIds.Contains(_taskId);
        TotalMessages = diag.ValueCount;
        ErrorCount = diag.PublishErrorCount;
        RestartCount = diag.RestartCount;
        SubscribedTags = diag.SubscribedTagCount;
        UptimeDisplay = FormatUptime(now - diag.StartedAt);
        LastHeartbeatDisplay = diag.LastHeartbeatAt is { } hb
            ? _loc.Format("Overview_SecondsAgo", $"{(now - hb).TotalSeconds:F0}")
            : "—";

        if (_lastValueCount is { } prev)
        {
            var elapsed = (now - _lastSampleAt).TotalSeconds;
            if (elapsed > 0.001)
            {
                var rate = (diag.ValueCount - prev) / elapsed;
                if (rate < 0) rate = 0;
                SparklineRates.Add(rate);
                while (SparklineRates.Count > MaxPoints) SparklineRates.RemoveAt(0);
            }
        }
        _lastValueCount = diag.ValueCount;
        _lastSampleAt = now;
    }

    private static string FormatUptime(TimeSpan s)
    {
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d {s.Hours}h";
        if (s.TotalHours >= 1) return $"{s.Hours}h {s.Minutes}m";
        return $"{s.Minutes}m {s.Seconds}s";
    }
}
