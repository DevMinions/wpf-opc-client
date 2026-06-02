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
            // 关闭时不监听 → 连接被拒
            await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => http.GetAsync($"http://127.0.0.1:{port}/healthz"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }
}
