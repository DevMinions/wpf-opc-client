namespace Dc.App.Dashboard;

public sealed record HealthSnapshot(
    int HealthScore,
    int RunningTasks,
    int TotalTasks,
    int ActiveTags,
    double MessagesPerSecond,
    long ErrorsTotal,
    long QueueBackedFrames,
    TimeSpan? Uptime,
    IReadOnlyList<AlertItem> Alerts,
    IReadOnlyList<TaskRowSummary> Tasks);

public sealed record TaskRowSummary(
    string TaskId,
    bool IsRunning,
    AlertSeverity? Severity,
    double RatePerSecond);
