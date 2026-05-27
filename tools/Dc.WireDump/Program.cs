using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MessagePack;
using MessagePack.Resolvers;

namespace Dc.WireDump;

// 最小 TCP 接收器，专门用来验证 Dc.App 的发布管线端到端通了：
//   1. TCP listen on --host:--port
//   2. 接受单个连接（每个 Task 一条 TCP 连接）
//   3. 循环读 4 字节 big-endian 长度前缀 + 长度字节负载
//   4. 按 --format 解码（msgpack | json）并美化打印
//
// 不做并发多连接 / 不持久化 / 不应答 — 单纯做"看见就贴出来"的回声夹板。
//
// 用法（同机调试 Dc.App 默认 127.0.0.1:5000）：
//   dotnet run --project wpf/tools/Dc.WireDump -- --port 5000 --format msgpack
//   dotnet run --project wpf/tools/Dc.WireDump -- --port 5000 --format json
//
// Dc.App 那侧任务的 TCP 地址改成 127.0.0.1:5000 即可。
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var host = GetOpt(args, "--host", "0.0.0.0");
        var port = int.Parse(GetOpt(args, "--port", "5000"));
        var format = GetOpt(args, "--format", "msgpack").ToLowerInvariant();
        if (format is not ("msgpack" or "json"))
        {
            Console.Error.WriteLine($"unknown format '{format}' — use msgpack | json");
            return 2;
        }

        var listener = new TcpListener(System.Net.IPAddress.Parse(host == "0.0.0.0" ? "0.0.0.0" : host), port);
        listener.Start();
        Console.WriteLine($"[wiredump] listening on {host}:{port}  format={format}  press Ctrl+C to quit");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                _ = HandleAsync(client, format, cts.Token); // fire and forget — 任务级隔离
            }
        }
        finally
        {
            listener.Stop();
        }
        return 0;
    }

    private static async Task HandleAsync(TcpClient client, string format, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Console.WriteLine($"[wiredump] + connect from {remote}");
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var lenBuf = new byte[4];
                var count = 0L;
                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(stream, lenBuf, ct)) break;
                    var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                    if (len <= 0 || len > 16 * 1024 * 1024) // 防御 16MB 上限
                    {
                        Console.Error.WriteLine($"[wiredump] frame size out of range: {len}");
                        break;
                    }
                    var frame = new byte[len];
                    if (!await ReadExactAsync(stream, frame, ct)) break;
                    count++;
                    PrintFrame(remote, count, format, frame);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[wiredump] {remote} error: {ex.Message}");
        }
        Console.WriteLine($"[wiredump] - disconnect {remote}");
    }

    private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0) return false; // EOF
            off += n;
        }
        return true;
    }

    // v1.1 wire: 自检 frame[0]==0xDC magic → 拆 1B format-id 自适应解码；
    // 否则按 --format 命令行回落处理（兼容旧 v1.0 raw 流，主要是手动塞数据测试）。
    private const byte MagicV11      = 0xDC;
    private const byte FormatMsgpack = 0x01;
    private const byte FormatJson    = 0x02;

    private static void PrintFrame(string remote, long idx, string fallbackFormat, byte[] frame)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTimeOffset.Now.ToString("HH:mm:ss.fff")).Append("] ");
        sb.Append(remote).Append("  #").Append(idx).Append("  ").Append(frame.Length).Append("B  ");

        byte[] payload;
        string effectiveFormat;
        if (frame.Length >= 2 && frame[0] == MagicV11)
        {
            effectiveFormat = frame[1] switch
            {
                FormatMsgpack => "msgpack",
                FormatJson    => "json",
                _             => $"unknown(0x{frame[1]:X2})"
            };
            payload = frame[2..];
            sb.Append("[v1.1 ").Append(effectiveFormat).Append("] ");
        }
        else
        {
            // 旧/裸帧
            payload = frame;
            effectiveFormat = fallbackFormat;
            sb.Append("[raw ").Append(effectiveFormat).Append("] ");
        }

        try
        {
            object? decoded = effectiveFormat switch
            {
                "msgpack" => MessagePackSerializer.Deserialize<object>(payload, ContractlessStandardResolver.Options),
                "json"    => JsonSerializer.Deserialize<JsonElement>(payload),
                _         => "(unknown format)"
            };
            var json = JsonSerializer.Serialize(decoded, new JsonSerializerOptions { WriteIndented = false });
            sb.Append(json);
        }
        catch (Exception ex)
        {
            sb.Append("decode failed: ").Append(ex.Message);
        }

        Console.WriteLine(sb.ToString());
    }

    private static string GetOpt(string[] args, string name, string defaultValue)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return defaultValue;
    }
}
