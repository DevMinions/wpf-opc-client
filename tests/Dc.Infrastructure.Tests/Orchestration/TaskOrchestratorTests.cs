using Dc.Infrastructure.Orchestration;
using Dc.Infrastructure.Tests.Fakes;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

// 看门狗/心跳计时敏感：独占执行（不与其他测试类并行争 CPU），避免 CI 上 flaky。
[Collection("Timing-Sensitive")]
public class TaskOrchestratorTests
{
    private static (TaskOrchestrator orch, FakeOpcSubscriberFactory daFactory, FakePublisherFactory pubFactory) Build(OrchestratorOptions? opts = null)
    {
        var daFactory = new FakeOpcSubscriberFactory(OpcProtocol.Da);
        var pubFactory = new FakePublisherFactory();
        var orch = new TaskOrchestrator(new[] { (IOpcSubscriberFactory)daFactory }, pubFactory, opts);
        return (orch, daFactory, pubFactory);
    }

    private static TaskStartRequest Request(string taskId = "t1", params TagDescriptor[] tags)
    {
        return new TaskStartRequest(
            taskId,
            OpcProtocol.Da,
            new OpcConnectionOptions { ServerUri = "opc.tcp://localhost:4840" },
            "127.0.0.1:5000",
            tags.Length == 0 ? Array.Empty<TagDescriptor>() : tags);
    }

    private static TaskStartRequest RequestWithTransform(
        string taskId, TransformConfig cfg, params TagDescriptor[] tags) =>
        new(taskId, OpcProtocol.Da,
            new OpcConnectionOptions { ServerUri = "opc.tcp://localhost:4840" },
            "127.0.0.1:5000",
            tags.Length == 0 ? Array.Empty<TagDescriptor>() : tags,
            cfg);

    private static TaskOrchestrator BuildWithTransformFactory(
        out FakeOpcSubscriberFactory daFactory, out FakePublisherFactory pubFactory)
    {
        daFactory = new FakeOpcSubscriberFactory(OpcProtocol.Da);
        pubFactory = new FakePublisherFactory();
        return new TaskOrchestrator(
            new[] { (IOpcSubscriberFactory)daFactory },
            pubFactory,
            options: null,
            logger: null,
            transformFactory: new TagValueTransformFactory());
    }

    [Fact]
    public async Task StartAsync_CreatesSubscriberAndPublisher_SubscribesAllTags()
    {
        var (orch, daFactory, pubFactory) = Build();
        await using var _ = orch;

        var tags = new[] { new TagDescriptor("t1-a", "A", 1), new TagDescriptor("t1-b", "B", 1) };
        await orch.StartAsync(Request("t1", tags));

        Assert.Single(daFactory.Created);
        var sub = daFactory.Created.First();
        Assert.Equal(1, sub.ConnectCalls);
        Assert.Equal(tags, sub.Subscribed);
        Assert.Single(pubFactory.Created);
        Assert.Equal("127.0.0.1:5000", pubFactory.Created.First().Address);
        Assert.Contains("t1", orch.RunningTaskIds);
    }

    [Fact]
    public async Task StartAsync_TaskValuesFlowToPublisher()
    {
        var (orch, daFactory, pubFactory) = Build();
        await using var _ = orch;

        await orch.StartAsync(Request("t1", new TagDescriptor("id", "A", 1)));
        var sub = daFactory.Created.First();
        var pub = pubFactory.Created.First().Publisher;

        var v = new TagValue("A", 42.5, 0xC0, DateTimeOffset.UtcNow);
        sub.EmitValue(v);

        await WaitForAsync(() => pub.Published.Count >= 1);
        Assert.Equal(v, pub.Published.First());
    }

    [Fact]
    public async Task StartAsync_WithScale_PublishesEngineeringValue()
    {
        await using var orch = BuildWithTransformFactory(out var daFactory, out var pubFactory);

        var cfg = new TransformConfig(
            new Dictionary<string, ScaleConfig> { ["t1"] = new(0.1, 0) },
            new Dictionary<string, string> { ["t1"] = "A" },
            Array.Empty<FormulaConfig>());
        await orch.StartAsync(RequestWithTransform("t1", cfg, new TagDescriptor("t1", "A", 6)));
        var sub = daFactory.Created.First();
        var pub = pubFactory.Created.First().Publisher;

        sub.EmitValue(new TagValue("A", 255.0, 0xC0, DateTimeOffset.UtcNow));

        await WaitForAsync(() => pub.Published.Count >= 1);
        var published = Assert.IsType<TagValue>(pub.Published.First());
        Assert.Equal(25.5, published.Value);
    }

    [Fact]
    public async Task StartAsync_VirtualTagNotInSubscriberList()
    {
        await using var orch = BuildWithTransformFactory(out var daFactory, out var pubFactory);

        var cfg = new TransformConfig(
            new Dictionary<string, ScaleConfig> { ["t1"] = new(null, null) },
            new Dictionary<string, string> { ["t1"] = "A" },
            new[] { new FormulaConfig("f1", "OUT", "A*2", new[] { new FormulaInputConfig("A", "t1") }) });
        await orch.StartAsync(RequestWithTransform("t1", cfg, new TagDescriptor("t1", "A", 6)));
        var sub = daFactory.Created.First();
        var pub = pubFactory.Created.First().Publisher;

        Assert.Single(sub.Subscribed);
        Assert.Equal("A", sub.Subscribed[0].Item);

        sub.EmitValue(new TagValue("A", 10.0, 0xC0, DateTimeOffset.UtcNow));
        await WaitForAsync(() => pub.Published.Count >= 2);
        var published = pub.Published.OfType<TagValue>().ToArray();
        Assert.Contains(published, v => v.Item == "A" && (double)v.Value! == 10.0);
        Assert.Contains(published, v => v.Item == "OUT" && (double)v.Value! == 20.0);
    }

    [Fact]
    public async Task RemoveTagsAsync_StopsVirtualOutput_WhenInputRemoved()
    {
        await using var orch = BuildWithTransformFactory(out var daFactory, out var pubFactory);

        var cfg = new TransformConfig(
            new Dictionary<string, ScaleConfig> { ["t1"] = new(null, null), ["t2"] = new(null, null) },
            new Dictionary<string, string> { ["t1"] = "A", ["t2"] = "B" },
            new[] { new FormulaConfig("f1", "OUT", "A+B",
                new[] { new FormulaInputConfig("A", "t1"), new FormulaInputConfig("B", "t2") }) });
        await orch.StartAsync(RequestWithTransform("t1", cfg,
            new TagDescriptor("t1", "A", 6), new TagDescriptor("t2", "B", 6)));
        var sub = daFactory.Created.First();
        var pub = pubFactory.Created.First().Publisher;

        sub.EmitValue(new TagValue("A", 1.0, 0xC0, DateTimeOffset.UtcNow));
        sub.EmitValue(new TagValue("B", 2.0, 0xC0, DateTimeOffset.UtcNow));
        await WaitForAsync(() => pub.Published.OfType<TagValue>().Any(v => v.Item == "OUT"));

        var outCountBefore = pub.Published.OfType<TagValue>().Count(v => v.Item == "OUT");

        await orch.RemoveTagsAsync("t1", new[] { "B" });

        sub.EmitValue(new TagValue("A", 5.0, 0xC0, DateTimeOffset.UtcNow));
        await Task.Delay(100);
        var outCountAfter = pub.Published.OfType<TagValue>().Count(v => v.Item == "OUT");
        Assert.Equal(outCountBefore, outCountAfter);
    }

    [Fact]
    public async Task StartAsync_SameTaskIdTwice_ReplacesPreviousRuntime()
    {
        var (orch, daFactory, _) = Build();
        await using var _d = orch;

        await orch.StartAsync(Request("t1"));
        await orch.StartAsync(Request("t1"));

        var subs = daFactory.Created.ToArray();
        Assert.Equal(2, subs.Length);
        Assert.True(subs[0].Disposed);
        Assert.False(subs[1].Disposed);
    }

    [Fact]
    public async Task StopAsync_DisposesSubscriberAndPublisher()
    {
        var (orch, daFactory, pubFactory) = Build();
        await using var _ = orch;

        await orch.StartAsync(Request("t1"));
        await orch.StopAsync("t1");

        Assert.True(daFactory.Created.First().Disposed);
        Assert.True(pubFactory.Created.First().Publisher.Disposed);
        Assert.DoesNotContain("t1", orch.RunningTaskIds);
    }

    [Fact]
    public async Task StopAsync_DrainsBufferedValues_BeforeDisposingPublisher()
    {
        // 回归 #5：停止时先 Dispose 订阅器（completes channel）再让 pipeline drain 残余值，
        // 不应丢弃通道里已收未发的值。用慢 publisher 让值在通道积压。
        var (orch, daFactory, pubFactory) = Build();
        await using var _ = orch;

        await orch.StartAsync(Request("t1"));
        var sub = daFactory.Created.First();
        var pub = pubFactory.Created.First().Publisher;
        pub.PublishDelay = TimeSpan.FromMilliseconds(20); // 制造积压

        const int n = 10;
        for (int i = 0; i < n; i++)
            sub.EmitValue(new TagValue($"V{i}", i, 0xC0, DateTimeOffset.UtcNow));

        await orch.StopAsync("t1"); // 优雅 drain：10 个应全部发出

        Assert.Equal(n, pub.Published.Count);
        Assert.True(pub.Disposed);
    }

    [Fact]
    public async Task Watchdog_DoesNotResurrect_TaskStoppedByUser()
    {
        // 回归 #7：看门狗按锁内重新校验存在性，用户已 StopAsync 的任务不应被复活。
        var opts = new OrchestratorOptions
        {
            WatchdogInterval = TimeSpan.FromMilliseconds(20),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(30)
        };
        var (orch, daFactory, _) = Build(opts);
        await using var _ = orch;

        await orch.StartAsync(Request("t1")); // 无心跳 → 很快 stale
        await orch.StopAsync("t1");           // 用户显式停止
        var createdAfterStop = daFactory.Created.Count;

        // 看门狗持续跑；已停任务不应复活、不应再造订阅器
        await Task.Delay(300);

        Assert.DoesNotContain("t1", orch.RunningTaskIds);
        Assert.Equal(createdAfterStop, daFactory.Created.Count);
    }

    [Fact]
    public async Task AddTagsAsync_CallsSubscribeWithNewTagsOnly()
    {
        var (orch, daFactory, _) = Build();
        await using var _d = orch;

        var initial = new TagDescriptor("id1", "A", 1);
        await orch.StartAsync(Request("t1", initial));
        var sub = daFactory.Created.First();

        var added = new[] { new TagDescriptor("id2", "B", 1), initial };
        Assert.True(await orch.AddTagsAsync("t1", added));

        Assert.Equal(2, sub.Subscribed.Count);
        Assert.Equal("A", sub.Subscribed[0].Item);
        Assert.Equal("B", sub.Subscribed[1].Item);
    }

    [Fact]
    public async Task RemoveTagsAsync_CallsUnsubscribeOnExistingTagsOnly()
    {
        var (orch, daFactory, _) = Build();
        await using var _d = orch;

        await orch.StartAsync(Request("t1",
            new TagDescriptor("id1", "A", 1),
            new TagDescriptor("id2", "B", 1)));
        var sub = daFactory.Created.First();

        Assert.True(await orch.RemoveTagsAsync("t1", new[] { "A", "DOES_NOT_EXIST" }));

        Assert.Single(sub.Unsubscribed);
        Assert.Equal("A", sub.Unsubscribed[0]);
    }

    [Fact]
    public async Task AddTagsAsync_UnknownTaskId_ReturnsFalse()
    {
        var (orch, _, _) = Build();
        await using var _d = orch;
        Assert.False(await orch.AddTagsAsync("nope", new[] { new TagDescriptor("x", "x", 1) }));
    }

    [Fact]
    public async Task Watchdog_RestartsTask_WhenHeartbeatStale()
    {
        var opts = new OrchestratorOptions
        {
            WatchdogInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(100)
        };
        var (orch, daFactory, _) = Build(opts);
        await using var _ = orch;

        await orch.StartAsync(Request("t1"));
        Assert.Single(daFactory.Created);

        await WaitForAsync(() => daFactory.Created.Count >= 2, TimeSpan.FromSeconds(2));
        Assert.True(daFactory.Created.Count >= 2);
    }

    [Fact]
    public async Task Watchdog_DoesNotRestart_WhenHeartbeatsFresh()
    {
        // emit 间隔(100ms)远小于 timeout（15x 余量），避免 CI 共享 runner 调度抖动误判超时；
        // 本类已 [Collection("Timing-Sensitive")] 独占执行消除并行争用；总时长(2s) > timeout(1.5s)
        // 仍能验证「有心跳不重启」的语义（看门狗在超时horizon之后仍跑过，但因心跳新鲜未重启）。
        var opts = new OrchestratorOptions
        {
            WatchdogInterval = TimeSpan.FromMilliseconds(100),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(1500)
        };
        var (orch, daFactory, _) = Build(opts);
        await using var _ = orch;

        await orch.StartAsync(Request("t1"));
        var sub = daFactory.Created.First();

        // 持续心跳 2.0 s（> timeout 1.5s），应不重启
        for (int i = 0; i < 20; i++)
        {
            sub.EmitHeartbeat(new HeartBeat("t1", DateTimeOffset.UtcNow));
            await Task.Delay(100);
        }

        Assert.Single(daFactory.Created);
    }

    [Fact]
    public async Task StartAsync_ConcurrentCallsForDifferentTasks_NoDuplicates()
    {
        var (orch, daFactory, _) = Build();
        await using var _ = orch;

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            orch.StartAsync(Request($"task-{i}"))));

        Assert.Equal(10, orch.RunningTaskIds.Count);
        Assert.Equal(10, daFactory.Created.Count);
    }

    [Fact]
    public async Task DisposeAsync_StopsAllTasks()
    {
        var (orch, daFactory, pubFactory) = Build();
        await orch.StartAsync(Request("t1"));
        await orch.StartAsync(Request("t2"));
        await orch.DisposeAsync();

        Assert.All(daFactory.Created, s => Assert.True(s.Disposed));
        Assert.All(pubFactory.Created, c => Assert.True(c.Publisher.Disposed));
    }

    [Fact]
    public async Task GetDiagnostics_ReportsValueCountAndLastValueTime()
    {
        var (orch, daFactory, _) = Build();
        await using var _d = orch;

        await orch.StartAsync(Request("diag-1"));
        var sub = daFactory.Created.First();

        sub.EmitValue(new TagValue("A", 1.0, 0xC0, DateTimeOffset.UtcNow));
        sub.EmitValue(new TagValue("B", 2.0, 0xC0, DateTimeOffset.UtcNow));

        await WaitForAsync(() => orch.GetDiagnostics().FirstOrDefault()?.ValueCount >= 2);

        var d = orch.GetDiagnostics().Single();
        Assert.Equal("diag-1", d.TaskId);
        Assert.True(d.ValueCount >= 2);
        Assert.NotNull(d.LastValueAt);
        Assert.Equal(0, d.PublishErrorCount);
    }

    [Fact(Timeout = 10_000)]
    public async Task Start_TransitionsToRunning_AndDiagnosticsReportState()
    {
        var (orch, _, _) = Build();
        await using var _d = orch;

        await orch.StartAsync(Request("t1"));

        var d = orch.GetDiagnostics().Single(x => x.TaskId == "t1");
        Assert.Equal(ConnectionState.Running, d.State);
    }

    [Fact]
    public async Task StartAsync_UnknownProtocol_Throws()
    {
        var (orch, _, _) = Build();
        await using var _ = orch;

        var req = new TaskStartRequest("t1", OpcProtocol.Ua,
            new OpcConnectionOptions { ServerUri = "x" }, "127.0.0.1:5000", Array.Empty<TagDescriptor>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => orch.StartAsync(req));
    }

    [Fact]
    public async Task GetDiagnostics_FoldsQueuePendingAndDroppedFromPublisher()
    {
        var (orch, _, pubFactory) = Build();
        await using var _ = orch;
        await orch.StartAsync(Request("t1"));

        // 拿到该任务的 FakePublisher，设置健康计数
        var pub = pubFactory.Created.First().Publisher;
        pub.PendingBytes = 4096;
        pub.DroppedFrameCount = 7;

        var diag = orch.GetDiagnostics().Single(d => d.TaskId == "t1");

        Assert.Equal(4096, diag.QueuePendingBytes);
        Assert.Equal(7, diag.DroppedFrameCount);
    }

    private static ConnectionState State(TaskOrchestrator o, string id)
        => o.GetDiagnostics().Single(x => x.TaskId == id).State;

    [Fact(Timeout = 15_000)]
    public async Task Watchdog_RebindRestart_StaleThenRecover_BackToRunning()
    {
        var (orch, daFactory, _) = Build(new OrchestratorOptions
        {
            WatchdogInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(120),
            FaultThreshold = 3,
            StopDrainTimeout = TimeSpan.FromMilliseconds(200),
        });

        await orch.StartAsync(Request("t1"));
        Assert.Equal(ConnectionState.Running, State(orch, "t1"));

        // 停发心跳 → 看门狗超时 → 原地重绑（server 正常，重连成功，RestartCount 增）
        await WaitForAsync(() => orch.GetDiagnostics().Single(x => x.TaskId == "t1").RestartCount >= 1, TimeSpan.FromSeconds(4));

        // 重绑会 factory.Create 新 sub；给最新的 sub 发心跳 → 恢复 Running
        var fresh = daFactory.Created.Last();
        fresh.EmitHeartbeat(new HeartBeat("t1", DateTimeOffset.UtcNow));
        await WaitForAsync(() => State(orch, "t1") == ConnectionState.Running, TimeSpan.FromSeconds(4));
        await orch.DisposeAsync();
    }

    [Fact(Timeout = 15_000)]
    public async Task Watchdog_RebindRestart_PersistentFail_StaysInRunning_AndFaulted()
    {
        var (orch, daFactory, _) = Build(new OrchestratorOptions
        {
            WatchdogInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(120),
            FaultThreshold = 2,
            StopDrainTimeout = TimeSpan.FromMilliseconds(200),
        });

        await orch.StartAsync(Request("t1"));
        daFactory.ThrowOnConnectForFutureCreates = true;   // 之后每次重连 connect 抛
        await WaitForAsync(() => State(orch, "t1") == ConnectionState.Faulted, TimeSpan.FromSeconds(6));
        Assert.Contains(orch.GetDiagnostics(), x => x.TaskId == "t1");  // 仍在运行集，不消失
        await orch.DisposeAsync();
    }

    [Fact(Timeout = 15_000)]
    public async Task Watchdog_Faulted_ThenServerBack_RecoversToRunning()
    {
        var (orch, daFactory, _) = Build(new OrchestratorOptions
        {
            WatchdogInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(120),
            FaultThreshold = 2,
            StopDrainTimeout = TimeSpan.FromMilliseconds(200),
        });

        await orch.StartAsync(Request("t1"));
        daFactory.ThrowOnConnectForFutureCreates = true;
        await WaitForAsync(() => State(orch, "t1") == ConnectionState.Faulted, TimeSpan.FromSeconds(6));
        daFactory.ThrowOnConnectForFutureCreates = false;   // server 回来
        await WaitForAsync(() => daFactory.Created.Count >= 3, TimeSpan.FromSeconds(4));
        daFactory.Created.Last().EmitHeartbeat(new HeartBeat("t1", DateTimeOffset.UtcNow));
        await WaitForAsync(() => State(orch, "t1") == ConnectionState.Running, TimeSpan.FromSeconds(4));
        await orch.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task InjectFault_Stall_TriggersWatchdogRestart_AndMissReturnsFalse()
    {
        var (orch, _, _) = Build(new OrchestratorOptions
        {
            WatchdogInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(120),
        });

        await orch.StartAsync(Request("t1"));
        Assert.Equal(ConnectionState.Running, State(orch, "t1"));

        Assert.False(orch.InjectFault("nope", "stall"));   // 不存在 → false
        Assert.False(orch.InjectFault("t1", "bogus"));      // 未知 kind → false
        Assert.True(orch.InjectFault("t1", "stall"));        // 命中 → true

        await WaitForAsync(
            () => orch.GetDiagnostics().Single(x => x.TaskId == "t1").RestartCount >= 1,
            TimeSpan.FromSeconds(4));
        await orch.DisposeAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "WaitForAsync 条件未在超时内满足");
    }
}
