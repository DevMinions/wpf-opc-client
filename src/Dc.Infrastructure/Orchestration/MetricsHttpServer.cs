using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dc.Infrastructure.Orchestration;

public sealed record MetricsServerOptions
{
    // 关闭时不监听任何端口（默认开，给 Docker/k8s 探针 + Prometheus 抓取用）。
    public bool Enabled { get; init; } = true;

    // HttpListener 前缀。"+" = 所有网卡；Linux 上 >1024 端口无需特权，Docker 直接可用。
    // Windows 上 "+"/"*" 需要 URL ACL，本地调试可改 "http://localhost:9090/"。
    public string Prefix { get; init; } = "http://+:9090/";
}

// 轻量诊断 HTTP 端点（零额外依赖，纯 net8.0，跨平台）：
//   GET /healthz  → 200（存活探针，进程在跑即 200）
//   GET /readyz   → 200（就绪探针；宿主已启动即就绪）
//   GET /metrics  → 200 Prometheus 文本，指标名与 DiagnosticsReporter 的 Meter 一一对应
//                   （点号按 OpenTelemetry Prometheus 导出器约定转下划线），便于以后切 OTel 不漂移。
// 依赖 Func 快照委托而非具体 orchestrator，便于单测；生产传 orchestrator.GetDiagnostics。
// 作为 IHostedService 由 Generic Host 自动启停。监听失败只记日志、不拖垮宿主。
public sealed class MetricsHttpServer : IHostedService, IDisposable
{
    private readonly Func<IReadOnlyList<TaskDiagnostics>> _provider;
    private readonly MetricsServerOptions _options;
    private readonly ILogger<MetricsHttpServer>? _logger;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MetricsHttpServer(
        Func<IReadOnlyList<TaskDiagnostics>> diagnosticsProvider,
        MetricsServerOptions? options = null,
        ILogger<MetricsHttpServer>? logger = null)
    {
        _provider = diagnosticsProvider;
        _options = options ?? new MetricsServerOptions();
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return Task.CompletedTask;

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(_options.Prefix);
            _listener.Start();
        }
        catch (Exception ex)
        {
            // 端口被占 / URL ACL 缺失等：诊断端点非核心，降级为禁用而不崩采集进程。
            _logger?.LogWarning(ex, "诊断 HTTP 端点启动失败（前缀 {Prefix}），已禁用 /healthz /metrics", _options.Prefix);
            _listener = null;
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _logger?.LogInformation("诊断 HTTP 端点已监听 {Prefix} （/healthz /readyz /metrics）", _options.Prefix);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* 忽略关停竞态 */ }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
            _loop = null;
        }
    }

    // 单连接串行处理：探针/抓取是低频小流量（liveness+readiness+Prometheus 几个周期客户端），
    // 无需并发；慢客户端理论上会阻塞本循环，故 Handle 只做内存渲染+一次写出，不做阻塞 IO。
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { return; } // Stop() 触发的正常退出
            catch (Exception ex) { _logger?.LogDebug(ex, "诊断 HTTP 接受连接失败"); continue; }

            try { Handle(ctx); }
            catch (Exception ex) { _logger?.LogDebug(ex, "诊断 HTTP 处理请求失败"); }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        switch (path)
        {
            case "/healthz":
            case "/readyz":
                Write(ctx, 200, "text/plain; charset=utf-8", "ok");
                break;
            case "/metrics":
                Write(ctx, 200, "text/plain; version=0.0.4; charset=utf-8",
                    RenderPrometheus(_provider(), DateTimeOffset.UtcNow));
                break;
            default:
                Write(ctx, 404, "text/plain; charset=utf-8", "not found");
                break;
        }
    }

    private static void Write(HttpListenerContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        using var os = ctx.Response.OutputStream;
        os.Write(bytes, 0, bytes.Length);
    }

    // 用诊断快照渲染 Prometheus 文本。指标名 = DiagnosticsReporter Meter 名按 OTel 约定转下划线。
    // public static + 显式 now：便于单测（无需起 HttpListener / 控制时钟）。
    public static string RenderPrometheus(IReadOnlyList<TaskDiagnostics> snap, DateTimeOffset now)
    {
        var sb = new StringBuilder(256 + snap.Count * 256);

        Gauge(sb, "dc_collector_up", "Collector 进程存活（1=存活）。",
            g => g.Line(null, 1));
        Gauge(sb, "dc_collector_tasks_running", "运行中的采集任务数。",
            g => g.Line(null, snap.Count));

        Gauge(sb, "dc_collector_task_values", "每任务累计收到的值数。",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.ValueCount); });
        Gauge(sb, "dc_collector_task_publish_errors", "每任务累计发布错误数。",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.PublishErrorCount); });
        Gauge(sb, "dc_collector_task_restarts", "每任务被看门狗重启次数。",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.RestartCount); });
        Gauge(sb, "dc_collector_task_subscribed_tags", "每任务当前订阅的 Tag 数。",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.SubscribedTagCount); });
        Gauge(sb, "dc_collector_task_heartbeat_age_seconds", "每任务距上次心跳的秒数（-1=尚无心跳）。",
            g =>
            {
                foreach (var d in snap)
                    g.Line(d.TaskId, d.LastHeartbeatAt is { } hb ? (now - hb).TotalSeconds : -1d);
            });
        Gauge(sb, "dc_collector_task_queue_pending_bytes", "每任务离线队列未发字节数。",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.QueuePendingBytes); });
        Gauge(sb, "dc_collector_task_dropped_frames", "每任务累计因队列溢出丢弃的帧数。",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.DroppedFrameCount); });

        return sb.ToString();
    }

    // 从 HttpListener 前缀（如 "http://+:9090/"）解析监听端口；解析不出按 9090。
    // public static：HealthCheck（探活模式）与本服务共用一份口径，避免漂移；便于单测。
    public static int ParsePort(string prefix)
    {
        var afterScheme = prefix.IndexOf("://", StringComparison.Ordinal) is var i && i >= 0
            ? prefix[(i + 3)..]
            : prefix;
        var hostPort = afterScheme.TrimEnd('/').Split('/', 2)[0]; // 去掉路径段
        var colon = hostPort.LastIndexOf(':');
        return colon >= 0 && int.TryParse(hostPort[(colon + 1)..], out var p) ? p : 9090;
    }

    private static void Gauge(StringBuilder sb, string name, string help, Action<GaugeWriter> body)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        body(new GaugeWriter(sb, name));
    }

    // 写单个 gauge 样本：可选 task_id 标签 + 数值（InvariantCulture）。
    private readonly struct GaugeWriter(StringBuilder sb, string name)
    {
        public void Line(string? taskId, double value)
        {
            sb.Append(name);
            if (taskId is not null)
                sb.Append("{task_id=\"").Append(Escape(taskId)).Append("\"}");
            sb.Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        // Prometheus 标签值转义：反斜杠、双引号、换行。
        private static string Escape(string s)
            => s.IndexOfAny(['\\', '"', '\n']) < 0
                ? s
                : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }

    public void Dispose()
    {
        try { _listener?.Close(); } catch { }
        _cts?.Dispose();
    }
}
