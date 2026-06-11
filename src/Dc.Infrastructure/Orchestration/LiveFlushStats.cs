namespace Dc.Infrastructure.Orchestration;

/// <summary>LiveData flush 指标快照（App VM 填充，/metrics 渲染消费）。</summary>
public sealed record LiveFlushStats(
    double P50Ms,
    double P95Ms,
    double CoalesceRatio,
    int Rows,
    double UpdatesPerSecond);
