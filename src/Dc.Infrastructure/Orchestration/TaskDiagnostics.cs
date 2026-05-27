namespace Dc.Infrastructure.Orchestration;

public sealed record TaskDiagnostics(
    string TaskId,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastValueAt,
    DateTimeOffset? LastHeartbeatAt,
    long ValueCount,
    long PublishErrorCount,
    int RestartCount,
    int SubscribedTagCount);
