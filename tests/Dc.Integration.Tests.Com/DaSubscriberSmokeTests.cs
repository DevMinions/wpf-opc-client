using Dc.Opc.Abstractions;
using Dc.Opc.Da;
using Dc.Integration.Tests.Com.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Com;

[Collection("Com")]
public class DaSubscriberSmokeTests
{
    private readonly DemoServerFixture _demo = new();

    // DA-1: 连 demo server SampleCompany.DaSample，订阅 "SimulatedData.Ramp"
    //        （Technosoftware demo server 内置持续变化的稳定项），
    //        收到至少 1 条 TagValue，quality bit 段为 Good (0xC0)。
    [WindowsComFact("SampleCompany.DaSample", Timeout = 30_000)]
    public async Task DA1_SubscribeSimulatedData_ReceivesValue()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.DaProgId,
            SamplingInterval = TimeSpan.FromMilliseconds(500),
            HeartbeatInterval = TimeSpan.FromSeconds(10)
        };
        await using var sub = new OpcDaSubscriber("test-da", options);
        await sub.ConnectAsync();

        var tag = new TagDescriptor(Id: "t1", Item: "SimulatedData.Ramp", DataType: 0);
        await sub.SubscribeAsync(new[] { tag });

        // DA 订阅瞬间会先回调一条缓存初值(quality 0x00 Bad),下个采样周期才是真实 Good 值。
        // 循环读到首个 Good(超时即失败),准确表达「订阅并收到有效值」的意图。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        TagValue v;
        do { v = await sub.TagValues.ReadAsync(cts.Token); }
        while (!v.IsGood);

        Assert.Equal("SimulatedData.Ramp", v.Item);
        Assert.True(v.IsGood, $"期望 quality 0xC0 Good，实际 0x{v.Quality:X2}");
    }
}

// 同进程内多个 Com 测试 class 共享 [Collection("Com")] → xunit 串行执行
// （DA COM 的 STA 公寓敏感，并发激活同一 server 时常 0x80010108 RPC_E_DISCONNECTED）
[CollectionDefinition("Com")]
public class ComCollection { }
