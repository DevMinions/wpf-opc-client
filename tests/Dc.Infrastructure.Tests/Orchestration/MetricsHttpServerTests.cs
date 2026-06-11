using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class MetricsHttpServerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-05-28T00:00:00Z");

    [Fact]
    public void Render_NoTasks_EmitsProcessUpAndZeroRunning()
    {
        var text = MetricsHttpServer.RenderPrometheus(Array.Empty<TaskDiagnostics>(), Now);

        Assert.Contains("# TYPE dc_collector_up gauge", text);
        Assert.Contains("dc_collector_up 1", text);
        Assert.Contains("dc_collector_tasks_running 0", text);
        // 无任务时不应有带标签的样本行
        Assert.DoesNotContain("task_id=", text);
    }

    [Fact]
    public void Render_WithTasks_EmitsLabeledSamplesAndHeartbeatAge()
    {
        var tasks = new[]
        {
            new TaskDiagnostics(
                TaskId: "T1",
                StartedAt: Now.AddMinutes(-10),
                LastValueAt: Now.AddSeconds(-1),
                LastHeartbeatAt: Now.AddSeconds(-5),
                ValueCount: 42,
                PublishErrorCount: 3,
                RestartCount: 1,
                SubscribedTagCount: 7,
                QueuePendingBytes: 8192,
                DroppedFrameCount: 11),
            // 尚无心跳 → 心跳龄 -1
            new TaskDiagnostics("T2", Now, null, null, 0, 0, 0, 0),
        };

        var text = MetricsHttpServer.RenderPrometheus(tasks, Now);

        Assert.Contains("dc_collector_tasks_running 2", text);
        Assert.Contains("dc_collector_task_values{task_id=\"T1\"} 42", text);
        Assert.Contains("dc_collector_task_publish_errors{task_id=\"T1\"} 3", text);
        Assert.Contains("dc_collector_task_restarts{task_id=\"T1\"} 1", text);
        Assert.Contains("dc_collector_task_subscribed_tags{task_id=\"T1\"} 7", text);
        Assert.Contains("dc_collector_task_heartbeat_age_seconds{task_id=\"T1\"} 5", text);
        Assert.Contains("dc_collector_task_heartbeat_age_seconds{task_id=\"T2\"} -1", text);
        Assert.Contains("dc_collector_task_queue_pending_bytes{task_id=\"T1\"} 8192", text);
        Assert.Contains("dc_collector_task_dropped_frames{task_id=\"T1\"} 11", text);
    }

    [Fact]
    public void RenderPrometheus_WithLiveFlushStats_EmitsLiveGauges()
    {
        var live = new LiveFlushStats(P50Ms: 3.0, P95Ms: 12.0, CoalesceRatio: 9.5, Rows: 1000, UpdatesPerSecond: 9800);
        var text = MetricsHttpServer.RenderPrometheus(Array.Empty<TaskDiagnostics>(), Now, live);

        Assert.Contains("# TYPE dc_livedata_flush_ms_p50 gauge", text);
        Assert.Contains("dc_livedata_flush_ms_p50 3", text);
        Assert.Contains("dc_livedata_flush_ms_p95 12", text);
        Assert.Contains("dc_livedata_coalesce_ratio 9.5", text);
        Assert.Contains("dc_livedata_rows 1000", text);
        Assert.Contains("dc_livedata_updates_per_second 9800", text);
    }

    [Fact]
    public void RenderPrometheus_WithoutLiveFlushStats_OmitsLiveGauges()
    {
        var text = MetricsHttpServer.RenderPrometheus(Array.Empty<TaskDiagnostics>(), Now);

        Assert.DoesNotContain("dc_livedata_", text);
    }

    [Fact]
    public void Render_EscapesLabelValues()
    {
        var tasks = new[] { new TaskDiagnostics("a\"b\\c", Now, null, null, 1, 0, 0, 0) };

        var text = MetricsHttpServer.RenderPrometheus(tasks, Now);

        Assert.Contains("task_id=\"a\\\"b\\\\c\"", text);
    }

    [Theory]
    [InlineData("http://+:9090/", 9090)]          // 全网卡前缀
    [InlineData("http://localhost:8080/", 8080)]  // 具名主机
    [InlineData("http://*:1234/metrics/", 1234)]  // 带路径段
    [InlineData("http://127.0.0.1:5000", 5000)]   // 无尾斜杠
    [InlineData("http://+/", 9090)]               // 无端口 → 兜底 9090
    [InlineData("garbage", 9090)]                 // 解析不出 → 兜底 9090
    public void ParsePort_ExtractsPortOrFallsBack(string prefix, int expected)
        => Assert.Equal(expected, MetricsHttpServer.ParsePort(prefix));
}
