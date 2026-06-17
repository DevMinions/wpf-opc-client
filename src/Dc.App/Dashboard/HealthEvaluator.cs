using Dc.Infrastructure.Orchestration;

namespace Dc.App.Dashboard;

public static class HealthEvaluator
{
    private const int ScoreStoppedPenalty = 15;
    private const int ScoreStalePenalty = 5;
    private const int ScoreErrorPenalty = 3;
    private const int MaxAlertsShown = 5;

    public static HealthSnapshot Evaluate(
        HealthSnapshot? previous,
        IReadOnlyList<TaskDiagnostics> diagnostics,
        IReadOnlyCollection<string> runningTaskIds,
        DateTimeOffset now,
        TimeSpan heartbeatTimeout)
        => Evaluate(previous, previousValueCounts: null, diagnostics, runningTaskIds, now, heartbeatTimeout);

    public static HealthSnapshot Evaluate(
        HealthSnapshot? previous,
        IReadOnlyDictionary<string, (long Count, DateTimeOffset At)>? previousValueCounts,
        IReadOnlyList<TaskDiagnostics> diagnostics,
        IReadOnlyCollection<string> runningTaskIds,
        DateTimeOffset now,
        TimeSpan heartbeatTimeout)
    {
        var running = new HashSet<string>(runningTaskIds, StringComparer.Ordinal);
        int score = 100;
        var alerts = new List<AlertItem>();
        var rows = new List<TaskRowSummary>(diagnostics.Count);
        double totalRate = 0;
        int activeTags = 0;
        long totalErrors = 0;
        DateTimeOffset? earliestStart = null;

        foreach (var d in diagnostics)
        {
            bool isRunning = running.Contains(d.TaskId);
            AlertSeverity? severity = null;
            string? message = null;

            if (!isRunning)
            {
                severity = AlertSeverity.Critical;
                message = "已停止";
                score -= ScoreStoppedPenalty;
            }
            else if (d.LastHeartbeatAt is { } hb && (now - hb) > heartbeatTimeout)
            {
                severity = AlertSeverity.Warning;
                var late = (now - hb).TotalSeconds;
                message = $"心跳延迟 {late:F0}s";
                score -= ScoreStalePenalty;
            }
            else if (d.PublishErrorCount > 0)
            {
                severity = AlertSeverity.Warning;
                // 区分「从未成功发过」vs「曾成功、现中断」:前者几乎必是无下游消费者连不上
                // (单机自测不起消费端即此态),后者才是真实发送故障。避免把「无消费者」误读成采集器坏。
                message = d.PublishedCount == 0
                    ? $"无下游消费者连接（发送全部失败 {d.PublishErrorCount}）"
                    : $"发送错误 {d.PublishErrorCount}";
                score -= ScoreErrorPenalty;
            }

            if (severity is not null)
            {
                alerts.Add(new AlertItem(
                    severity.Value, d.TaskId, d.TaskId, message!, d.LastHeartbeatAt ?? d.StartedAt));
            }

            double rate = 0;
            if (isRunning && previousValueCounts is not null
                && previousValueCounts.TryGetValue(d.TaskId, out var prev))
            {
                var elapsed = (now - prev.At).TotalSeconds;
                if (elapsed > 0.001)
                {
                    rate = (d.ValueCount - prev.Count) / elapsed;
                    if (rate < 0) rate = 0;
                }
            }
            totalRate += rate;

            activeTags += d.SubscribedTagCount;
            totalErrors += d.PublishErrorCount;
            if (earliestStart is null || d.StartedAt < earliestStart) earliestStart = d.StartedAt;

            rows.Add(new TaskRowSummary(d.TaskId, isRunning, severity, rate));
        }

        score = Math.Max(0, Math.Min(100, score));

        var sortedAlerts = alerts
            .OrderBy(a => a.Severity)
            .ThenByDescending(a => a.OccurredAt)
            .Take(MaxAlertsShown)
            .ToList();

        TimeSpan? uptime = earliestStart is null ? null : now - earliestStart;

        return new HealthSnapshot(
            HealthScore: score,
            RunningTasks: running.Count,
            TotalTasks: diagnostics.Count,
            ActiveTags: activeTags,
            MessagesPerSecond: totalRate,
            ErrorsTotal: totalErrors,
            QueueBackedFrames: 0,
            Uptime: uptime,
            Alerts: sortedAlerts,
            Tasks: rows);
    }
}
