using Dc.Infrastructure.Orchestration;
using Dc.Infrastructure.Tests.Fakes;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

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
        // emit 间隔远小于 timeout（10x 余量），避免 CI 共享 runner 调度抖动误判超时；
        // 同时总时长(1.5s) > timeout(1s)，仍能验证「无心跳会重启、有心跳不重启」的语义。
        var opts = new OrchestratorOptions
        {
            WatchdogInterval = TimeSpan.FromMilliseconds(100),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(1000)
        };
        var (orch, daFactory, _) = Build(opts);
        await using var _ = orch;

        await orch.StartAsync(Request("t1"));
        var sub = daFactory.Created.First();

        // 持续心跳 1.5 s（> timeout），应不重启
        for (int i = 0; i < 15; i++)
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

    [Fact]
    public async Task StartAsync_UnknownProtocol_Throws()
    {
        var (orch, _, _) = Build();
        await using var _ = orch;

        var req = new TaskStartRequest("t1", OpcProtocol.Ua,
            new OpcConnectionOptions { ServerUri = "x" }, "127.0.0.1:5000", Array.Empty<TagDescriptor>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => orch.StartAsync(req));
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
    }
}
