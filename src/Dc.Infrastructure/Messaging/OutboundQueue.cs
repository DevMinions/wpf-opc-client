using System.Buffers.Binary;

namespace Dc.Infrastructure.Messaging;

// 文件 backed FIFO 队列（记录格式 v2）。
//
// 记录格式 v2（每条）：
//   [4B BE rec magic = 0xDC51_5632 "ÜQV2"-ish] [4B BE payloadLen] [4B BE ~payloadLen] [payload]
//   记录头 12B。payload 不透明（实际是 wire 帧，原样存）。
//
// 为什么带 magic + 长度补码：
//   - 通用队列不能假定 payload 内含 wire magic（曾踩坑）。故自带记录级完整性标记。
//   - magic 让损坏后可向后**重新同步**到下一条完整记录（而非旧版那样把整队从 cursor 截掉）。
//   - ~payloadLen（一次性补码）校验长度字段未被破坏；magic+补码误对齐概率 ≈ 1/2^64，无需 CRC 依赖。
//
// 设计取舍：
//   - 单文件 append-only：write 简单 + fsync 后崩溃也只丢未 flush 的尾部
//   - cursor 写 sidecar 文件：commit 是 O(1)；启动时读回（绝对偏移，从 0 起）
//   - drop-oldest：超 MaxBytes 时 compact 去掉已 commit 段；仍超则按记录丢最旧直到 fit
//   - legacy v1（[4B len][payload]）文件：构造时一次性迁移为 v2
public sealed class OutboundQueue : IOutboundQueue
{
    // 记录起始 magic（与 wire magic 0xDC 区分；选一个不易在长度字段里自然出现的值）
    private const uint RecMagic = 0xDC51_5632;
    private const int RecHeaderSize = 12; // 4B magic + 4B len + 4B ~len

    private readonly string _dataPath;
    private readonly string _cursorPath;
    private readonly long _maxBytes;
    private readonly object _lock = new();

    // peek 时缓存"队首记录总大小"（含 12B 头），commit 时推进 cursor 用
    private int? _pendingRecordSize;

    public OutboundQueue(string dataPath, long maxBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _dataPath = dataPath;
        _cursorPath = dataPath + ".cursor";
        _maxBytes = maxBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(_dataPath) ?? ".");

        // 旧格式（v1：[4B len][payload]）文件 → 一次性迁移为 v2
        lock (_lock)
        {
            if (File.Exists(_dataPath) && !FirstRecordIsV2())
                MigrateLegacyV1ToV2();
        }
    }

    public long PendingBytes
    {
        get
        {
            lock (_lock)
            {
                if (!File.Exists(_dataPath)) return 0;
                var size = new FileInfo(_dataPath).Length;
                var cursor = LoadCursor();
                return Math.Max(0, size - cursor);
            }
        }
    }

    public void Enqueue(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0) return;
        lock (_lock)
        {
            using (var fs = new FileStream(_dataPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                Span<byte> header = stackalloc byte[RecHeaderSize];
                BinaryPrimitives.WriteUInt32BigEndian(header[..4], RecMagic);
                BinaryPrimitives.WriteInt32BigEndian(header[4..8], frame.Length);
                BinaryPrimitives.WriteInt32BigEndian(header[8..12], ~frame.Length);
                fs.Write(header);
                fs.Write(frame);
                fs.Flush(flushToDisk: true);
            }

            // 检查超限。先尝试 compact（去掉已 commit 段）；如果仍超就 drop-oldest 未 commit 帧
            if (new FileInfo(_dataPath).Length > _maxBytes)
            {
                Compact();
                if (new FileInfo(_dataPath).Length > _maxBytes)
                    DropOldestUntilFits();
            }
        }
    }

    public bool TryPeekFront(out byte[]? frame)
    {
        frame = null;
        lock (_lock)
        {
            if (!File.Exists(_dataPath)) return false;
            var cursor = LoadCursor();
            var fileLen = new FileInfo(_dataPath).Length;
            if (cursor >= fileLen) return false;

            using var fs = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // 校验队首记录头；损坏则用 magic 向后 resync 到下一条完整记录，保住其后好帧。
            if (!TryReadRecordHeader(fs, cursor, fileLen, out var len))
            {
                var next = FindNextRecord(fs, cursor + 1, fileLen);
                if (next < 0)
                {
                    // 其后无完整记录（write 中崩溃的半截尾记录即属此类）→ 截掉不可恢复段
                    TruncateAt(cursor);
                    return false;
                }
                SaveCursor(next);
                cursor = next;
                if (!TryReadRecordHeader(fs, cursor, fileLen, out len))
                {
                    TruncateAt(cursor); // 理论不可达（resync 已校验），兜底
                    return false;
                }
            }

            var payload = new byte[len];
            fs.Seek(cursor + RecHeaderSize, SeekOrigin.Begin);
            fs.ReadExactly(payload);
            frame = payload;
            _pendingRecordSize = RecHeaderSize + len;
            return true;
        }
    }

    public void CommitFront()
    {
        lock (_lock)
        {
            if (_pendingRecordSize is null)
                throw new InvalidOperationException("CommitFront 须在 TryPeekFront 成功后调用");

            var cursor = LoadCursor();
            SaveCursor(cursor + _pendingRecordSize.Value);
            _pendingRecordSize = null;

            // 队列空了 → 直接清掉文件
            if (File.Exists(_dataPath))
            {
                var size = new FileInfo(_dataPath).Length;
                if (LoadCursor() >= size)
                {
                    TryDelete(_dataPath);
                    TryDelete(_cursorPath);
                }
            }
        }
    }

    public void Dispose() { /* 文件句柄都是每次开关，无长持有句柄 */ }

    // —————————— 私有帮手 ——————————

    // 校验 offset 处是否为合法 v2 记录头：magic + len>0 + ~len 自洽 + 记录不越界。
    private static bool TryReadRecordHeader(FileStream fs, long offset, long fileLength, out int len)
    {
        len = 0;
        if (offset < 0 || offset + RecHeaderSize > fileLength) return false;
        Span<byte> h = stackalloc byte[RecHeaderSize];
        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(h);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(h[..4]);
        var l = BinaryPrimitives.ReadInt32BigEndian(h[4..8]);
        var lChk = BinaryPrimitives.ReadInt32BigEndian(h[8..12]);
        if (magic != RecMagic || l <= 0 || lChk != ~l || offset + RecHeaderSize + l > fileLength)
            return false;
        len = l;
        return true;
    }

    // 从 from 起扫描下一条合法记录起点（用 rec magic 重新对齐）。找不到返回 -1。
    // 冷路径（仅记录损坏时触发）：一次性读入剩余区间在内存里扫，避免逐字节 syscall。
    private static long FindNextRecord(FileStream fs, long from, long fileLength)
    {
        var spanLen = fileLength - from;
        if (spanLen < RecHeaderSize) return -1;
        var buf = new byte[spanLen];
        fs.Seek(from, SeekOrigin.Begin);
        fs.ReadExactly(buf);
        for (int i = 0; i + RecHeaderSize <= buf.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i, 4)) != RecMagic) continue;
            var l = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(i + 4, 4));
            var lChk = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(i + 8, 4));
            if (l > 0 && lChk == ~l && from + i + (long)RecHeaderSize + l <= fileLength)
                return from + i;
        }
        return -1;
    }

    // 文件首条是否 v2 记录（用于区分 legacy v1）。
    private bool FirstRecordIsV2()
    {
        var fi = new FileInfo(_dataPath);
        if (fi.Length < 4) return true; // 空/过短当作 v2（无需迁移）
        using var fs = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> m = stackalloc byte[4];
        fs.ReadExactly(m);
        return BinaryPrimitives.ReadUInt32BigEndian(m) == RecMagic;
    }

    // legacy v1（[4B len][payload]，payload 即 wire 帧）→ v2。从当前 cursor 起把未消费记录重写为 v2，cursor 归零。
    private void MigrateLegacyV1ToV2()
    {
        var cursor = LoadCursor();
        var tmp = _dataPath + ".migrate";
        using (var src = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            src.Seek(cursor, SeekOrigin.Begin);
            Span<byte> lenBuf = stackalloc byte[4];
            Span<byte> header = stackalloc byte[RecHeaderSize];
            while (src.Position + 4 <= src.Length)
            {
                src.ReadExactly(lenBuf);
                var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                if (len <= 0 || src.Position + len > src.Length) break; // 半截/损坏 → 停（尾部不可恢复）

                // v1 记录整体（4B len + payload）就是 wire 帧；作为 v2 的 payload 重新封装
                var wireFrame = new byte[4 + len];
                lenBuf.CopyTo(wireFrame);
                src.ReadExactly(wireFrame.AsSpan(4));

                BinaryPrimitives.WriteUInt32BigEndian(header[..4], RecMagic);
                BinaryPrimitives.WriteInt32BigEndian(header[4..8], wireFrame.Length);
                BinaryPrimitives.WriteInt32BigEndian(header[8..12], ~wireFrame.Length);
                dst.Write(header);
                dst.Write(wireFrame);
            }
            dst.Flush(flushToDisk: true);
        }
        File.Replace(tmp, _dataPath, destinationBackupFileName: null);
        SaveCursor(0);
    }

    private long LoadCursor()
    {
        if (!File.Exists(_cursorPath)) return 0;
        Span<byte> buf = stackalloc byte[8];
        using var fs = new FileStream(_cursorPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.ReadExactly(buf);
        return BinaryPrimitives.ReadInt64LittleEndian(buf);
    }

    private void SaveCursor(long value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        using var fs = new FileStream(_cursorPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(buf);
        fs.Flush(flushToDisk: true);
    }

    // 重写文件去掉 [0, cursor) 段。新 cursor = 0。
    private void Compact()
    {
        var cursor = LoadCursor();
        if (cursor <= 0 || !File.Exists(_dataPath)) return;

        var tmp = _dataPath + ".compact";
        using (var src = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            src.Seek(cursor, SeekOrigin.Begin);
            src.CopyTo(dst);
            dst.Flush(flushToDisk: true);
        }
        File.Replace(tmp, _dataPath, destinationBackupFileName: null);
        SaveCursor(0);
    }

    // Compact 后仍超 MaxBytes → 从头按记录丢最旧，最后 Compact 一次。
    // 遇损坏记录用 magic resync 越过，确保真正降到 MaxBytes 以下（否则配额形同虚设）。
    private void DropOldestUntilFits()
    {
        var fi = new FileInfo(_dataPath);
        if (fi.Length <= _maxBytes) return;

        var droppedToOffset = 0L;
        using (var fs = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            while (fi.Length - droppedToOffset > _maxBytes)
            {
                if (TryReadRecordHeader(fs, droppedToOffset, fi.Length, out var len))
                {
                    droppedToOffset += RecHeaderSize + len;
                }
                else
                {
                    var next = FindNextRecord(fs, droppedToOffset + 1, fi.Length);
                    droppedToOffset = next < 0 ? fi.Length : next;
                }
                if (droppedToOffset >= fi.Length) break;
            }
        }
        SaveCursor(droppedToOffset);
        Compact();
    }

    // 截掉 [position, end) 段，处理 write 中崩溃留下的半截记录
    private void TruncateAt(long position)
    {
        using var fs = new FileStream(_dataPath, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.SetLength(position);
        fs.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 路径锁定就下次再试 */ }
    }
}
