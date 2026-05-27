using Dc.Infrastructure.Messaging;
using Dc.Opc.Abstractions;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace Dc.Integration.Tests.Infrastructure;

public class BrokerOfflineQueueTests : IDisposable
{
    private readonly string _tmpDir;
    public BrokerOfflineQueueTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "dc-inf6-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    // INF-6: broker 暂离时消息进 queue；broker 起回来后后台 flusher 应在 ~2s 内自动补发，
    //         顺序保持，无丢失。
    [Fact(Timeout = 30_000)]
    public async Task INF6_BrokerOffline_MessagesQueuedAndReplayedInOrder()
    {
        // 先起 listener 拿一个空端口，立刻停掉模拟 broker 不在
        var portHolder = await TcpListenerFixture.StartAsync();
        var port = portHolder.Port;
        var address = portHolder.Address;
        await portHolder.DisposeAsync();

        var queuePath = Path.Combine(_tmpDir, "q.bin");
        var queue = new OutboundQueue(queuePath, maxBytes: 10 * 1024 * 1024);
        var serializer = new MessagePackMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(address, serializer, queue);

        // 阶段 1：broker 不在 → 发 3 条，每条都应抛 BrokerUnavailable，且 queue 里有 3 帧
        for (int i = 0; i < 3; i++)
        {
            var v = new TagValue($"q-{i}", i, 0xC0, DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<BrokerUnavailableException>(() => pub.PublishAsync(v));
        }
        Assert.True(queue.PendingBytes > 0);

        // 阶段 2：起 listener 等 flusher 自动 drain
        await using var listener = await TcpListenerFixture.StartOnPortAsync(port);

        // flusher 周期 2s + connect 时间，给 8s 余量
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var received = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var frame = await listener.Frames.ReadAsync(cts.Token);
            // v1.1 头 + msgpack TagValue → 跳 2 字节解
            var back = MessagePackSerializer.Deserialize<TagValue>(frame[2..], ContractlessStandardResolver.Options);
            received.Add(back.Item);
        }

        // FIFO 顺序保持
        Assert.Equal(new[] { "q-0", "q-1", "q-2" }, received);
        // queue 已清空
        // 给一小段让 commit 落盘后再判断
        await Task.Delay(200);
        Assert.Equal(0, queue.PendingBytes);
    }
}

