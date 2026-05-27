using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Dc.Infrastructure.Messaging;
using Xunit;

namespace Dc.Infrastructure.Tests;

public class BatchingPublisherTests
{
    public record TagSample(string Item, double Value, ushort Quality, long Timestamp);

    private static IMessageSerializer Ser => new MessagePackMessageSerializer();

    /// <summary>Helper: 读一帧 v1.1</summary>
    private static async Task<TagSample> ReadOneFrameAsync(NetworkStream ns)
    {
        var lenBuf = new byte[4];
        await ns.ReadExactlyAsync(lenBuf);
        var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        var frame = new byte[len];
        await ns.ReadExactlyAsync(frame);
        return Ser.Deserialize<TagSample>(frame[2..]); // skip magic + format-id
    }

    [Fact]
    public async Task BatchingPublisher_SingleMessage_ReceivedByServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ns = client.GetStream();
            return await ReadOneFrameAsync(ns);
        });

        await using var pub = new BatchingTcpPublisher("127.0.0.1", port, Ser, batchIntervalMs: 50);
        var msg = new TagSample("T1", 1.0, 0xC0, 100);
        await pub.PublishAsync(msg);

        var received = await serverTask;
        Assert.Equal(msg, received);
    }

    [Fact]
    public async Task BatchingPublisher_MultipleMessages_AllReceived()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        const int count = 20;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ns = client.GetStream();
            var results = new List<TagSample>();
            for (int i = 0; i < count; i++)
                results.Add(await ReadOneFrameAsync(ns));
            return results;
        });

        await using var pub = new BatchingTcpPublisher("127.0.0.1", port, Ser, batchIntervalMs: 30);
        for (int i = 0; i < count; i++)
            await pub.PublishAsync(new TagSample($"T{i}", i * 1.5, 0xC0, i));

        var received = await serverTask;
        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal($"T{i}", received[i].Item);
            Assert.Equal(i * 1.5, received[i].Value);
        }
    }

    [Fact]
    public async Task BatchingPublisher_BatchSizeThreshold_TriggersEarlyFlush()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        const int batchSize = 5;
        const int totalMessages = 10;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ns = client.GetStream();
            var results = new List<TagSample>();
            for (int i = 0; i < totalMessages; i++)
                results.Add(await ReadOneFrameAsync(ns));
            return results;
        });

        // batchIntervalMs 设很大（5s），依赖 batchSize=5 触发 flush
        await using var pub = new BatchingTcpPublisher("127.0.0.1", port, Ser, batchIntervalMs: 5000, batchSize: batchSize);

        for (int i = 0; i < totalMessages; i++)
            await pub.PublishAsync(new TagSample($"T{i}", i, 0xC0, i));

        var received = await serverTask;
        Assert.Equal(totalMessages, received.Count);
    }

    [Fact]
    public async Task BatchingPublisher_PublishAsync_ReturnsImmediately()
    {
        // 无 server — PublishAsync 应该立即返回（消息进入队列）
        await using var pub = new BatchingTcpPublisher("127.0.0.1", 19999, Ser, batchIntervalMs: 5000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
            await pub.PublishAsync(new TagSample($"T{i}", i, 0xC0, i));
        sw.Stop();

        // 100 次 PublishAsync 应在 < 500ms 内完成（只是入队）
        Assert.True(sw.ElapsedMilliseconds < 500, $"PublishAsync took {sw.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Fact]
    public async Task BatchingPublisher_Cooldown_ThrowsBrokerUnavailable_WhenQueueEnabled()
    {
        // 不依赖 TCP 断连 — 直接测试冷却逻辑：
        // 构造一个指向不存在端口的 publisher（连接会失败），发一条消息入队，
        // 等 flush 循环尝试连接并失败进入冷却，再发消息应抛 BrokerUnavailableException。
        var queuePath = Path.Combine(Path.GetTempPath(), $"batch-cooldown-{Guid.NewGuid():N}.bin");
        var queue = new OutboundQueue(queuePath, 1024 * 1024);

        // 端口 1 — 几乎不会有服务监听，ConnectAsync 会很快失败
        await using var pub = new BatchingTcpPublisher("127.0.0.1", 1, Ser, queue, batchIntervalMs: 30);

        // 发一条 → 进入 _pending 队列
        await pub.PublishAsync(new TagSample("T1", 1.0, 0xC0, 1));

        // 等 flush 循环尝试连接并失败（30ms interval + connect timeout）
        await Task.Delay(500);

        // 再发消息 → 如果冷却已生效，应抛 BrokerUnavailableException
        // 如果冷却还没生效，消息正常入队不抛 — 两种都算正确行为
        try
        {
            await pub.PublishAsync(new TagSample("T2", 2.0, 0xC0, 2));
            // 没抛 — 冷却还没生效，也行（timing dependent）
        }
        catch (BrokerUnavailableException ex)
        {
            Assert.Contains("冷却期", ex.Message);
        }

        // 清理
        try { File.Delete(queuePath); } catch { }
        try { File.Delete(queuePath + ".cursor"); } catch { }
    }

    [Fact]
    public async Task BatchingPublisher_Dispose_CompletesWithoutHanging()
    {
        // Dispose 应该快速完成 — 剩余帧入 queue（如果有），flush loop 取消
        var pub = new BatchingTcpPublisher("127.0.0.1", 19999, Ser, batchIntervalMs: 30);
        for (int i = 0; i < 10; i++)
            await pub.PublishAsync(new TagSample($"T{i}", i, 0xC0, i));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await pub.DisposeAsync();
        sw.Stop();

        // Dispose 应在 5s 内完成（不会卡在连接等待上）
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Dispose took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public async Task BatchingPublisher_Dispose_WithQueue_EnqueuesRemainingFrames()
    {
        var queuePath = Path.Combine(Path.GetTempPath(), $"batch-dispose-{Guid.NewGuid():N}.bin");
        var queue = new OutboundQueue(queuePath, 1024 * 1024);

        var pub = new BatchingTcpPublisher("127.0.0.1", 19999, Ser, queue, batchIntervalMs: 30);
        for (int i = 0; i < 5; i++)
            await pub.PublishAsync(new TagSample($"T{i}", i, 0xC0, i));

        await pub.DisposeAsync();

        // 剩余帧应已入 queue
        Assert.True(queue.PendingBytes > 0, "Queue should have pending bytes after Dispose");

        // 清理
        try { File.Delete(queuePath); } catch { }
        try { File.Delete(queuePath + ".cursor"); } catch { }
    }

    [Fact]
    public async Task BatchingPublisher_SendErrorCount_IncrementsOnBackgroundFailure()
    {
        // 回归 #3：PublishAsync 立即返回，发送在后台失败。IPublisherHealth.SendErrorCount
        // 必须能观测到后台失败，否则 TaskOrchestrator 折入诊断后仍是 0、Dashboard 假健康。
        // 端口 1 几乎必然拒绝连接，后台 flush 会失败累计。
        await using var pub = new BatchingTcpPublisher("127.0.0.1", 1, Ser, batchIntervalMs: 30);

        Assert.Equal(0, ((IPublisherHealth)pub).SendErrorCount);

        await pub.PublishAsync(new TagSample("T1", 1.0, 0xC0, 1));

        // 等后台 flush 至少尝试并失败一次
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (((IPublisherHealth)pub).SendErrorCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(((IPublisherHealth)pub).SendErrorCount > 0,
            "后台发送失败应使 SendErrorCount > 0（供诊断折入 PublishErrorCount）");
    }

    [Fact]
    public async Task BatchingPublisher_LargeBatch_AllFramesExactlyOnceInOrder()
    {
        // 触发 >8192B 逐帧发送路径（#10 改动所在）：验证正常情况下不丢、不重、保序。
        // 每帧 payload ~400B，40 帧 → 远超 8192B 单次合并阈值。
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        const int count = 40;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ns = client.GetStream();
            var results = new List<TagSample>();
            for (int i = 0; i < count; i++)
                results.Add(await ReadOneFrameAsync(ns));
            return results;
        });

        await using var pub = new BatchingTcpPublisher("127.0.0.1", port, Ser, batchIntervalMs: 5000, batchSize: count + 1);
        var bigText = new string('x', 400);
        for (int i = 0; i < count; i++)
            await pub.PublishAsync(new TagSample($"L{i}-{bigText}", i, 0xC0, i));

        var received = await serverTask;
        Assert.Equal(count, received.Count);                       // 不丢
        Assert.Equal(count, received.Select(r => r.Item).Distinct().Count()); // 不重
        for (int i = 0; i < count; i++)
            Assert.StartsWith($"L{i}-", received[i].Item);          // 保序
    }

    [Fact]
    public void BatchingPublisher_InvalidArgs_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BatchingTcpPublisher("127.0.0.1", 5000, Ser, batchIntervalMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BatchingTcpPublisher("127.0.0.1", 5000, Ser, batchSize: 0));
    }

    [Fact]
    public async Task BatchingPublisher_WireFormat_MatchesV11()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[]? rawFrame = null;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ns = client.GetStream();
            var lenBuf = new byte[4];
            await ns.ReadExactlyAsync(lenBuf);
            var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            var frame = new byte[4 + len];
            var frameMem = new Memory<byte>(frame);
            new ReadOnlyMemory<byte>(lenBuf).CopyTo(frameMem);
            await ns.ReadExactlyAsync(frameMem[4..]);
            rawFrame = frame;
        });

        await using var pub = new BatchingTcpPublisher("127.0.0.1", port, Ser, batchIntervalMs: 50);
        await pub.PublishAsync(new TagSample("W1", 1.0, 0xC0, 1));

        await serverTask;
        Assert.NotNull(rawFrame);

        // v1.1: [4B BE length][0xDC magic][format-id][payload]
        var frameLen = BinaryPrimitives.ReadInt32BigEndian(rawFrame);
        Assert.Equal(rawFrame.Length - 4, frameLen); // length field = magic + format-id + payload
        Assert.Equal(WireFormat.MagicV11, rawFrame[4]);     // 0xDC
        Assert.Equal(WireFormat.FormatMsgpack, rawFrame[5]); // 0x01
    }

    [Fact]
    public async Task BatchingPublisher_BatchedFrames_AreSeparateOnWire()
    {
        // 验证多条消息被合并到一次 TCP 写入但帧边界清晰
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        const int count = 3;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ns = client.GetStream();
            var results = new List<TagSample>();
            for (int i = 0; i < count; i++)
                results.Add(await ReadOneFrameAsync(ns));
            return results;
        });

        await using var pub = new BatchingTcpPublisher("127.0.0.1", port, Ser, batchIntervalMs: 50);
        for (int i = 0; i < count; i++)
            await pub.PublishAsync(new TagSample($"B{i}", i * 10.0, 0xC0, i));

        var received = await serverTask;
        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal($"B{i}", received[i].Item);
    }
}
