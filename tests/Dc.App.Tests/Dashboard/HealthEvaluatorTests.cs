using System.Globalization;
using Dc.App.Dashboard;
using Dc.App.Services.I18n;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.Tests.Dashboard;

[Collection("I18nCulture")]
public class HealthEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(120);

    // 告警文案本地化后按 culture 取值;断言中文子串须锁定中文。
    public HealthEvaluatorTests() => LocalizationManager.Instance.SetCulture(new CultureInfo("zh-CN"));

    private static TaskDiagnostics Diag(
        string id,
        DateTimeOffset? lastHeartbeat = null,
        long valueCount = 0,
        long errors = 0,
        int restarts = 0,
        int tags = 0,
        DateTimeOffset? startedAt = null,
        long published = 0) =>
        new(id, startedAt ?? Now.AddMinutes(-10), Now.AddSeconds(-2),
            lastHeartbeat ?? Now.AddSeconds(-1), valueCount, errors, restarts, tags,
            QueuePendingBytes: 0, DroppedFrameCount: 0, State: ConnectionState.Running,
            PublishedCount: published);

    [Fact]
    public void Empty_NoTasks_HealthIs100_ZeroAlerts()
    {
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: Array.Empty<TaskDiagnostics>(),
            runningTaskIds: Array.Empty<string>(),
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(100, snap.HealthScore);
        Assert.Empty(snap.Alerts);
        Assert.Equal(0, snap.RunningTasks);
    }

    [Fact]
    public void StoppedTask_GeneratesCriticalAlert_Score_Minus15()
    {
        var diag = Diag("t1");
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: new[] { diag },
            runningTaskIds: Array.Empty<string>(),
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(85, snap.HealthScore);
        var alert = Assert.Single(snap.Alerts);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("已停止", alert.Message);
    }

    [Fact]
    public void StaleHeartbeat_GeneratesWarning_Score_Minus5()
    {
        var diag = Diag("t1", lastHeartbeat: Now.AddSeconds(-150));
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: new[] { diag },
            runningTaskIds: new[] { "t1" },
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(95, snap.HealthScore);
        var alert = Assert.Single(snap.Alerts);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("心跳", alert.Message);
    }

    [Fact]
    public void ErrorRatePresent_GeneratesWarning_Score_Minus3()
    {
        // 曾成功发过(published>0)、现出错 → 真实发送错误
        var diag = Diag("t1", errors: 5, published: 100);
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: new[] { diag },
            runningTaskIds: new[] { "t1" },
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(97, snap.HealthScore);
        var alert = Assert.Single(snap.Alerts);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("错误", alert.Message);
    }

    [Fact]
    public void ErrorsButNeverPublished_FlaggedAsNoConsumer()
    {
        // 从未成功发过任何帧 + 发送全失败 → 几乎必是无下游消费者连不上(单机自测常态)
        var diag = Diag("t1", errors: 5, published: 0);
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: new[] { diag },
            runningTaskIds: new[] { "t1" },
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(97, snap.HealthScore);
        var alert = Assert.Single(snap.Alerts);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("无下游消费者", alert.Message);
    }

    [Fact]
    public void HealthScore_FloorIs0_NotNegative()
    {
        var stopped = Enumerable.Range(1, 7).Select(i => Diag($"t{i}")).ToArray();
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: stopped,
            runningTaskIds: Array.Empty<string>(),
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(0, snap.HealthScore);
    }

    [Fact]
    public void Rate_UsesPreviousValueCountsForDelta()
    {
        var diag = Diag("t1", valueCount: 180);
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            previousValueCounts: new Dictionary<string, (long Count, DateTimeOffset At)>
            {
                ["t1"] = (100, Now.AddSeconds(-1))
            },
            diagnostics: new[] { diag },
            runningTaskIds: new[] { "t1" },
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(80.0, snap.MessagesPerSecond, precision: 1);
        var row = Assert.Single(snap.Tasks);
        Assert.Equal(80.0, row.RatePerSecond, precision: 1);
    }

    [Fact]
    public void Alerts_LimitedTo5_AndSortedBySeverityThenTime()
    {
        var t = Now.AddSeconds(-30);
        var diagnostics = new[]
        {
            Diag("warn1", errors: 5, startedAt: t),
            Diag("warn2", lastHeartbeat: Now.AddSeconds(-200), startedAt: t),
            Diag("stop1", startedAt: t),
            Diag("stop2", startedAt: t),
            Diag("warn3", errors: 3, startedAt: t),
            Diag("warn4", errors: 7, startedAt: t)
        };
        var running = new[] { "warn1", "warn2", "warn3", "warn4" };

        var snap = HealthEvaluator.Evaluate(null, diagnostics, running, Now, HeartbeatTimeout);

        Assert.Equal(5, snap.Alerts.Count);
        Assert.Equal(AlertSeverity.Critical, snap.Alerts[0].Severity);
        Assert.Equal(AlertSeverity.Critical, snap.Alerts[1].Severity);
        Assert.All(snap.Alerts.Skip(2), a => Assert.Equal(AlertSeverity.Warning, a.Severity));
    }
}
