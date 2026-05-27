using System.Buffers.Binary;
using Dc.Infrastructure.Messaging;
using Dc.Opc.Abstractions;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace Dc.Integration.Tests.Infrastructure;

public class TcpPublisherEndToEndTests
{
    // INF-1: TcpPublisher 用 msgpack 发一条 TagValue，listener 收到 [4B BE length][payload]，
    //         反序列化字段与原始一致。
    [Fact(Timeout = 10_000)]
    public async Task INF1_MsgpackFrame_RoundTrip()
    {
        await using var lis = await TcpListenerFixture.StartAsync();
        var serializer = new MessagePackMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(lis.Address, serializer);

        var sent = new TagValue(
            Item: "Demo.Int32",
            Value: 123,
            Quality: 0xC0,
            Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

        await pub.PublishAsync(sent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = await lis.Frames.ReadAsync(cts.Token);

        // v1.1: 帧首 2 字节是 0xDC + format-id；剩下是真 payload
        Assert.Equal(WireFormat.MagicV11, frame[0]);
        Assert.Equal(WireFormat.FormatMsgpack, frame[1]);
        var payload = frame[2..];

        var back = MessagePackSerializer.Deserialize<TagValue>(payload, ContractlessStandardResolver.Options);

        Assert.Equal(sent.Item, back.Item);
        Assert.Equal(sent.Quality, back.Quality);
        Assert.Equal(sent.Timestamp, back.Timestamp);
        Assert.NotNull(back.Value);
    }

    // INF-2: 同样的 TagValue 用 JsonMessageSerializer 发，listener 收到的应是 UTF-8 JSON。
    [Fact(Timeout = 10_000)]
    public async Task INF2_JsonFrame_RoundTrip()
    {
        await using var lis = await TcpListenerFixture.StartAsync();
        var serializer = new JsonMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(lis.Address, serializer);

        var sent = new TagValue("Demo.String", "hello", 0xC0, DateTimeOffset.UtcNow);
        await pub.PublishAsync(sent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = await lis.Frames.ReadAsync(cts.Token);

        Assert.Equal(WireFormat.MagicV11, frame[0]);
        Assert.Equal(WireFormat.FormatJson, frame[1]);
        var json = System.Text.Encoding.UTF8.GetString(frame[2..]);
        // JsonMessageSerializer 用 camelCase：item / value / quality / timestamp
        Assert.Contains("\"item\":\"Demo.String\"", json);
        Assert.Contains("\"value\":\"hello\"", json);
        Assert.Contains("\"quality\":192", json); // 0xC0 = 192
    }
}
