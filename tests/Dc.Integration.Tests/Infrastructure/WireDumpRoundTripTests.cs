using Dc.Infrastructure.Messaging;
using Dc.Opc.Abstractions;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace Dc.Integration.Tests.Infrastructure;

public class WireDumpRoundTripTests
{
    // INF-5: 模拟 Dc.WireDump 的解码路径 — publisher 发 N 条 → listener 按帧分割 →
    //         msgpack 解 → JSON 序列化 → 串能匹配关键字段。这一条等于"WireDump 在生产用法下能解码"。
    [Theory(Timeout = 15_000)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task INF5_WireDumpDecodes_SequentialFrames(int n)
    {
        await using var lis = await TcpListenerFixture.StartAsync();
        var serializer = new MessagePackMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(lis.Address, serializer);

        var samples = new List<TagValue>();
        for (int i = 0; i < n; i++)
        {
            var v = new TagValue($"Tag{i}", i * 10, 0xC0,
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + i));
            samples.Add(v);
            await pub.PublishAsync(v);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (int i = 0; i < n; i++)
        {
            var frame = await lis.Frames.ReadAsync(cts.Token);
            // 走 WireDump 同套 v1.1 头校验
            Assert.Equal(WireFormat.MagicV11, frame[0]);
            Assert.Equal(WireFormat.FormatMsgpack, frame[1]);
            var decoded = MessagePackSerializer.Deserialize<object>(frame[2..], ContractlessStandardResolver.Options);
            var json = System.Text.Json.JsonSerializer.Serialize(decoded);
            Assert.Contains($"Tag{i}", json);
        }
    }
}
