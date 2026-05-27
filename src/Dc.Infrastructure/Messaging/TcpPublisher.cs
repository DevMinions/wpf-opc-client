using System.Buffers.Binary;
using System.Net.Sockets;

namespace Dc.Infrastructure.Messaging;

public sealed class TcpPublisher : IPublisher
{
    // 失败后冷却 — 防止 broker 长期不在时把每个消息都拖到 TCP connect 超时（默认 ~21s on Windows）。
    // 冷却期内 PublishAsync 直接抛上一次的错（轻量 + 不阻塞），让 orchestrator 增计 PublishErrorCount。
    private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(2);
    private const int SendTimeoutMs = 5000;
    // 后台 flusher 周期：与 ReconnectCooldown 同步即可，每过一轮冷却就尝试 drain 队列
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly string _host;
    private readonly int _port;
    private readonly IMessageSerializer _serializer;
    private readonly IOutboundQueue? _queue;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _flushCts = new();
    private readonly Task? _flushTask;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private DateTimeOffset _failedUntil = DateTimeOffset.MinValue;
    private Exception? _lastError;
    private bool _disposed;

    public TcpPublisher(string host, int port, IMessageSerializer serializer, IOutboundQueue? queue = null)
    {
        _host = host;
        _port = port;
        _serializer = serializer;
        _queue = queue;
        // queue 非空 → 启动后台 drain task
        if (_queue is not null)
            _flushTask = Task.Run(FlushLoopAsync);
    }

    public static TcpPublisher FromAddress(string address, IMessageSerializer serializer, IOutboundQueue? queue = null)
    {
        var idx = address.LastIndexOf(':');
        if (idx <= 0) throw new ArgumentException($"Invalid address '{address}', expected host:port", nameof(address));
        var host = address[..idx];
        if (!int.TryParse(address[(idx + 1)..], out var port))
            throw new ArgumentException($"Invalid port in '{address}'", nameof(address));
        return new TcpPublisher(host, port, serializer, queue);
    }

    public async Task PublishAsync<T>(T message, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 帧字节：v1.1 [4B BE length][1B 0xDC][1B format-id][payload]
        var frame = BuildFrame(message);

        // 冷却中 + 有 queue → 直接入队后抛（orchestrator 看到错但消息不会丢）
        // 冷却中 + 无 queue → 抛冷却异常（旧行为）
        if (DateTimeOffset.UtcNow < _failedUntil && _lastError is not null)
        {
            if (_queue is not null)
            {
                _queue.Enqueue(frame);
                throw new BrokerUnavailableException(
                    $"TCP {_host}:{_port} 处于冷却期；消息已入 queue（{_queue.PendingBytes}B pending）。上次错误: {_lastError.Message}",
                    _lastError);
            }
            throw new InvalidOperationException($"TCP {_host}:{_port} 处于冷却期，上次错误: {_lastError.Message}", _lastError);
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await SendFrameAsync(frame, ct).ConfigureAwait(false);
                _lastError = null;
            }
            catch (Exception ex) when (_queue is not null)
            {
                // 有 queue → enqueue + 抛特定异常（与"无 queue 直接失败"区分）
                MarkFailed(ex);
                DropConnection();
                _queue.Enqueue(frame);
                throw new BrokerUnavailableException(
                    $"TCP {_host}:{_port} 发送失败；消息已入 queue（{_queue.PendingBytes}B pending）。错误: {ex.Message}",
                    ex);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

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

    // 单帧发送：连接 + 写整帧（4B 长度前缀已在 frame 里）+ flush
    private async Task SendFrameAsync(byte[] frame, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeoutMs);
            await _stream!.WriteAsync(frame, sendCts.Token).ConfigureAwait(false);
            await _stream.FlushAsync(sendCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MarkFailed(ex);
            DropConnection();
            throw;
        }
    }

    // 后台 drain：每 FlushInterval 检查 queue 队首；连得上就 send + commit；任一失败 break，下轮再试
    private async Task FlushLoopAsync()
    {
        var ct = _flushCts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(FlushInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (_queue is null || _queue.PendingBytes == 0) continue;
            // 冷却中跳过本轮，等冷却过去
            if (DateTimeOffset.UtcNow < _failedUntil) continue;

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (!ct.IsCancellationRequested && _queue.TryPeekFront(out var frame))
                {
                    try
                    {
                        await SendFrameAsync(frame!, ct).ConfigureAwait(false);
                        _queue.CommitFront();
                        _lastError = null;
                    }
                    catch
                    {
                        // 发不成功 → 跳出 inner 循环，等下轮 Flush 再试
                        break;
                    }
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        // TcpClient.Connected 在 peer 半关闭后仍可能返回 true（直到下次写才报错）。
        // 用 Socket.Poll 主动探一下：select-on-read 立即返回 + Available=0 → peer 已 FIN。
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _flushCts.Cancel();
        try { if (_flushTask is not null) await _flushTask.ConfigureAwait(false); } catch { }
        DropConnection();
        _sendLock.Dispose();
        _flushCts.Dispose();
        try { _queue?.Dispose(); } catch { }
    }
}

// PublishAsync 在 queue-enabled 模式下抛这个，让上游能区分"真丢消息"vs"暂时不通已 queue"
public sealed class BrokerUnavailableException : Exception
{
    public BrokerUnavailableException(string message, Exception inner) : base(message, inner) { }
}
