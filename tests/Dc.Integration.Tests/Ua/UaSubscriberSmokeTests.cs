using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class UaSubscriberSmokeTests
{
    private readonly EmbeddedUaServerFixture _ua;
    public UaSubscriberSmokeTests(EmbeddedUaServerFixture ua) => _ua = ua;

    // UA-1: 连内嵌 server → 订阅 ns=2;s=Demo.Int32（MinimalUaNodeManager 暴露的）→ 收到 ≥ 1 条 TagValue
    [Fact(Timeout = 20_000)]
    public async Task UA1_SubscribeStaticInt_ReceivesValue()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _ua.AnonymousEndpointUrl,
            SamplingInterval = TimeSpan.FromMilliseconds(200),
            HeartbeatInterval = TimeSpan.FromSeconds(5)
        };
        await using var sub = new OpcUaSubscriber("test-ua", options);

        await sub.ConnectAsync();

        // ns=2 是 MinimalUaNodeManager 的 namespace index（系统默认 ns=0 + 测试 server 的命名空间 ns=2）
        // NodeId 字符串使用 vendor 风格 "ns=2;s=Demo.Int32"
        var tag = new TagDescriptor(Id: "test-tag-1", Item: "ns=2;s=Demo.Int32", DataType: 0);
        await sub.SubscribeAsync(new[] { tag });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        TagValue v = await sub.TagValues.ReadAsync(cts.Token);

        Assert.Equal("ns=2;s=Demo.Int32", v.Item);
        Assert.NotNull(v.Value);
    }
}
