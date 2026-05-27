using Dc.Opc.Abstractions;
using Dc.Opc.Ae;
using Dc.Integration.Tests.Com.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Com;

[Collection("Com")]
public class AeSubscriberSmokeTests
{
    private readonly DemoServerFixture _demo = new();

    // AE-1: Tag.Item = "*" 走全收路径。Technosoftware demo server 启动会主动发若干
    //        condition refresh 事件，等 10s 内应至少收到 1 条。
    [WindowsComFact("SampleCompany.AeSample", Timeout = 30_000)]
    public async Task AE1_SubscribeWildcard_ReceivesEvent()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.AeProgId,
            SamplingInterval = TimeSpan.FromMilliseconds(500),
            HeartbeatInterval = TimeSpan.FromSeconds(10)
        };
        await using var sub = new OpcAeSubscriber("test-ae", options);
        await sub.ConnectAsync();

        var tag = new TagDescriptor(Id: "t-wild", Item: "*", DataType: 0);
        await sub.SubscribeAsync(new[] { tag });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        TagValue v = await sub.TagValues.ReadAsync(cts.Token);

        // AE 事件 Value 是 Dictionary<string,object?>
        Assert.IsAssignableFrom<IDictionary<string, object?>>(v.Value);
        var payload = (IDictionary<string, object?>)v.Value!;
        Assert.True(payload.ContainsKey("severity"));
        Assert.True(payload.ContainsKey("event_type"));
        Assert.Equal((ushort)0xC0, v.Quality);
    }
}
