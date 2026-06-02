using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dc.Infrastructure.Orchestration;

public sealed record DiagnosticsReporterOptions
{
    // 周期结构化诊断日志的间隔；<= 0 则不开日志循环。
    public TimeSpan ReportInterval { get; init; } = TimeSpan.FromSeconds(30);
    // 周期结构化日志（给没有指标抓取器的运维看）。
    public bool EnableLogging { get; init; } = true;
    // System.Diagnostics.Metrics 仪表（可被 dotnet-counters / OpenTelemetry 抓取）。
    public bool EnableMetrics { get; init; } = true;

    public const string MeterName = "Dc.Collector";
}

// 把 TaskOrchestrator 的运行诊断对外可观测：
//   1) System.Diagnostics.Metrics 拉取式仪表（零依赖，dotnet-counters/OTel 可抓），无需 HTTP server；
//   2) 周期结构化诊断日志。
// 依赖 Func 快照委托而非具体 orchestrator，便于单测；生产传 orchestrator.GetDiagnostics。
// 作为 IHostedService 由 Generic Host 自动启停（WPF 与无头 Cli 共用）。
public sealed class DiagnosticsReporter : IHostedService, IAsyncDisposable
{
    private readonly Func<IReadOnlyList<TaskDiagnostics>> _provider;
    private readonly DiagnosticsReporterOptions _options;
    private readonly ILogger<DiagnosticsReporter>? _logger;
    private readonly CancellationTokenSource _cts = new();
    private Meter? _meter;
    private Task? _logTask;
    private bool _disposed;

    public DiagnosticsReporter(
        Func<IReadOnlyList<TaskDiagnostics>> diagnosticsProvider,
        DiagnosticsReporterOptions? options = null,
        ILogger<DiagnosticsReporter>? logger = null)
    {
        _provider = diagnosticsProvider;
        _options = options ?? new DiagnosticsReporterOptions();
        _logger = logger;

        // 指标在构造时即注册（拉取式，宿主持有单例即保活）；日志循环在 StartAsync 才起。
        if (_options.EnableMetrics) SetupMetrics();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.EnableLogging && _options.ReportInterval > TimeSpan.Zero && _logTask is null)
            _logTask = Task.Run(() => LogLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        if (_logTask is not null)
        {
            try { await _logTask.ConfigureAwait(false); } catch { }
            _logTask = null;
        }
    }

    private void SetupMetrics()
    {
        _meter = new Meter(DiagnosticsReporterOptions.MeterName);

        // 进程存活恒为 1（与 /metrics 的 dc_collector_up 对齐，两条抓取路径指标集不漂移）。
        _meter.CreateObservableGauge("dc.collector.up",
            () => 1L, unit: "{up}", description: "Collector 进程存活（1=存活）");

        _meter.CreateObservableGauge("dc.collector.tasks.running",
            () => _provider().Count, unit: "{tasks}", description: "运行中的采集任务数");

        _meter.CreateObservableGauge("dc.collector.task.values",
            () => Each(d => d.ValueCount), unit: "{values}", description: "每任务累计收到的值数");

        _meter.CreateObservableGauge("dc.collector.task.publish_errors",
            () => Each(d => d.PublishErrorCount), unit: "{errors}", description: "每任务累计发布错误数");

        _meter.CreateObservableGauge("dc.collector.task.restarts",
            () => Each(d => (long)d.RestartCount), unit: "{restarts}", description: "每任务被看门狗重启次数");

        _meter.CreateObservableGauge("dc.collector.task.subscribed_tags",
            () => Each(d => (long)d.SubscribedTagCount), unit: "{tags}", description: "每任务当前订阅的 Tag 数");

        _meter.CreateObservableGauge("dc.collector.task.heartbeat_age_seconds",
            ObserveHeartbeatAge, unit: "s", description: "每任务距上次心跳的秒数（-1=尚无心跳）");
    }

    // 每任务一个 Measurement，带 task.id 维度标签
    private IEnumerable<Measurement<long>> Each(Func<TaskDiagnostics, long> selector)
        => _provider().Select(d => new Measurement<long>(
            selector(d), new KeyValuePair<string, object?>("task.id", d.TaskId)));

    private IEnumerable<Measurement<double>> ObserveHeartbeatAge()
    {
        var now = DateTimeOffset.UtcNow;
        return _provider().Select(d => new Measurement<double>(
            d.LastHeartbeatAt is { } hb ? (now - hb).TotalSeconds : -1d,
            new KeyValuePair<string, object?>("task.id", d.TaskId)));
    }

    private async Task LogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.ReportInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { LogOnce(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "诊断日志输出失败"); }
        }
    }

    // 输出一轮结构化诊断（每任务一条）。public 便于宿主在关停前主动落一笔，也便于测试。
    public void LogOnce()
    {
        if (_logger is null) return;
        var now = DateTimeOffset.UtcNow;
        var snap = _provider();
        if (snap.Count == 0)
        {
            _logger.LogInformation("诊断：当前无运行任务");
            return;
        }
        foreach (var d in snap)
        {
            var hbAge = d.LastHeartbeatAt is { } hb ? $"{(now - hb).TotalSeconds:F0}" : "—";
            _logger.LogInformation(
                "诊断 task={TaskId} 运行={UpSeconds:F0}s 值={Values} 发布错误={PublishErrors} 重启={Restarts} 订阅Tag={Tags} 心跳龄={HeartbeatAge}s",
                d.TaskId, (now - d.StartedAt).TotalSeconds, d.ValueCount, d.PublishErrorCount,
                d.RestartCount, d.SubscribedTagCount, hbAge);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        if (_logTask is not null)
        {
            try { await _logTask.ConfigureAwait(false); } catch { }
        }
        _meter?.Dispose();
        _cts.Dispose();
    }
}
