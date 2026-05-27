using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Ua;

// 自管 server 生命周期（不用共享 fixture），但归入 "Ua" collection 串行执行，
// 避免与其他 UA 测试并行时争用客户端应用证书。
[Collection("Ua")]
public class UaReconnectTests
{
    // UA-2: 服务器崩溃后同端口重启 → 订阅器无需重建即自动重连，数据恢复流动。
    // 没有 KeepAlive→SessionReconnectHandler 的话，旧订阅器会一直死着，断言会超时失败。
    [Fact(Timeout = 90_000)]
    public async Task UA2_AutoReconnects_AfterServerRestart()
    {
        // 同一个 host 实例贯穿停-启：保留同端口 + 同一套 server 证书
        await using var host = new TestUaServerHost(TestUaServerHost.FindFreePort());
        await host.StartAsync();

        var options = new OpcConnectionOptions
        {
            ServerUri = host.EndpointUrl,
            SamplingInterval = TimeSpan.FromMilliseconds(200),
            HeartbeatInterval = TimeSpan.FromSeconds(2),
            KeepAliveInterval = TimeSpan.FromSeconds(1),   // 快速探测断线
            ReconnectPeriod = TimeSpan.FromSeconds(1)       // 快速重连重试
        };
        await using var sub = new OpcUaSubscriber("test-reconnect", options);
        await sub.ConnectAsync();
        await sub.SubscribeAsync(new[] { new TagDescriptor(Id: "t1", Item: "ns=2;s=Demo.Int32", DataType: 0) });

        // 连接正常：先收到一条值
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var first = await sub.TagValues.ReadAsync(cts.Token);
            Assert.Equal("ns=2;s=Demo.Int32", first.Item);
        }

        // 模拟服务器崩溃
        host.Stop();
        // 等 KeepAlive 探到 bad、重连开始（KeepAliveInterval=1s）；同时给端口释放留出时间
        await Task.Delay(TimeSpan.FromSeconds(3));

        // 排空崩溃前残留，确保下面读到的是「重连后」的新值
        while (sub.TagValues.TryRead(out _)) { }

        // 同端口重启
        await host.StartAsync();

        // 断言：未重建 subscriber，数据自动恢复
        using var resumed = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var v = await sub.TagValues.ReadAsync(resumed.Token);
        Assert.Equal("ns=2;s=Demo.Int32", v.Item);
        Assert.NotNull(v.Value);
    }
}
