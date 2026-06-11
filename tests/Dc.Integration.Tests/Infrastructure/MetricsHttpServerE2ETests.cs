using System.Net;
using System.Net.Sockets;
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Integration.Tests.Infrastructure;

// 诊断 HTTP 端点真起 HttpListener 的端到端：探针 /healthz /readyz、/metrics Prometheus 文本、未知路径 404。
// 绑 127.0.0.1 随机端口（避开 Windows 上 "+"/"*" 通配前缀需要 URL ACL 的限制，CI 跨平台可跑）。
public class MetricsHttpServerE2ETests
{
    private static IReadOnlyList<TaskDiagnostics> Sample() => new[]
    {
        new TaskDiagnostics(
            TaskId: "T1",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            LastValueAt: DateTimeOffset.UtcNow,
            LastHeartbeatAt: DateTimeOffset.UtcNow,
            ValueCount: 5,
            PublishErrorCount: 0,
            RestartCount: 0,
            SubscribedTagCount: 3),
    };

    // 借一个 0 端口拿空闲端口号后立刻释放，再给 HttpListener 用（HttpListener 不支持 0 端口）。
    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    [Fact(Timeout = 15_000)]
    public async Task Endpoints_Serve_Health_Metrics_And_404()
    {
        var port = GetFreePort();
        var server = new MetricsHttpServer(
            Sample, new MetricsServerOptions { Enabled = true, Prefix = $"http://127.0.0.1:{port}/" });

        await server.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(5)
            };

            // 探针：/healthz、/readyz → 200 "ok"
            foreach (var probe in new[] { "healthz", "readyz" })
            {
                using var resp = await http.GetAsync(probe);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("ok", await resp.Content.ReadAsStringAsync());
            }

            // /metrics → 200 Prometheus 文本，含 up=1、运行任务数、带 task_id 维度的样本
            using (var resp = await http.GetAsync("metrics"))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
                var body = await resp.Content.ReadAsStringAsync();
                Assert.Contains("dc_collector_up 1", body);
                Assert.Contains("dc_collector_tasks_running 1", body);
                Assert.Contains("dc_collector_task_values{task_id=\"T1\"} 5", body);
            }

            // 未知路径 → 404
            using (var resp = await http.GetAsync("nope"))
                Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Screenshot_Returns_503_Without_Provider_And_Png_With_Provider()
    {
        // 假 provider 返回固定字节即可验证路由契约，无需 WPF。
        var fakePng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

        var portA = GetFreePort();
        var noProvider = new MetricsHttpServer(
            Sample, new MetricsServerOptions { Enabled = true, Prefix = $"http://127.0.0.1:{portA}/" });
        await noProvider.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{portA}/screenshot");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        }
        finally { await noProvider.StopAsync(CancellationToken.None); noProvider.Dispose(); }

        var portB = GetFreePort();
        var withProvider = new MetricsHttpServer(
            Sample, new MetricsServerOptions { Enabled = true, Prefix = $"http://127.0.0.1:{portB}/" },
            screenshotProvider: () => fakePng);
        await withProvider.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{portB}/screenshot");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
            Assert.Equal(fakePng, await resp.Content.ReadAsByteArrayAsync());
        }
        finally { await withProvider.StopAsync(CancellationToken.None); withProvider.Dispose(); }
    }

    [Fact(Timeout = 15_000)]
    public async Task DebugStress_404_Without_Runner_405_On_Get_And_202_With_Runner()
    {
        // 无 runner → 404
        var portA = GetFreePort();
        var noRunner = new MetricsHttpServer(
            Sample, new MetricsServerOptions { Enabled = true, Prefix = $"http://127.0.0.1:{portA}/" });
        await noRunner.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.PostAsync($"http://127.0.0.1:{portA}/debug/stress?tags=10&hz=5&seconds=1", null);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await noRunner.StopAsync(CancellationToken.None); noRunner.Dispose(); }

        // 有 runner：GET → 405；POST → 202 + started，且 runner 后台收到解析参数
        var tcs = new TaskCompletionSource<(int, int, int)>();
        Func<int, int, int, CancellationToken, Task<long>> runner = (t, h, s, ct) => { tcs.TrySetResult((t, h, s)); return Task.FromResult(123L); };
        var portB = GetFreePort();
        var withRunner = new MetricsHttpServer(
            Sample, new MetricsServerOptions { Enabled = true, Prefix = $"http://127.0.0.1:{portB}/" },
            stressRunner: runner);
        await withRunner.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // GET → 405
            using (var getResp = await http.GetAsync($"http://127.0.0.1:{portB}/debug/stress?tags=10&hz=5&seconds=1"))
                Assert.Equal(HttpStatusCode.MethodNotAllowed, getResp.StatusCode);

            // POST → 202 + started
            using (var resp = await http.PostAsync($"http://127.0.0.1:{portB}/debug/stress?tags=1000&hz=20&seconds=30", null))
            {
                Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
                var body = await resp.Content.ReadAsStringAsync();
                Assert.Contains("\"started\":true", body);
            }

            // 后台 runner 应被调用并收到解析后的参数
            var got = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((1000, 20, 30), got);
        }
        finally { await withRunner.StopAsync(CancellationToken.None); withRunner.Dispose(); }
    }

    [Fact(Timeout = 15_000)]
    public async Task DebugFault_404_NoInjector_405_OnGet_200_WithInjector()
    {
        // 无 injector → 404
        var portA = GetFreePort();
        using (var noInj = new MetricsHttpServer(
            Sample, new MetricsServerOptions { Enabled = true, Prefix = $"http://127.0.0.1:{portA}/" }))
        {
            await noInj.StartAsync(CancellationToken.None);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var r = await http.PostAsync($"http://127.0.0.1:{portA}/debug/fault?task=t1&kind=stall", null);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            await noInj.StopAsync(CancellationToken.None);
        }

        // 有 injector：GET → 405；POST → 200 + injected，且 injector 收到解析参数
        (string Task, string Kind) got = default;
        Func<string, string, bool> injector = (t, k) => { got = (t, k); return true; };
        var portB = GetFreePort();
        using (var withInj = new MetricsHttpServer(
            Sample, new MetricsServerOptions { Enabled = true, Prefix = $"http://127.0.0.1:{portB}/" },
            faultInjector: injector))
        {
            await withInj.StartAsync(CancellationToken.None);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            using (var g = await http.GetAsync($"http://127.0.0.1:{portB}/debug/fault?task=t1&kind=stall"))
                Assert.Equal(HttpStatusCode.MethodNotAllowed, g.StatusCode);

            using (var p = await http.PostAsync($"http://127.0.0.1:{portB}/debug/fault?task=t1&kind=stall", null))
            {
                Assert.Equal(HttpStatusCode.OK, p.StatusCode);
                Assert.Contains("\"injected\":true", await p.Content.ReadAsStringAsync());
            }

            Assert.Equal(("t1", "stall"), got);
            await withInj.StopAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Disabled_DoesNotListen()
    {
        var port = GetFreePort();
        var server = new MetricsHttpServer(
            Sample, new MetricsServerOptions { Enabled = false, Prefix = $"http://127.0.0.1:{port}/" });

        await server.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            // 关闭时不监听 → 请求不成功。失败形态跨平台不同：Linux 立即 connection-refused
            // (HttpRequestException)，Windows 上无快速 RST → 命中超时 (TaskCanceledException)。
            // 两者都证明无服务应答，故只断言「抛异常」而不锁定具体类型。
            await Assert.ThrowsAnyAsync<Exception>(
                () => http.GetAsync($"http://127.0.0.1:{port}/healthz"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }
}
