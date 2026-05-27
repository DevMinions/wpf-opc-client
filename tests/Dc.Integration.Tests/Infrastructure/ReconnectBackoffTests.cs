using System.Diagnostics;
using System.Net.Sockets;
using Dc.Infrastructure.Messaging;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Integration.Tests.Infrastructure;

public class ReconnectBackoffTests
{
    // INF-3: listener 关掉后再发 3 条；第 1 条触发真 TCP connect 失败，
    // 后 2 条在 2 秒冷却内必须快速失败（< 100ms 各自），不真去 connect。
    [Fact(Timeout = 30_000)]
    public async Task INF3_PublisherCooldown_FastFail()
    {
        // 先起一个 listener 拿端口，立刻 Stop 让端口可用但拒绝连接
        var lis = await TcpListenerFixture.StartAsync();
        var address = lis.Address;
        await lis.DisposeAsync();
        // 这里端口已经释放，再连会 ConnectionRefused

        var serializer = new MessagePackMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(address, serializer);
        var sample = new TagValue("X", 1, 0xC0, DateTimeOffset.UtcNow);

        // 第 1 条：实际 TCP connect 失败，耗时不限
        var firstError = await Assert.ThrowsAnyAsync<Exception>(() => pub.PublishAsync(sample));
        Assert.True(IsConnectFailure(firstError), $"首次失败应是 TCP 连接错，实际: {firstError.GetType().Name} - {firstError.Message}");

        // 第 2、3 条：冷却中 → 应快速失败（实测应在 100ms 内）
        for (int i = 0; i < 2; i++)
        {
            var sw = Stopwatch.StartNew();
            var err = await Assert.ThrowsAnyAsync<Exception>(() => pub.PublishAsync(sample));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 100,
                $"冷却期内第 {i + 2} 条应快速失败，实际 {sw.ElapsedMilliseconds}ms");
            Assert.Contains("冷却期", err.Message);
        }
    }

    // INF-4: broker 短暂下线（< 冷却时间）再起来，冷却期过后下一条应成功发出。
    [Fact(Timeout = 30_000)]
    public async Task INF4_PublisherRecovery_AfterCooldown()
    {
        // 用同一端口先后两次起 listener，模拟 broker 重启
        var first = await TcpListenerFixture.StartAsync();
        var port = first.Port;
        var address = $"127.0.0.1:{port}";
        await first.DisposeAsync();

        var serializer = new MessagePackMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(address, serializer);
        var sample = new TagValue("X", 1, 0xC0, DateTimeOffset.UtcNow);

        // 触发首次失败（进冷却）
        await Assert.ThrowsAnyAsync<Exception>(() => pub.PublishAsync(sample));

        // 端口可能已被系统短暂 TIME_WAIT，等 3s 并起新 listener。SO_REUSEADDR 默认情况下 .NET listener 应能在 Loopback 上重用。
        await Task.Delay(TimeSpan.FromSeconds(3));

        TcpListener? second = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                second = new TcpListener(System.Net.IPAddress.Loopback, port);
                second.Start();
                break;
            }
            catch (SocketException)
            {
                await Task.Delay(1000);
            }
        }
        Assert.NotNull(second);

        try
        {
            // 冷却已过（2s + 3s），下一发应成功 — 不抛
            await pub.PublishAsync(sample);
        }
        finally
        {
            second!.Stop();
        }
    }

    private static bool IsConnectFailure(Exception ex)
    {
        // 第 1 条失败可能是 SocketException 或包了一层 InvalidOperationException
        for (var e = (Exception?)ex; e != null; e = e.InnerException)
        {
            if (e is SocketException) return true;
        }
        // 也接受我们包的"冷却期"消息（如果第 1 条触发瞬间已被前一次失败置入冷却）
        return ex.Message.Contains("冷却期") || ex.Message.Contains("connect", StringComparison.OrdinalIgnoreCase);
    }
}
