namespace Dc.Infrastructure.Orchestration;

public sealed record TaskDiagnostics(
    string TaskId,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastValueAt,
    DateTimeOffset? LastHeartbeatAt,
    long ValueCount,
    long PublishErrorCount,
    int RestartCount,
    int SubscribedTagCount,
    long QueuePendingBytes = 0,
    long DroppedFrameCount = 0,
    ConnectionState State = ConnectionState.Running,
    long PublishedCount = 0);
