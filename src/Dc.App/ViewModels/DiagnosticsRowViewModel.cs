using CommunityToolkit.Mvvm.ComponentModel;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.ViewModels;

public partial class DiagnosticsRowViewModel : ObservableObject
{
    [ObservableProperty] private string _taskId = string.Empty;
    [ObservableProperty] private DateTimeOffset _startedAt;
    [ObservableProperty] private DateTimeOffset? _lastValueAt;
    [ObservableProperty] private DateTimeOffset? _lastHeartbeatAt;
    [ObservableProperty] private long _valueCount;
    [ObservableProperty] private long _publishErrorCount;
    [ObservableProperty] private int _restartCount;
    [ObservableProperty] private int _subscribedTagCount;
    [ObservableProperty] private TimeSpan _uptime;
    [ObservableProperty] private double _valuesPerSecond;
    [ObservableProperty] private bool _hasErrors;            // 速率列着色用
    [ObservableProperty] private int _heartbeatSeverity;     // 0 正常 / 1 偏迟(>5s) / 2 失联(>2min)，心跳列着色用
    [ObservableProperty] private ConnectionState _state;     // 连接生命周期态，状态徽章列用
    [ObservableProperty] private bool _justRecovered;        // 重启/故障后刚恢复，绿闪瞬态

    private const int RecoverySeconds = 5;
    private int _recoveryTicksLeft;

    // 速率滚动窗口（最近 N 个 values/s 采样），供行内 sparkline 趋势图。
    [ObservableProperty] private IReadOnlyList<double> _rateHistory = Array.Empty<double>();
    private const int RateWindow = 40;
    private readonly Queue<double> _rates = new();

    private long _lastSnapshotValueCount;
    private DateTimeOffset _lastSnapshotAt = DateTimeOffset.UtcNow;

    public void Apply(TaskDiagnostics d)
    {
        // 已恢复检测：Restarting/Faulted → Running 即视为「刚恢复」，触发绿闪瞬态。
        // 注意：编排器在「进入 Restarting」时就 RestartCount++（见 TaskOrchestrator 重连路径），
        // 故恢复到 Running 这一跳 RestartCount 不再变化——不能用 RestartCount 增量做判据，
        // 状态转移本身才是恢复信号。prevState 取自上一帧快照。
        var prevState = State;
        State = d.State;
        if (prevState is ConnectionState.Restarting or ConnectionState.Faulted
            && d.State == ConnectionState.Running)
        {
            JustRecovered = true;
            _recoveryTicksLeft = RecoverySeconds;
        }

        TaskId = d.TaskId;
        StartedAt = d.StartedAt;
        LastValueAt = d.LastValueAt;
        LastHeartbeatAt = d.LastHeartbeatAt;
        var prevValueCount = ValueCount;
        ValueCount = d.ValueCount;
        PublishErrorCount = d.PublishErrorCount;
        RestartCount = d.RestartCount;
        SubscribedTagCount = d.SubscribedTagCount;
        Uptime = DateTimeOffset.UtcNow - d.StartedAt;

        HasErrors = d.PublishErrorCount > 0;
        var hbAge = d.LastHeartbeatAt is { } hb ? DateTimeOffset.UtcNow - hb : TimeSpan.Zero;
        HeartbeatSeverity = hbAge > TimeSpan.FromMinutes(2) ? 2 : hbAge > TimeSpan.FromSeconds(5) ? 1 : 0;

        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - _lastSnapshotAt).TotalSeconds;
        if (elapsed >= 1.0)
        {
            ValuesPerSecond = (d.ValueCount - _lastSnapshotValueCount) / elapsed;
            _lastSnapshotValueCount = d.ValueCount;
            _lastSnapshotAt = now;

            _rates.Enqueue(ValuesPerSecond);
            while (_rates.Count > RateWindow) _rates.Dequeue();
            RateHistory = _rates.ToArray(); // 重新赋值触发 sparkline 重算
        }
    }

    // 由诊断页刷新循环每轮调用；倒计时到点清除「已恢复」标。
    public void TickRecovery()
    {
        if (_recoveryTicksLeft > 0 && --_recoveryTicksLeft == 0)
            JustRecovered = false;
    }

    internal void RecoveryTickForTest()
    {
        _recoveryTicksLeft = 1;
        TickRecovery();
    }
}
