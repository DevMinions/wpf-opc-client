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
    // 可选截图 provider：返回 PNG 字节，null 表示当前不可用 → /screenshot 给 503。
    // 用 byte[] 而非 WPF 类型，桌面端（Dc.App）注入 RenderTargetBitmap 实现，
    // 无头端（Dc.Cli）传 null —— Infrastructure 因此保持零 WPF 依赖、跨平台不变。
    private readonly Func<byte[]?>? _screenshotProvider;
    // 可选 LiveData flush 指标 provider：App VM 填充，无头端传 null。
    // 仅经 /metrics 暴露、不镜像到任何 Meter（UI 侧指标无 OTel 消费方）。
    private readonly Func<LiveFlushStats?>? _liveFlushProvider;
    // 可选压测 runner：(tags,hz,seconds)->injected。null → /debug/stress 走 404（默认不暴露）。
    // App 侧仅在 DC_DEBUG_STRESS=1 时注入，沿用 screenshot「provider 存在即启用」门控。
    private readonly Func<int, int, int, CancellationToken, Task<long>>? _stressRunner;
    // 可选故障注入器：(taskId,kind)->hit。null → /debug/fault 走 404（默认不暴露）。
    // App 侧仅在 DC_DEBUG_STRESS=1 时注入，沿用「provider 存在即启用」门控。
    private readonly Func<string, string, bool>? _faultInjector;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MetricsHttpServer(
        Func<IReadOnlyList<TaskDiagnostics>> diagnosticsProvider,
        MetricsServerOptions? options = null,
        ILogger<MetricsHttpServer>? logger = null,
        Func<byte[]?>? screenshotProvider = null,
        Func<LiveFlushStats?>? liveFlushProvider = null,
        Func<int, int, int, CancellationToken, Task<long>>? stressRunner = null,
        Func<string, string, bool>? faultInjector = null)
    {
        _provider = diagnosticsProvider;
        _options = options ?? new MetricsServerOptions();
        _logger = logger;
        _screenshotProvider = screenshotProvider;
        _liveFlushProvider = liveFlushProvider;
        _stressRunner = stressRunner;
        _faultInjector = faultInjector;
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
            _logger?.LogWarning(ex, "Diagnostics HTTP endpoint failed to start (prefix {Prefix}); /healthz /metrics disabled", _options.Prefix);
            _listener = null;
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _logger?.LogInformation("Diagnostics HTTP endpoint listening on {Prefix} (/healthz /readyz /metrics{Shot}{Stress}{Fault})",
            _options.Prefix,
            _screenshotProvider is not null ? " /screenshot" : "",
            _stressRunner is not null ? " /debug/stress" : "",
            _faultInjector is not null ? " /debug/fault" : "");
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
            catch (Exception ex) { _logger?.LogDebug(ex, "Diagnostics HTTP accept failed"); continue; }

            try { Handle(ctx); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Diagnostics HTTP request handling failed"); }
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
                    RenderPrometheus(_provider(), DateTimeOffset.UtcNow, _liveFlushProvider?.Invoke()));
                break;
            case "/screenshot":
                // 调试用后台截图：进程内渲染主窗口为 PNG（不依赖物理屏幕，遮挡/最小化也可）。
                // provider 为空（无头端）或渲染失败返回 503，不影响其它端点。
                var png = _screenshotProvider?.Invoke();
                if (png is null || png.Length == 0)
                    Write(ctx, 503, "text/plain; charset=utf-8", "screenshot unavailable");
                else
                    WriteBytes(ctx, 200, "image/png", png);
                break;
            case "/debug/stress":
                if (_stressRunner is null) { Write(ctx, 404, "text/plain; charset=utf-8", "not found"); break; }
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, "text/plain; charset=utf-8", "method not allowed"); break; }
                var qs = ctx.Request.QueryString;
                var tags = ParseInt(qs["tags"], 1000);
                var hz = ParseInt(qs["hz"], 10);
                var seconds = ParseInt(qs["seconds"], 30);
                // 后台运行、立即 202：避免阻塞单连接串行循环，压测期间 /metrics /healthz 仍可服务（dc-remote 边压边抓）。
                // 调试端点，后台 Task 的异常吞掉不外抛（不影响诊断服务）。压测结果经 /metrics 的 dc_livedata_* 读取。
                _ = _stressRunner(tags, hz, seconds, _cts?.Token ?? CancellationToken.None).ContinueWith(
                    t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                Write(ctx, 202, "application/json; charset=utf-8",
                    $"{{\"started\":true,\"tags\":{tags},\"hz\":{hz},\"seconds\":{seconds}}}");
                break;
            case "/debug/fault":
                if (_faultInjector is null) { Write(ctx, 404, "text/plain; charset=utf-8", "not found"); break; }
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, "text/plain; charset=utf-8", "method not allowed"); break; }
                var fq = ctx.Request.QueryString;
                var ftask = EscapeJson(fq["task"] ?? "");
                var fkind = EscapeJson(fq["kind"] ?? "stall");
                var hit = _faultInjector(ftask, fkind);
                Write(ctx, 200, "application/json; charset=utf-8",
                    $"{{\"injected\":{(hit ? "true" : "false")},\"task\":\"{ftask}\",\"kind\":\"{fkind}\"}}");
                break;
            default:
                Write(ctx, 404, "text/plain; charset=utf-8", "not found");
                break;
        }
    }

    // query 整数解析：缺省/非法/非正 → def（压测参数都要 > 0）。
    private static int ParseInt(string? s, int def) => int.TryParse(s, out var v) && v > 0 ? v : def;

    // 最小 JSON 字符串转义：反斜杠 → 引号 → 换行/回车/Tab。供 /debug/fault 回显 query 值。
    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    private static void Write(HttpListenerContext ctx, int status, string contentType, string body)
        => WriteBytes(ctx, status, contentType, Encoding.UTF8.GetBytes(body));

    private static void WriteBytes(HttpListenerContext ctx, int status, string contentType, byte[] bytes)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        using var os = ctx.Response.OutputStream;
        os.Write(bytes, 0, bytes.Length);
    }

    // 用诊断快照渲染 Prometheus 文本。指标名 = DiagnosticsReporter Meter 名按 OTel 约定转下划线。
    // public static + 显式 now：便于单测（无需起 HttpListener / 控制时钟）。
    public static string RenderPrometheus(IReadOnlyList<TaskDiagnostics> snap, DateTimeOffset now,
        LiveFlushStats? live = null)
    {
        var sb = new StringBuilder(256 + snap.Count * 256);

        Gauge(sb, "dc_collector_up", "Collector process alive (1=alive)",
            g => g.Line(null, 1));
        Gauge(sb, "dc_collector_tasks_running", "Number of running collection tasks",
            g => g.Line(null, snap.Count));

        Gauge(sb, "dc_collector_task_values", "Cumulative values received per task",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.ValueCount); });
        Gauge(sb, "dc_collector_task_publish_errors", "Cumulative publish errors per task",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.PublishErrorCount); });
        Gauge(sb, "dc_collector_task_restarts", "Watchdog restarts per task",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.RestartCount); });
        Gauge(sb, "dc_collector_task_subscribed_tags", "Currently subscribed tags per task",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.SubscribedTagCount); });
        Gauge(sb, "dc_collector_task_heartbeat_age_seconds", "Seconds since last heartbeat per task (-1=no heartbeat yet)",
            g =>
            {
                foreach (var d in snap)
                    g.Line(d.TaskId, d.LastHeartbeatAt is { } hb ? (now - hb).TotalSeconds : -1d);
            });
        Gauge(sb, "dc_collector_task_queue_pending_bytes", "Offline queue pending (unsent) bytes per task",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.QueuePendingBytes); });
        Gauge(sb, "dc_collector_task_dropped_frames", "Cumulative frames dropped due to queue overflow per task",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.DroppedFrameCount); });
        Gauge(sb, "dc_collector_task_state",
            "Per-task connection state (state label: connecting/running/restarting/faulted, value always 1=current)",
            g =>
            {
                foreach (var d in snap)
                    g.LineKv(("task_id", d.TaskId), ("state", d.State.ToString().ToLowerInvariant()));
            });

        // LiveData flush（仅 /metrics 暴露，无 Meter 镜像——UI 侧指标无 OTel 消费方，
        // 是对项目「双路径」约定的有意例外；双路径只约束 collector 任务指标）。
        if (live is not null)
        {
            Gauge(sb, "dc_livedata_flush_ms_p50", "LiveData flush time p50 (ms)", g => g.Line(null, live.P50Ms));
            Gauge(sb, "dc_livedata_flush_ms_p95", "LiveData flush time p95 (ms)", g => g.Line(null, live.P95Ms));
            Gauge(sb, "dc_livedata_coalesce_ratio", "LiveData coalesce ratio (raw inputs / output keys; higher=denser)", g => g.Line(null, live.CoalesceRatio));
            Gauge(sb, "dc_livedata_rows", "LiveData current row count", g => g.Line(null, live.Rows));
            Gauge(sb, "dc_livedata_updates_per_second", "LiveData raw updates per second", g => g.Line(null, live.UpdatesPerSecond));
        }

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

        // 写多标签样本（值恒 1）：用于「枚举状态当前态」类指标，如 dc_collector_task_state。
        // 标签值复用 Line 的同一 Escape，转义口径不漂移。
        public void LineKv(params (string Key, string Value)[] labels)
        {
            sb.Append(name).Append('{');
            for (var i = 0; i < labels.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(labels[i].Key).Append("=\"").Append(Escape(labels[i].Value)).Append('"');
            }
            sb.Append("} 1\n");
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
