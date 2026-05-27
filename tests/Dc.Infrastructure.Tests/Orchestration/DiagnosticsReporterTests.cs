using System.Diagnostics.Metrics;
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

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
