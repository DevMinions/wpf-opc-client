namespace Dc.Infrastructure.Messaging;

// 文件 backed FIFO queue，给 TcpPublisher 在 broker 断网时存帧、恢复后补发用。
//
// 协议：
//   - Enqueue(frame) — 追加帧字节（含 4B 长度前缀）。线程安全。
//   - TryPeekFront(out frame) — 看队首一帧但不移除（用于 send 尝试）。
//   - CommitFront() — 推进 cursor，丢弃队首。仅在上次 Peek 后 send 成功才调。
//   - PendingBytes — 未发字节数。供 UI / 监控展示。
//
// 文件结构：
//   <Directory>/<name>.bin    — 帧字节流（append-only，过大时整体 rewrite 跳过已发段）
//   <Directory>/<name>.cursor — 8 字节 long，记 _bin_ 文件中已 commit 的 byte offset
public interface IOutboundQueue : IDisposable
{
    long PendingBytes { get; }

    void Enqueue(ReadOnlySpan<byte> frame);

    bool TryPeekFront(out byte[]? frame);

    void CommitFront();
}
