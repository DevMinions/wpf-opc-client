namespace Dc.Infrastructure.Orchestration;

public sealed record OrchestratorOptions
{
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromMinutes(2);
    // 停止/重启时，等待 pipeline 把通道残余值发完的上限；超时则强制取消兜底。
    public TimeSpan StopDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>连续看门狗重启仍未恢复心跳的次数阈值，达到则标记 Faulted。</summary>
    public int FaultThreshold { get; init; } = 3;
}
