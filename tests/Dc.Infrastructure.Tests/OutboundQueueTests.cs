using System.Buffers.Binary;
using Dc.Infrastructure.Messaging;
using Xunit;

namespace Dc.Infrastructure.Tests;

public class OutboundQueueTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _dataPath;

    public OutboundQueueTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "dc-outq-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
        _dataPath = Path.Combine(_tmpDir, "test.bin");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    // 帧 = 4B BE length + payload；length 是 payload 长度
    private static byte[] MakeFrame(byte[] payload)
    {
        var f = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(0, 4), payload.Length);
        payload.CopyTo(f, 4);
        return f;
    }

    [Fact]
    public void EnqueuePeekCommit_RoundTrip_FIFOOrder()
    {
        using var q = new OutboundQueue(_dataPath, maxBytes: 1_000_000);

        var f1 = MakeFrame(new byte[] { 1, 2, 3 });
        var f2 = MakeFrame(new byte[] { 4, 5 });
        var f3 = MakeFrame(new byte[] { 6 });
        q.Enqueue(f1);
        q.Enqueue(f2);
        q.Enqueue(f3);

        // v2 每条记录额外 12B 头（magic + len + ~len）
        const int recHeader = 12;
        Assert.Equal((recHeader + f1.Length) + (recHeader + f2.Length) + (recHeader + f3.Length), q.PendingBytes);

        Assert.True(q.TryPeekFront(out var p1));
        Assert.Equal(f1, p1);
        q.CommitFront();

        Assert.True(q.TryPeekFront(out var p2));
        Assert.Equal(f2, p2);
        q.CommitFront();

        Assert.True(q.TryPeekFront(out var p3));
        Assert.Equal(f3, p3);
        q.CommitFront();

        Assert.False(q.TryPeekFront(out _));
        Assert.Equal(0, q.PendingBytes);
    }

    [Fact]
    public void Persistence_AcrossInstances_PreservesPendingFrames()
    {
        var f1 = MakeFrame(new byte[] { 0xAA });
        var f2 = MakeFrame(new byte[] { 0xBB });
        var f3 = MakeFrame(new byte[] { 0xCC });

        using (var q1 = new OutboundQueue(_dataPath, 1_000_000))
        {
            q1.Enqueue(f1);
            q1.Enqueue(f2);
            q1.Enqueue(f3);
            Assert.True(q1.TryPeekFront(out _));
            q1.CommitFront(); // f1 已发
        }

        // 重新打开 — f2/f3 应该还在
        using var q2 = new OutboundQueue(_dataPath, 1_000_000);
        Assert.True(q2.TryPeekFront(out var p));
        Assert.Equal(f2, p);
    }

    [Fact]
    public void Enqueue_OverflowMaxBytes_DropsOldestUntilFits()
    {
        // 每帧 = 4 + 100 = 104 字节；上限 250 字节 → 最多容 2 帧 (208 字节)
        using var q = new OutboundQueue(_dataPath, maxBytes: 250);

        var f1 = MakeFrame(new byte[100]); for (int i = 0; i < 100; i++) f1[4 + i] = 1;
        var f2 = MakeFrame(new byte[100]); for (int i = 0; i < 100; i++) f2[4 + i] = 2;
        var f3 = MakeFrame(new byte[100]); for (int i = 0; i < 100; i++) f3[4 + i] = 3;

        q.Enqueue(f1); // 104B 内
        q.Enqueue(f2); // 208B 内
        q.Enqueue(f3); // 312B > 250 → 丢 f1，剩 f2+f3 (208B)

        Assert.True(q.TryPeekFront(out var head));
        Assert.Equal(f2, head); // f1 被丢，f2 上位
    }

    [Fact]
    public void CorruptMidRecord_RecoversLaterFrames_DoesNotWipeQueue()
    {
        // 回归 #8：中段记录损坏不应清空整队，应 resync 到后续好帧。
        var f1 = MakeFrame(new byte[] { 1, 2, 3 });
        var f2 = MakeFrame(new byte[] { 4, 5 });
        var f3 = MakeFrame(new byte[] { 6 });

        using (var q = new OutboundQueue(_dataPath, 1_000_000))
        {
            q.Enqueue(f1);
            q.Enqueue(f2);
            q.Enqueue(f3);
        }

        // 损坏 f2 记录头：f2 记录起点 = 12+f1.Length，覆盖其 magic 4 字节
        var f2RecordOffset = 12 + f1.Length;
        using (var fs = new FileStream(_dataPath, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(f2RecordOffset, SeekOrigin.Begin);
            fs.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        }

        using var q2 = new OutboundQueue(_dataPath, 1_000_000);
        Assert.True(q2.TryPeekFront(out var p1));
        Assert.Equal(f1, p1);     // f1 完好
        q2.CommitFront();

        // f2 损坏 → 跳过，resync 到 f3（旧实现会把 f3 一并截掉）
        Assert.True(q2.TryPeekFront(out var p3));
        Assert.Equal(f3, p3);
        q2.CommitFront();

        Assert.False(q2.TryPeekFront(out _));
    }

    [Fact]
    public void LegacyV1File_MigratedToV2_PreservesFrames()
    {
        // 回归：旧格式 [4B len][payload] 文件构造时迁移为 v2，帧按序保留。
        var f1 = MakeFrame(new byte[] { 0x11, 0x22 });
        var f2 = MakeFrame(new byte[] { 0x33 });

        // 直接写 legacy v1 文件（MakeFrame 输出即 v1 记录）
        using (var fs = new FileStream(_dataPath, FileMode.Create, FileAccess.Write))
        {
            fs.Write(f1);
            fs.Write(f2);
        }

        using var q = new OutboundQueue(_dataPath, 1_000_000); // 构造触发迁移
        Assert.True(q.TryPeekFront(out var p1));
        Assert.Equal(f1, p1);
        q.CommitFront();
        Assert.True(q.TryPeekFront(out var p2));
        Assert.Equal(f2, p2);
    }

    [Fact]
    public void TryPeekFront_OnEmptyQueue_ReturnsFalse()
    {
        using var q = new OutboundQueue(_dataPath, 1_000_000);
        Assert.False(q.TryPeekFront(out _));
        Assert.Equal(0, q.PendingBytes);
    }

    [Fact]
    public void CommitFront_WithoutPeek_Throws()
    {
        using var q = new OutboundQueue(_dataPath, 1_000_000);
        q.Enqueue(MakeFrame(new byte[] { 1 }));
        Assert.Throws<InvalidOperationException>(() => q.CommitFront());
    }
}
