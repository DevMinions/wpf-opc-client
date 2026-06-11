using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

/// <summary>
/// 调试用：按 tags 个 key × hz 频率合成 TagValue，定速灌进编排器 TagValueReceived 路径，
/// 绕过真 OPC，仅压 VM→UI 渲染段。门控后才构造/调用，绝不进真采集路径。
/// </summary>
public sealed class SyntheticLoadGenerator
{
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
                // 每 ~50 个掺 1 个非 Good（Bad 0x00 / Uncertain 0x40 交替）验证着色
                ushort quality = (seq % 50 == 0) ? (ushort)0x00 : (seq % 97 == 0) ? (ushort)0x40 : (ushort)0xC0;
                _inject(taskId, new TagValue($"Stress::tag{i}", seq, quality, DateTimeOffset.UtcNow));
                injected++;
            }
        }

        return injected;
    }
}
