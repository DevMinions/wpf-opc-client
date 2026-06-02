using System.Diagnostics.Metrics;
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

// 捕获日志级别+渲染文本，供边沿日志断言。
file sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        => Entries.Add((level, formatter(state, ex)));
}

public class DiagnosticsReporterTests
{
    private static TaskDiagnostics Diag(string id, long values, long pubErr, int restarts, int tags, DateTimeOffset? hb)
        => new(id, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, hb, values, pubErr, restarts, tags);

    // 指标反映诊断快照：tasks.running 总数；每任务 values/restarts/subscribed_tags 带 task.id 维度。
    [Fact]
    public async Task Metrics_ReflectDiagnosticsSnapshot()
    {
        IReadOnlyList<TaskDiagnostics> snapshot = new[]
        {
            Diag("task-A", values: 100, pubErr: 2, restarts: 1, tags: 3, hb: DateTimeOffset.UtcNow),
            Diag("task-B", values: 50,  pubErr: 0, restarts: 0, tags: 7, hb: DateTimeOffset.UtcNow),
        };
        await using var reporter = new DiagnosticsReporter(
            () => snapshot,
            new DiagnosticsReporterOptions { EnableLogging = false });   // 只测指标，不开日志循环

        var recorded = new List<(string Name, double Value, string? TaskId)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == DiagnosticsReporterOptions.MeterName)
                    l.EnableMeasurementEvents(inst);
            }
        };
        void Record<T>(Instrument inst, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags) where T : struct
        {
            string? taskId = null;
            foreach (var t in tags)
                if (t.Key == "task.id") taskId = t.Value as string;
            recorded.Add((inst.Name, Convert.ToDouble(value), taskId));
        }
        listener.SetMeasurementEventCallback<int>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        listener.Start();

        listener.RecordObservableInstruments();

        Assert.Contains(recorded, m => m.Name == "dc.collector.tasks.running" && m.Value == 2);
        Assert.Contains(recorded, m => m.Name == "dc.collector.task.values" && m.TaskId == "task-A" && m.Value == 100);
        Assert.Contains(recorded, m => m.Name == "dc.collector.task.publish_errors" && m.TaskId == "task-A" && m.Value == 2);
        Assert.Contains(recorded, m => m.Name == "dc.collector.task.restarts" && m.TaskId == "task-A" && m.Value == 1);
        Assert.Contains(recorded, m => m.Name == "dc.collector.task.subscribed_tags" && m.TaskId == "task-B" && m.Value == 7);
    }

    [Fact]
    public async Task Metrics_IncludeQueuePendingAndDroppedFrames()
    {
        IReadOnlyList<TaskDiagnostics> snapshot = new[]
        {
            new TaskDiagnostics("task-A", DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 100, 0, 0, 3,
                QueuePendingBytes: 2048, DroppedFrameCount: 9),
        };
        await using var reporter = new DiagnosticsReporter(
            () => snapshot, new DiagnosticsReporterOptions { EnableLogging = false });

        var recorded = new List<(string Name, double Value, string? TaskId)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == DiagnosticsReporterOptions.MeterName) l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            string? taskId = null;
            foreach (var t in tags) if (t.Key == "task.id") taskId = t.Value as string;
            recorded.Add((inst.Name, value, taskId));
        });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Contains(recorded, r => r.Name == "dc.collector.task.queue_pending_bytes" && r.TaskId == "task-A" && r.Value == 2048);
        Assert.Contains(recorded, r => r.Name == "dc.collector.task.dropped_frames" && r.TaskId == "task-A" && r.Value == 9);
    }

    [Fact]
    public void LogOnce_EdgeLogsDropStartAndStop()
    {
        var dropped = 0L;
        var logger = new CapturingLogger<DiagnosticsReporter>();
        var reporter = new DiagnosticsReporter(
            () => new[] { new TaskDiagnostics("t1", DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 0, 0, 1, DroppedFrameCount: dropped) },
            new DiagnosticsReporterOptions { EnableLogging = true }, logger);

        dropped = 0; reporter.LogOnce();   // tick1：未丢，无边沿
        dropped = 5; reporter.LogOnce();   // tick2：开始丢 → WARN
        dropped = 5; reporter.LogOnce();   // tick3：停止丢 → INFO

        Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("开始丢弃"));
        Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information && e.Message.Contains("停止丢弃"));
    }

    // 无运行任务：tasks.running == 0，不抛
    [Fact]
    public async Task Metrics_NoTasks_ReportsZeroRunning()
    {
        await using var reporter = new DiagnosticsReporter(
            Array.Empty<TaskDiagnostics>,
            new DiagnosticsReporterOptions { EnableLogging = false });

        double? running = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Name == "dc.collector.tasks.running") l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<int>((i, v, t, _) => running = v);
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Equal(0d, running);
    }
}
