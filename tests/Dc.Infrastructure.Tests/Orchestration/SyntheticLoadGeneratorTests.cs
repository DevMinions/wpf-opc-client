using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

[Collection("Timing-Sensitive")]
public class SyntheticLoadGeneratorTests
{
    [Fact]
    public async Task RunAsync_InjectsApproxTagsTimesHzTimesSeconds_DistinctKeys()
    {
        var received = new List<(string TaskId, TagValue V)>();
        var gen = new SyntheticLoadGenerator((taskId, v) => { lock (received) received.Add((taskId, v)); });

        var injected = await gen.RunAsync("stress", tags: 100, hz: 10, seconds: 1, CancellationToken.None);

        // 100 tags × 10 hz × 1 s = 1000，允许 ±1 个周期容差
        Assert.InRange(injected, 800, 1100);
        Assert.InRange(received.Count, 800, 1100);
        var distinct = received.Select(r => r.V.Item).Distinct().Count();
        Assert.Equal(100, distinct);
        Assert.All(received, r => Assert.StartsWith("Stress::tag", r.V.Item));
        // 含少量非 Good 质量（验证着色路径）
        Assert.Contains(received, r => !r.V.IsGood);
    }

    [Fact]
    public async Task RunAsync_Cancelled_StopsEarly()
    {
        var count = 0;
        var gen = new SyntheticLoadGenerator((_, _) => Interlocked.Increment(ref count));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var injected = await gen.RunAsync("stress", tags: 50, hz: 10, seconds: 30, cts.Token);
        Assert.True(injected < 50 * 10 * 30, "取消后应提前停止");
    }
}
