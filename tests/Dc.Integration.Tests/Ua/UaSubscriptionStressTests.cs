using System.Diagnostics;
using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class UaSubscriptionStressTests
{
    private readonly ITestOutputHelper _out;
    public UaSubscriptionStressTests(ITestOutputHelper o) => _out = o;

    private static TagDescriptor[] StressTags(int n)
    {
        var tags = new TagDescriptor[n];
        for (var i = 0; i < n; i++) tags[i] = new TagDescriptor($"s{i}", $"ns=2;s=Stress.{i}", 0);
        return tags;
    }

    // 从 Item（如 "ns=2;s=Stress.7"）解析节点索引 7，用于路由身份断言 value%N==index
    private static int NodeIndex(string item) => int.Parse(item[(item.LastIndexOf('.') + 1)..]);

    [Fact(Timeout = 60_000)]
    public async Task Throughput_ManyNodes_DeliversNearNOverP()
    {
        const int N = 500;
        var P = TimeSpan.FromMilliseconds(100);
        var duration = TimeSpan.FromSeconds(10);

        await using var host = new TestUaServerHost(TestUaServerHost.FindFreePort(),
            stressNodes: N, stressTick: TimeSpan.FromMilliseconds(50));
        await host.StartAsync();

        await using var sub = new OpcUaSubscriber("stress-tp", new OpcConnectionOptions
        {
            ServerUri = host.EndpointUrl, SamplingInterval = P, HeartbeatInterval = TimeSpan.FromSeconds(5),
        });
        await sub.ConnectAsync();
        await sub.SubscribeAsync(StressTags(N));

        // 预热：等首批通知到达后再计时（排除订阅建立冷启动）
        using (var warm = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            await sub.TagValues.ReadAsync(warm.Token);
        while (sub.TagValues.TryRead(out _)) { }

        long received = 0;
        var sw = Stopwatch.StartNew();
        using (var cts = new CancellationTokenSource(duration))
        {
            try { while (true) { await sub.TagValues.ReadAsync(cts.Token); received++; } }
            catch (OperationCanceledException) { }
        }
        sw.Stop();

        var thru = received / sw.Elapsed.TotalSeconds;
        _out.WriteLine($"N={N} P={P.TotalMilliseconds}ms received={received} throughput={thru:F0}/s (理论 N/P={N/P.TotalSeconds:F0}/s) serverTicks={host.StressTickCount}");
        var serverProduced = (long)N * host.StressTickCount;   // server 端总变化数
        var coalesceRatio = received > 0 ? (double)serverProduced / received : 0;
        _out.WriteLine($"合并比(server产出/交付)={coalesceRatio:F2} (QueueSize=1,tick {50}ms vs publish {P.TotalMilliseconds}ms)");
        Assert.True(thru >= 2500, $"交付吞吐应 ≥2500/s，实测 {thru:F0}/s");
    }

    [Fact(Timeout = 60_000)]
    public async Task Correctness_PerNode_MonotonicAndFinalMatchesServer()
    {
        const int N = 100;
        var P = TimeSpan.FromMilliseconds(50);
        var duration = TimeSpan.FromSeconds(5);

        await using var host = new TestUaServerHost(TestUaServerHost.FindFreePort(),
            stressNodes: N, stressTick: TimeSpan.FromMilliseconds(50));
        await host.StartAsync();

        await using var sub = new OpcUaSubscriber("stress-correct", new OpcConnectionOptions
        {
            ServerUri = host.EndpointUrl, SamplingInterval = P, HeartbeatInterval = TimeSpan.FromSeconds(5),
        });
        await sub.ConnectAsync();
        await sub.SubscribeAsync(StressTags(N));

        var lastByNode = new Dictionary<string, int>();
        var monotonicViolations = 0;
        long total = 0;
        using (var cts = new CancellationTokenSource(duration))
        {
            try
            {
                while (true)
                {
                    var v = await sub.TagValues.ReadAsync(cts.Token);
                    total++;
                    var cur = Convert.ToInt32(v.Value);
                    Assert.True(cur % N == NodeIndex(v.Item),
                        $"路由串扰:{v.Item} 收到值 {cur}，但 {cur}%{N}={cur % N} ≠ 索引 {NodeIndex(v.Item)}");
                    if (lastByNode.TryGetValue(v.Item, out var prev) && cur < prev) monotonicViolations++;
                    lastByNode[v.Item] = cur;
                }
            }
            catch (OperationCanceledException) { }
        }

        // settle：再等 3×P 排空残余，取每节点最后值与 server 当前计数比
        await Task.Delay(TimeSpan.FromMilliseconds(P.TotalMilliseconds * 3));
        while (sub.TagValues.TryRead(out var v))
        {
            total++;
            var cur = Convert.ToInt32(v.Value);
            Assert.True(cur % N == NodeIndex(v.Item),
                $"路由串扰:{v.Item} 收到值 {cur}，但 {cur}%{N}={cur % N} ≠ 索引 {NodeIndex(v.Item)}");
            lastByNode[v.Item] = cur;
        }
        var serverNow = host.StressTickCount;

        _out.WriteLine($"N={N} total={total} 单调违例={monotonicViolations} server当前={serverNow}");
        Assert.Equal(0, monotonicViolations);
        Assert.Equal(N, lastByNode.Count);
        // 每节点最终 tick(value/N) 应接近 server 当前 tick;容差 ≤5 补偿「排空后读 serverNow,期间 server 又 tick 几拍」的时序差(非丢值)
        foreach (var kv in lastByNode)
            Assert.True(serverNow - kv.Value / N <= 5,
                $"{kv.Key} 最终 tick {kv.Value / N} 落后 server {serverNow} 超过 5 拍");
    }
}
