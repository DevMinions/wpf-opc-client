using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class StressNodesSmokeTests
{
    [Fact(Timeout = 30_000)]
    public async Task StressNodes_TickAndDeliverChangingValues_AndTickCountIncreases()
    {
        await using var host = new TestUaServerHost(TestUaServerHost.FindFreePort(),
            stressNodes: 10, stressTick: TimeSpan.FromMilliseconds(50));
        await host.StartAsync();

        await using var sub = new OpcUaSubscriber("stress-smoke", new OpcConnectionOptions
        {
            ServerUri = host.EndpointUrl,
            SamplingInterval = TimeSpan.FromMilliseconds(100),
            HeartbeatInterval = TimeSpan.FromSeconds(5),
        });
        await sub.ConnectAsync();
        await sub.SubscribeAsync(new[] { new TagDescriptor("s0", "ns=2;s=Stress.0", 0) });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var v1 = await sub.TagValues.ReadAsync(cts.Token);
        object? first = v1.Value;
        object? second = first;
        while (Equals(second, first))
            second = (await sub.TagValues.ReadAsync(cts.Token)).Value;
        Assert.Equal("ns=2;s=Stress.0", v1.Item);
        Assert.True(host.StressTickCount > 0, "server 端 tick 计数应增长");
    }
}
