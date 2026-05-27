using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Dc.Infrastructure.Messaging;
using Xunit;

namespace Dc.Infrastructure.Tests;

public class MessagingTests
{
    public record TagSample(string Item, double Value, ushort Quality, long Timestamp);

    [Fact]
    public void MessagePackSerializer_Roundtrip_PreservesAllFields()
    {
        var ser = new MessagePackMessageSerializer();
        var original = new TagSample("Random.Int1", 42.5, 0xC0, 1700000000000);

        var bytes = ser.Serialize(original);
        var back = ser.Deserialize<TagSample>(bytes);

        Assert.Equal(original, back);
    }

    [Fact]
    public void MessagePackSerializer_FormatId_IsMsgpack()
    {
        var ser = new MessagePackMessageSerializer();
        Assert.Equal("msgpack", ser.FormatId);
    }

    [Fact]
    public async Task TcpPublisher_WritesLengthPrefixedFrame_ListenerSeesExactBytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ns = client.GetStream();
            var lenBuf = new byte[4];
            await ns.ReadExactlyAsync(lenBuf);
            var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            var frame = new byte[len];
            await ns.ReadExactlyAsync(frame);
            // v1.1: 跳过 2 字节 magic + format-id 头
            return frame[2..];
        });

        var serializer = new MessagePackMessageSerializer();
        await using var pub = new TcpPublisher("127.0.0.1", port, serializer);
        var msg = new TagSample("Random.Int1", 42.5, 0xC0, 1700000000000);
        await pub.PublishAsync(msg);

        var received = await serverTask;
        var deserialized = serializer.Deserialize<TagSample>(received);
        Assert.Equal(msg, deserialized);
    }

    [Fact]
    public async Task TcpPublisher_MultiplePublishes_AllReceived()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ns = client.GetStream();
            var results = new List<TagSample>();
            var ser = new MessagePackMessageSerializer();
            for (int i = 0; i < 5; i++)
            {
                var lenBuf = new byte[4];
                await ns.ReadExactlyAsync(lenBuf);
                var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                var frame = new byte[len];
                await ns.ReadExactlyAsync(frame);
                // v1.1: 跳过 2 字节 header
                results.Add(ser.Deserialize<TagSample>(frame[2..]));
            }
            return results;
        });

        await using var pub = new TcpPublisher("127.0.0.1", port, new MessagePackMessageSerializer());
        for (int i = 0; i < 5; i++)
            await pub.PublishAsync(new TagSample($"T{i}", i * 1.5, 0xC0, i));

        var received = await serverTask;
        Assert.Equal(5, received.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal($"T{i}", received[i].Item);
            Assert.Equal(i * 1.5, received[i].Value);
        }
    }

    // (旧 TcpPublisher_ReconnectsAfterServerDrop 删除)
    // 该测试基于 Phase 6 之前的 publisher 行为（失败立即抛 + 立即重连）。Phase 6
    // 加了 2s 冷却 + 半关闭主动检测后，单元层模拟"server drop → reconnect"会跨越
    // TCP/cooldown/queue 多个边界，断言哪一帧到达哪个 client 极易 flaky。同等覆盖
    // 由 Dc.Integration.Tests/Infrastructure/ReconnectBackoffTests 的 INF-3 (冷却快速失败)
    // 和 INF-4 (冷却后恢复) 提供，且行为更接近真生产路径。

    [Fact]
    public async Task TcpPublisher_FromAddress_ParsesHostPort()
    {
        await using var pub = TcpPublisher.FromAddress("192.168.1.1:5000", new MessagePackMessageSerializer());
        Assert.NotNull(pub);
    }

    [Fact]
    public void TcpPublisher_FromAddress_RejectsMalformed()
    {
        var ser = new MessagePackMessageSerializer();
        Assert.Throws<ArgumentException>(() => TcpPublisher.FromAddress("no-port", ser));
        Assert.Throws<ArgumentException>(() => TcpPublisher.FromAddress("host:notanumber", ser));
    }

    private static async Task<TagSample> ReadOneAsync(TcpClient client, IMessageSerializer ser)
    {
        await using var ns = client.GetStream();
        var lenBuf = new byte[4];
        await ns.ReadExactlyAsync(lenBuf);
        var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        var frame = new byte[len];
        await ns.ReadExactlyAsync(frame);
        // v1.1: 跳过 2 字节 header
        return ser.Deserialize<TagSample>(frame[2..]);
    }
}
