using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Dc.Integration.Tests.Ua;

[Collection("Timing-Sensitive")]
public class UaHeartbeatLivenessTests
{
    private readonly ITestOutputHelper _out;
    public UaHeartbeatLivenessTests(ITestOutputHelper o) => _out = o;

    [Fact(Timeout = 60_000)]
    public async Task Heartbeat_StopsWriting_WhenServerDown()
    {
        var port = TestUaServerHost.FindFreePort();
        var host = new TestUaServerHost(port);
        await host.StartAsync();

        await using var sub = new OpcUaSubscriber("hb-liveness", new OpcConnectionOptions
        {
            ServerUri = host.EndpointUrl,
            UseSecurity = false,
            SamplingInterval = TimeSpan.FromMilliseconds(200),
            HeartbeatInterval = TimeSpan.FromMilliseconds(500),
            KeepAliveInterval = TimeSpan.FromMilliseconds(500),
            ReconnectPeriod = TimeSpan.FromMilliseconds(500),
        });
        await sub.ConnectAsync();
        await sub.SubscribeAsync(new[] { new TagDescriptor("d", "ns=2;s=Demo.Int32", 0) });

        // 连接正常:确认有心跳
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await sub.Heartbeats.ReadAsync(cts.Token);

        // 停 server,等 KeepAliveStopped 生效 + 在途心跳清空(给 3s)
        host.Stop();
        await Task.Delay(TimeSpan.FromSeconds(3));
        while (sub.Heartbeats.TryRead(out _)) { }   // 排空 settle 期残留

        // 之后窗口内不应再有心跳(server 真停,KeepAliveStopped=true → 心跳停写)
        var after = 0;
        var window = TimeSpan.FromSeconds(3);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < window)
        {
            if (sub.Heartbeats.TryRead(out _)) after++;
            await Task.Delay(100);
        }
        _out.WriteLine($"server 停后窗口内心跳数={after}(期望 0)");
        Assert.Equal(0, after);

        await host.DisposeAsync();
    }
}
