using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

/// <summary>
/// 调试用：按 tags 个 key × hz 频率合成 TagValue，定速灌进编排器 TagValueReceived 路径，
/// 绕过真 OPC，仅压 VM→UI 渲染段。门控后才构造/调用，绝不进真采集路径。
/// </summary>
public sealed class SyntheticLoadGenerator
{
    private const int BadQualityEvery = 50;       // 每 50 个掺 1 个 Bad
    private const int UncertainQualityEvery = 97; // 每 97 个掺 1 个 Uncertain（与 Bad 独立取模）

    private readonly Action<string, TagValue> _inject;

    public SyntheticLoadGenerator(Action<string, TagValue> inject) => _inject = inject;

    /// <summary>持续 seconds 秒，每 1/hz 秒为 tags 个 key 各发一个递增值。返回实际注入条数。</summary>
    public async Task<long> RunAsync(string taskId, int tags, int hz, int seconds, CancellationToken ct)
    {
        if (tags <= 0 || hz <= 0 || seconds <= 0)
        {
            return 0;
        }

        hz = Math.Min(hz, 1000);
        seconds = Math.Min(seconds, 300); // 防失控
        var period = TimeSpan.FromSeconds(1.0 / hz);
        var totalTicks = hz * seconds;
        long injected = 0;
        long seq = 0;
        using var timer = new PeriodicTimer(period);
        for (var tick = 0; tick < totalTicks; tick++)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            for (var i = 0; i < tags; i++)
            {
                seq++;
                // 注入少量非 Good 质量验证下游着色：每 BadQualityEvery 个 Bad、每 UncertainQualityEvery 个 Uncertain（独立取模，Bad 优先）
                ushort quality = (seq % BadQualityEvery == 0) ? (ushort)0x00
                               : (seq % UncertainQualityEvery == 0) ? (ushort)0x40
                               : (ushort)0xC0;
                _inject(taskId, new TagValue($"Stress::tag{i}", seq, quality, DateTimeOffset.UtcNow));
                injected++;
            }
        }

        return injected;
    }
}
