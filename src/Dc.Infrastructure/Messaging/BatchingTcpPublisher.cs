using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Dc.Infrastructure.Messaging;

/// <summary>
/// 批量发送 Publisher：将多条消息攒批后一次性写入 TCP 流，减少锁竞争和网络 flush 次数。
/// <para>
/// 发送策略：消息进入 <see cref="ConcurrentQueue{T}"/> 后由后台 FlushLoop 按
/// <see cref="BatchIntervalMs"/>（默认 50ms）或 <see cref="BatchSize"/>（默认 64 帧）
/// 触发批量发送。单次 flush 获取一次锁、写全部帧、flush 一次网络流。
/// </para>
/// <para>
/// 对 <see cref="IPublisher"/> 调用方完全透明 — TaskOrchestrator 无需改动。
/// </para>
/// </summary>
public sealed class BatchingTcpPublisher : IPublisher, IPublisherHealth
{
    // ---- 可配置参数 ----
    private readonly int _batchIntervalMs;
    private readonly int _batchSize;

    // ---- 内部状态 ----
    private readonly string _host;
    private readonly int _port;
    private readonly IMessageSerializer _serializer;
    private readonly IOutboundQueue? _queue;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentQueue<byte[]> _pending = new();
    private readonly Channel<int> _flushSignal = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private DateTimeOffset _failedUntil = DateTimeOffset.MinValue;
    private Exception? _lastError;
    private long _sendErrorCount;
    private bool _disposed;

    /// <inheritdoc />
    public long SendErrorCount => Interlocked.Read(ref _sendErrorCount);

    /// <inheritdoc />
    public long PendingBytes => _queue?.PendingBytes ?? 0;

    /// <inheritdoc />
    public long DroppedFrameCount => _queue?.DroppedFrameCount ?? 0;

    // 失败冷却：与 TcpPublisher 一致
    private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(2);
    private const int SendTimeoutMs = 5000;

    /// <summary>
    /// 创建批量发送 Publisher。
    /// </summary>
    /// <param name="host">目标 TCP 主机</param>
    /// <param name="port">目标 TCP 端口</param>
    /// <param name="serializer">消息序列化器</param>
    /// <param name="queue">可选的断网缓存队列</param>
    /// <param name="batchIntervalMs">批量刷新间隔（毫秒），默认 50</param>
    /// <param name="batchSize">每批最大帧数，默认 64</param>
    public BatchingTcpPublisher(
        string host,
        int port,
        IMessageSerializer serializer,
        IOutboundQueue? queue = null,
        int batchIntervalMs = 50,
        int batchSize = 64)
    {
        if (batchIntervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(batchIntervalMs));
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        _host = host;
        _port = port;
        _serializer = serializer;
        _queue = queue;
        _batchIntervalMs = batchIntervalMs;
        _batchSize = batchSize;

        _flushTask = Task.Run(FlushLoopAsync);
    }

    /// <inheritdoc />
    public Task PublishAsync<T>(T message, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 序列化 + 帧封装（与 TcpPublisher.BuildFrame 一致）
        var frame = BuildFrame(message);

        // 冷却中：有 queue → 入队后抛；无 queue → 直接抛
        if (DateTimeOffset.UtcNow < _failedUntil && _lastError is not null)
        {
            if (_queue is not null)
            {
                _queue.Enqueue(frame);
                throw new BrokerUnavailableException(
                    $"TCP {_host}:{_port} 处于冷却期；消息已入 queue（{_queue.PendingBytes}B pending）。上次错误: {_lastError.Message}",
                    _lastError);
            }
            throw new InvalidOperationException(
                $"TCP {_host}:{_port} 处于冷却期，上次错误: {_lastError.Message}", _lastError);
        }

        // 入待发队列
        _pending.Enqueue(frame);

        // 达到批量阈值 → 通知 flush loop 提前发送
        if (_pending.Count >= _batchSize)
            _flushSignal.Writer.TryWrite(0); // non-blocking signal

        return Task.CompletedTask;
    }

    // ---- 批量 flush 循环 ----

    private async Task FlushLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 等待：定时 或 信号触发（哪个先到算哪个）
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delayCts.CancelAfter(_batchIntervalMs);
                try
                {
                    await _flushSignal.Reader.ReadAsync(delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (delayCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // 定时器到期 → 正常 flush
                }

                // 冷却中：先判断再决定是否 drain，避免无 queue 时把帧抽出后丢弃。
                if (DateTimeOffset.UtcNow < _failedUntil && _lastError is not null)
                {
                    if (_queue is not null)
                    {
                        // 有 queue → 全部转入 queue，下次重试
                        while (_pending.TryDequeue(out var frame)) _queue.Enqueue(frame);
                    }
                    // 无 queue → 保留 _pending 原样，冷却结束后下个循环再发（不丢数据）
                    continue;
                }

                // 先把旧积压 queue 发完，保证 FIFO：不让新帧越过更早入队的旧帧。
                if (_queue is not null && _queue.PendingBytes > 0)
                    await DrainQueueAsync(ct).ConfigureAwait(false);

                // drain 所有待发帧
                var batch = new List<byte[]>();
                while (_pending.TryDequeue(out var frame))
                    batch.Add(frame);

                if (batch.Count == 0) continue;

                // queue 仍有积压（没drain完）→ 新帧也排到 queue 尾，避免越过旧帧。
                if (_queue is not null && _queue.PendingBytes > 0)
                {
                    foreach (var f in batch) _queue.Enqueue(f);
                    continue;
                }

                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await SendBatchAsync(batch, ct).ConfigureAwait(false);
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    MarkFailed(ex);
                    Interlocked.Increment(ref _sendErrorCount); // 后台发送失败计数，供诊断折入
                    DropConnection();

                    // 只重入「未送出」的帧：已完整发出的不重入，避免 broker 收重复（#10）。
                    // 连接失败等非部分发送场景 sentCount=0 → 整批重入。
                    // 防御性解包：当前 await 单 Task 不会包装异常，但若未来重构成
                    // Task.WhenAll/.Result 会产生 AggregateException，这里也能取回 sentCount。
                    var pe = ex as PartialBatchSendException
                             ?? (ex as AggregateException)?.InnerExceptions
                                    .OfType<PartialBatchSendException>().FirstOrDefault();
                    var sentCount = pe?.SentCount ?? 0;
                    if (_queue is not null)
                    {
                        for (int i = sentCount; i < batch.Count; i++) _queue.Enqueue(batch[i]);
                    }
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SendBatchAsync(List<byte[]> batch, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var sent = 0; // 已完整送出的帧数（单帧 WriteAsync 全有或抛）
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeoutMs);
            var token = sendCts.Token;

            // 合并所有帧为一次 WriteAsync + 一次 FlushAsync
            // 计算总长度避免多次 Write
            var totalLen = 0;
            foreach (var f in batch) totalLen += f.Length;

            if (totalLen <= 8192)
            {
                // 小批量：合并到单个 buffer
                var buf = new byte[totalLen];
                var offset = 0;
                foreach (var f in batch)
                {
                    f.CopyTo(buf, offset);
                    offset += f.Length;
                }
                await _stream!.WriteAsync(buf, token).ConfigureAwait(false);
                sent = batch.Count; // 单次写成功 = 整批送出
            }
            else
            {
                // 大批量：逐帧 Write（避免大数组分配），最后一次 Flush
                foreach (var f in batch)
                {
                    await _stream!.WriteAsync(f, token).ConfigureAwait(false);
                    sent++; // 该帧已完整写出，失败时不应重发它（否则 broker 收重复）
                }
            }

            await _stream!.FlushAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 不在此处 MarkFailed：异常由 FlushLoop catch 标记。携带 sent 计数，让调用方只重入
            // 「未发出的尾部」，避免把已送出的帧重新入队造成 broker 重复（#10）。
            DropConnection();
            throw new PartialBatchSendException(sent, ex);
        }
    }

    // 携带「已成功送出帧数」的发送失败异常，供 FlushLoop 精确重入未发部分。
    private sealed class PartialBatchSendException(int sentCount, Exception inner)
        : Exception(inner.Message, inner)
    {
        public int SentCount { get; } = sentCount;
    }

    private async Task DrainQueueAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _failedUntil) return;

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (!ct.IsCancellationRequested && _queue!.TryPeekFront(out var frame))
            {
                try
                {
                    await EnsureConnectedAsync(ct).ConfigureAwait(false);
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    sendCts.CancelAfter(SendTimeoutMs);
                    await _stream!.WriteAsync(frame!, sendCts.Token).ConfigureAwait(false);
                    await _stream.FlushAsync(sendCts.Token).ConfigureAwait(false);
                    _queue.CommitFront();
                    _lastError = null;
                }
                catch
                {
                    break; // 发不动就下轮再试
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ---- 帧封装（与 TcpPublisher.BuildFrame 一致） ----

    private byte[] BuildFrame<T>(T message)
    {
        var payload = _serializer.Serialize(message);
        var frameSize = WireFormat.HeaderSize + payload.Length;
        var frame = new byte[4 + frameSize];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), frameSize);
        frame[4] = WireFormat.MagicV11;
        frame[5] = WireFormat.FormatIdFor(_serializer.FormatId);
        payload.CopyTo(frame.AsSpan(6));
        return frame;
    }

    // ---- 连接管理（与 TcpPublisher 一致） ----

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is { Connected: true } && _stream is not null)
        {
            var sock = _client.Client;
            if (sock.Poll(0, SelectMode.SelectRead) && sock.Available == 0)
                DropConnection();
            else
                return;
        }

        DropConnection();
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MarkFailed(ex);
            try { client.Dispose(); } catch { }
            throw;
        }
        _client = client;
        _stream = client.GetStream();
    }

    private void MarkFailed(Exception ex)
    {
        _lastError = ex;
        _failedUntil = DateTimeOffset.UtcNow + ReconnectCooldown;
    }

    private void DropConnection()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    // ---- Dispose ----

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // 刷出剩余帧
        try
        {
            var remaining = new List<byte[]>();
            while (_pending.TryDequeue(out var f)) remaining.Add(f);
            if (remaining.Count > 0 && _queue is not null)
            {
                foreach (var f in remaining) _queue.Enqueue(f);
            }
        }
        catch { }

        _cts.Cancel();
        _flushSignal.Writer.TryComplete(); // 唤醒 flush loop
        try { await _flushTask.ConfigureAwait(false); } catch { }

        DropConnection();
        _sendLock.Dispose();
        _cts.Dispose();
        try { _queue?.Dispose(); } catch { }
    }
}
