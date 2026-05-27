using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Dc.Integration.Tests.Infrastructure;

// 复用的 TCP listener 助手：起本地随机端口，按 wire-format.md 规则
// (4B BE length + payload) 读帧，写到 Channel<byte[]> 供测试断言。
//
// 用法：
//   using var lis = await TcpListenerFixture.StartAsync();
//   lis.Port → 拿端口
//   await lis.Frames.Reader.ReadAsync() → 拿下一帧 payload
public sealed class TcpListenerFixture : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ChannelReader<byte[]> Frames => _frames.Reader;

    private TcpListenerFixture(TcpListener listener)
    {
        _listener = listener;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public static Task<TcpListenerFixture> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new TcpListenerFixture(listener));
    }

    // INF-6 用：复用之前关掉过的端口号（模拟 broker 重启）
    public static Task<TcpListenerFixture> StartOnPortAsync(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return Task.FromResult(new TcpListenerFixture(listener));
    }

    public string Address => $"127.0.0.1:{Port}";

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            _ = ReadClientAsync(client);
        }
    }

    private async Task ReadClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            await using (var ns = client.GetStream())
            {
                var lenBuf = new byte[4];
                while (!_cts.IsCancellationRequested)
                {
                    if (!await TryReadExact(ns, lenBuf, _cts.Token)) return;
                    var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                    if (len <= 0 || len > 16 * 1024 * 1024) return;
                    var payload = new byte[len];
                    if (!await TryReadExact(ns, payload, _cts.Token)) return;
                    await _frames.Writer.WriteAsync(payload, _cts.Token);
                }
            }
        }
        catch { /* 客户端断开正常 */ }
    }

    private static async Task<bool> TryReadExact(NetworkStream s, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            int n;
            try { n = await s.ReadAsync(buf.AsMemory(off), ct); }
            catch (OperationCanceledException) { return false; }
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _frames.Writer.TryComplete();
        _cts.Dispose();
    }
}
