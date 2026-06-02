# 发布缓冲/丢弃可观测 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让无头采集器的离线队列积压与溢出丢弃对运维可见——每任务暴露 `queue_pending_bytes` 与 `dropped_frames` 两项指标（Meter + Prometheus 双路径），并在开始/停止丢弃时打边沿日志。

**架构：** 顺现有 `IPublisherHealth` 接缝纵切：`OutboundQueue` 计数 →（`IPublisherHealth`）`BatchingTcpPublisher` 委托 → `TaskOrchestrator.GetDiagnostics` 折入 → `TaskDiagnostics` 两个新字段（带默认值 0，避免改动无关构造点）→ `DiagnosticsReporter`(Meter + 边沿日志) 与 `MetricsHttpServer`(Prometheus)。不动 `TcpPublisher`（仅测试用、同步发布器）、WPF UI、背压策略。

**技术栈：** .NET 8 / C#，xUnit，`System.Diagnostics.Metrics`，`HttpListener`。规格见 `docs/superpowers/specs/2026-06-02-publish-buffer-observability-design.md`。

**全局约定：** 构建/测试前置 `export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$PATH`。WPF（`Dc.App`）本地编不了，靠 Windows CI 验证；本计划改动均落在跨平台 `net8.0` 工程，可本地全验。

---

### 任务 1：`OutboundQueue` 累计丢弃帧数

**文件：**
- 修改：`src/Dc.Infrastructure/Messaging/IOutboundQueue.cs`
- 修改：`src/Dc.Infrastructure/Messaging/OutboundQueue.cs`
- 测试：`tests/Dc.Infrastructure.Tests/Messaging/OutboundQueueTests.cs`（新建）

- [ ] **步骤 1：编写失败的测试**

新建 `tests/Dc.Infrastructure.Tests/Messaging/OutboundQueueTests.cs`：

```csharp
using Dc.Infrastructure.Messaging;
using Xunit;

namespace Dc.Infrastructure.Tests.Messaging;

public class OutboundQueueTests
{
    private static byte[] Frame(int size)
    {
        var f = new byte[size];
        for (int i = 0; i < size; i++) f[i] = (byte)(i & 0xFF);
        return f;
    }

    [Fact]
    public void DropOldest_OnOverflow_CountsDroppedFramesAndCapsPending()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oq-test-{Guid.NewGuid():N}.bin");
        try
        {
            // maxBytes 容不下 10 帧（每帧 100B + 12B 头）→ 必触发 drop-oldest
            using var q = new OutboundQueue(path, maxBytes: 512);
            for (int i = 0; i < 10; i++) q.Enqueue(Frame(100));

            Assert.True(q.PendingBytes <= 512, $"PendingBytes={q.PendingBytes} 应 <= maxBytes");
            Assert.True(q.DroppedFrameCount > 0, "溢出应记录丢弃帧数");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var cursor = path + ".cursor";
            if (File.Exists(cursor)) File.Delete(cursor);
        }
    }

    [Fact]
    public void NoOverflow_DroppedFrameCountStaysZero()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oq-test-{Guid.NewGuid():N}.bin");
        try
        {
            using var q = new OutboundQueue(path, maxBytes: 1024 * 1024);
            q.Enqueue(Frame(100));
            Assert.Equal(0, q.DroppedFrameCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var cursor = path + ".cursor";
            if (File.Exists(cursor)) File.Delete(cursor);
        }
    }
}
```

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~OutboundQueueTests"`
预期：编译失败，`IOutboundQueue`/`OutboundQueue` 无 `DroppedFrameCount`。

- [ ] **步骤 3：接口加成员**

`src/Dc.Infrastructure/Messaging/IOutboundQueue.cs`，在 `long PendingBytes { get; }` 下方加：

```csharp
    // 累计因超 MaxBytes 被 drop-oldest 丢弃的帧数。供监控/告警感知数据丢失。
    long DroppedFrameCount { get; }
```

- [ ] **步骤 4：实现计数**

`src/Dc.Infrastructure/Messaging/OutboundQueue.cs`：

(a) 在字段区（`_maxBytes` 附近）加：

```csharp
    private long _droppedFrameCount;
```

(b) 在 `PendingBytes` 属性下方加 getter（镜像其锁约定）：

```csharp
    public long DroppedFrameCount
    {
        get { lock (_lock) return _droppedFrameCount; }
    }
```

(c) 在 `DropOldestUntilFits()` 的 `if (TryReadRecordHeader(...))` 分支自增（成功跳过一条完整记录 = 丢一帧；`else` 是损坏 resync 的垃圾，不计）：

```csharp
                if (TryReadRecordHeader(fs, droppedToOffset, fi.Length, out var len))
                {
                    droppedToOffset += RecHeaderSize + len;
                    _droppedFrameCount++;
                }
```

- [ ] **步骤 5：运行测试验证通过**

运行：`dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~OutboundQueueTests"`
预期：PASS（2 passed）。

- [ ] **步骤 6：Commit**

```bash
git add src/Dc.Infrastructure/Messaging/IOutboundQueue.cs src/Dc.Infrastructure/Messaging/OutboundQueue.cs tests/Dc.Infrastructure.Tests/Messaging/OutboundQueueTests.cs
git commit -m "✨ feat(queue): OutboundQueue 累计 drop-oldest 丢弃帧数"
```

---

### 任务 2：`IPublisherHealth` 暴露积压/丢弃 + `BatchingTcpPublisher` 委托

**文件：**
- 修改：`src/Dc.Infrastructure/Messaging/IPublisherHealth.cs`
- 修改：`src/Dc.Infrastructure/Messaging/BatchingTcpPublisher.cs`
- 测试：`tests/Dc.Infrastructure.Tests/Messaging/BatchingTcpPublisherHealthTests.cs`（新建）

- [ ] **步骤 1：编写失败的测试**

新建 `tests/Dc.Infrastructure.Tests/Messaging/BatchingTcpPublisherHealthTests.cs`：

```csharp
using Dc.Infrastructure.Messaging;
using Xunit;

namespace Dc.Infrastructure.Tests.Messaging;

public class BatchingTcpPublisherHealthTests
{
    [Fact]
    public async Task Health_DelegatesQueuePendingAndDropped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"btp-test-{Guid.NewGuid():N}.bin");
        try
        {
            using var queue = new OutboundQueue(path, maxBytes: 1024 * 1024);
            queue.Enqueue(new byte[50]);

            // 指向一个不会有人听的端口；只验证 health 委托读队列，不真发送。
            await using var pub = new BatchingTcpPublisher("127.0.0.1", 1, new JsonMessageSerializer(), queue);
            var health = (IPublisherHealth)pub;

            Assert.Equal(queue.PendingBytes, health.PendingBytes);
            Assert.True(health.PendingBytes > 0);
            Assert.Equal(queue.DroppedFrameCount, health.DroppedFrameCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var cursor = path + ".cursor";
            if (File.Exists(cursor)) File.Delete(cursor);
        }
    }

    [Fact]
    public async Task Health_NoQueue_ReturnsZero()
    {
        await using var pub = new BatchingTcpPublisher("127.0.0.1", 1, new JsonMessageSerializer(), queue: null);
        var health = (IPublisherHealth)pub;
        Assert.Equal(0, health.PendingBytes);
        Assert.Equal(0, health.DroppedFrameCount);
    }
}
```

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~BatchingTcpPublisherHealthTests"`
预期：编译失败，`IPublisherHealth` 无 `PendingBytes`/`DroppedFrameCount`。

- [ ] **步骤 3：接口加成员**

`src/Dc.Infrastructure/Messaging/IPublisherHealth.cs`，在 `long SendErrorCount { get; }` 下方加：

```csharp
    /// 当前离线队列未发字节数（无队列时 0）。
    long PendingBytes { get; }

    /// 累计因队列溢出被 drop-oldest 丢弃的帧数（无队列时 0）。
    long DroppedFrameCount { get; }
```

- [ ] **步骤 4：`BatchingTcpPublisher` 实现委托**

`src/Dc.Infrastructure/Messaging/BatchingTcpPublisher.cs`，在 `public long SendErrorCount => Interlocked.Read(ref _sendErrorCount);` 下方加：

```csharp
    /// <inheritdoc />
    public long PendingBytes => _queue?.PendingBytes ?? 0;

    /// <inheritdoc />
    public long DroppedFrameCount => _queue?.DroppedFrameCount ?? 0;
```

- [ ] **步骤 5：运行测试验证通过**

运行：`dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~BatchingTcpPublisherHealthTests"`
预期：PASS（2 passed）。

- [ ] **步骤 6：Commit**

```bash
git add src/Dc.Infrastructure/Messaging/IPublisherHealth.cs src/Dc.Infrastructure/Messaging/BatchingTcpPublisher.cs tests/Dc.Infrastructure.Tests/Messaging/BatchingTcpPublisherHealthTests.cs
git commit -m "✨ feat(publish): IPublisherHealth 暴露队列积压与丢弃帧数"
```

---

### 任务 3：`TaskDiagnostics` 加字段 + `GetDiagnostics` 折入

**文件：**
- 修改：`src/Dc.Infrastructure/Orchestration/TaskDiagnostics.cs`
- 修改：`src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs:54-72`
- 修改：`tests/Dc.Infrastructure.Tests/Fakes/FakePublisher.cs`
- 测试：`tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs`

- [ ] **步骤 1：让 `FakePublisher` 实现 `IPublisherHealth`**

`tests/Dc.Infrastructure.Tests/Fakes/FakePublisher.cs`，把类声明与成员改为（加可设的 health 计数，默认 0）：

```csharp
public sealed class FakePublisher : IPublisher, IPublisherHealth
{
    public ConcurrentQueue<object> Published { get; } = new();
    public bool Disposed { get; private set; }

    // 测试可设的健康计数
    public long SendErrorCount { get; set; }
    public long PendingBytes { get; set; }
    public long DroppedFrameCount { get; set; }

    // 可选：每次发布的人为延迟，用于让值在通道里积压（测试停止 drain）。默认 0 = 立即返回。
    public TimeSpan PublishDelay { get; set; } = TimeSpan.Zero;

    public async Task PublishAsync<T>(T message, CancellationToken ct = default)
    {
        if (PublishDelay > TimeSpan.Zero)
            await Task.Delay(PublishDelay, ct).ConfigureAwait(false);
        Published.Enqueue(message!);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **步骤 2：编写失败的测试**

在 `tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs` 末尾（最后一个 `}` 前）加：

```csharp
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
```

> 注：`Build()` 与 `Request("t1")` 是该测试类既有 helper（见文件顶部）；`pubFactory.Created` 是 `FakePublisherFactory` 既有暴露。

- [ ] **步骤 3：运行测试验证失败**

运行：`dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~GetDiagnostics_FoldsQueuePendingAndDroppedFromPublisher"`
预期：编译失败，`TaskDiagnostics` 无 `QueuePendingBytes`/`DroppedFrameCount`。

- [ ] **步骤 4：`TaskDiagnostics` 加字段（带默认值）**

`src/Dc.Infrastructure/Orchestration/TaskDiagnostics.cs` 改为：

```csharp
public sealed record TaskDiagnostics(
    string TaskId,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastValueAt,
    DateTimeOffset? LastHeartbeatAt,
    long ValueCount,
    long PublishErrorCount,
    int RestartCount,
    int SubscribedTagCount,
    long QueuePendingBytes = 0,
    long DroppedFrameCount = 0);
```

> 默认值 0 让现有构造点（WPF 测试、E2E、MetricsHttpServerTests、DiagnosticsReporterTests 的 `Diag` helper）无需改动。

- [ ] **步骤 5：`GetDiagnostics` 折入**

`src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs`，把 `GetDiagnostics` 内的 lambda 改为（复用一个 health 句柄）：

```csharp
        return _running.Values.Select(rt =>
        {
            // 批量/异步 Publisher 的发送失败发生在后台，PublishAsync 不抛 → 折入这里，
            // 否则 broker 宕机时 PublishErrorCount 恒 0、Dashboard 假健康。
            var health = rt.Publisher as IPublisherHealth;
            var bgErrors = health?.SendErrorCount ?? 0;
            return new TaskDiagnostics(
                rt.TaskId,
                rt.StartedAt,
                rt.LastValueAt,
                rt.LastHeartbeat,
                Interlocked.Read(ref rt.ValueCount),
                Interlocked.Read(ref rt.PublishErrorCount) + bgErrors,
                rt.RestartCount,
                rt.Tags.Count,
                health?.PendingBytes ?? 0,
                health?.DroppedFrameCount ?? 0);
        }).ToArray();
```

- [ ] **步骤 6：运行测试验证通过**

运行：`dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~TaskOrchestratorTests"`
预期：PASS（既有用例 + 新用例全过）。

- [ ] **步骤 7：Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/TaskDiagnostics.cs src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs tests/Dc.Infrastructure.Tests/Fakes/FakePublisher.cs tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs
git commit -m "✨ feat(diag): TaskDiagnostics 携带队列积压/丢弃，GetDiagnostics 折入"
```

---

### 任务 4：`DiagnosticsReporter` Meter 指标 + 边沿日志

**文件：**
- 修改：`src/Dc.Infrastructure/Orchestration/DiagnosticsReporter.cs`
- 测试：`tests/Dc.Infrastructure.Tests/Orchestration/DiagnosticsReporterTests.cs`

- [ ] **步骤 1：编写失败的测试（Meter 两新指标 + 边沿日志）**

在 `tests/Dc.Infrastructure.Tests/Orchestration/DiagnosticsReporterTests.cs` 顶部 `using` 后加一个捕获 logger（若文件已有同名工具则复用，不要重复定义）：

```csharp
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
```

在类内加两个测试：

```csharp
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
```

> 注：`() => new[]{ new TaskDiagnostics(..., DroppedFrameCount: dropped) }` 每次调用都读当时的 `dropped` 闭包变量，故三次 `LogOnce` 看到不同值。

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~DiagnosticsReporterTests"`
预期：新两用例 FAIL（指标不存在 / 无边沿日志），旧用例仍 PASS。

- [ ] **步骤 3：加两个 Meter 指标**

`src/Dc.Infrastructure/Orchestration/DiagnosticsReporter.cs` 的 `SetupMetrics()` 内，在 `dc.collector.task.heartbeat_age_seconds` 那条之后加：

```csharp
        _meter.CreateObservableGauge("dc.collector.task.queue_pending_bytes",
            () => Each(d => d.QueuePendingBytes), unit: "By", description: "每任务离线队列未发字节数");

        _meter.CreateObservableGauge("dc.collector.task.dropped_frames",
            () => Each(d => d.DroppedFrameCount), unit: "{frames}", description: "每任务累计因队列溢出丢弃的帧数");
```

- [ ] **步骤 4：加边沿日志 + 明细行追加**

(a) 在 `DiagnosticsReporter` 字段区加状态字典：

```csharp
    // per-task 丢弃边沿状态：(上次累计丢弃数, 当前是否处于丢弃中)。仅日志循环单线程访问。
    private readonly Dictionary<string, (long Last, bool Dropping)> _dropState = new();
```

(b) 改 `LogOnce()`：在取得 `snap` 之后、`snap.Count == 0` 判断之前，调用边沿检测；并在 per-task 明细行追加积压/丢弃。改为：

```csharp
    public void LogOnce()
    {
        if (_logger is null) return;
        var now = DateTimeOffset.UtcNow;
        var snap = _provider();
        LogDropEdges(snap);
        if (snap.Count == 0)
        {
            _logger.LogInformation("诊断：当前无运行任务");
            return;
        }
        foreach (var d in snap)
        {
            var hbAge = d.LastHeartbeatAt is { } hb ? $"{(now - hb).TotalSeconds:F0}" : "—";
            _logger.LogInformation(
                "诊断 task={TaskId} 运行={UpSeconds:F0}s 值={Values} 发布错误={PublishErrors} 重启={Restarts} 订阅Tag={Tags} 心跳龄={HeartbeatAge}s 积压={QueueBytes}B 丢弃={Dropped}",
                d.TaskId, (now - d.StartedAt).TotalSeconds, d.ValueCount, d.PublishErrorCount,
                d.RestartCount, d.SubscribedTagCount, hbAge, d.QueuePendingBytes, d.DroppedFrameCount);
        }
    }

    // 比对累计丢弃数的跳变，开始丢弃打 WARN、停止丢弃打 INFO；任务重启归零或消失时重置状态。
    private void LogDropEdges(IReadOnlyList<TaskDiagnostics> snap)
    {
        var seen = new HashSet<string>();
        foreach (var d in snap)
        {
            seen.Add(d.TaskId);
            var cur = d.DroppedFrameCount;
            if (_dropState.TryGetValue(d.TaskId, out var st))
            {
                if (cur > st.Last && !st.Dropping)
                {
                    _logger!.LogWarning("诊断 task={TaskId} 离线队列溢出，开始丢弃最旧帧（累计丢 {Dropped}）", d.TaskId, cur);
                    _dropState[d.TaskId] = (cur, true);
                }
                else if (cur == st.Last && st.Dropping)
                {
                    _logger!.LogInformation("诊断 task={TaskId} 队列停止丢弃（累计丢 {Dropped}）", d.TaskId, cur);
                    _dropState[d.TaskId] = (cur, false);
                }
                else if (cur < st.Last)
                {
                    _dropState[d.TaskId] = (cur, false); // 任务重启 → 队列重建归零
                }
                else
                {
                    _dropState[d.TaskId] = (cur, st.Dropping);
                }
            }
            else
            {
                _dropState[d.TaskId] = (cur, cur > 0);
                if (cur > 0)
                    _logger!.LogWarning("诊断 task={TaskId} 离线队列溢出，开始丢弃最旧帧（累计丢 {Dropped}）", d.TaskId, cur);
            }
        }
        // 清理快照里已消失的任务，防字典无界增长。
        var gone = _dropState.Keys.Where(k => !seen.Contains(k)).ToList();
        foreach (var k in gone) _dropState.Remove(k);
    }
```

> 需要 `using System.Linq;`（`Where`/`ToList`）——文件若未引入则加到顶部。

- [ ] **步骤 5：运行测试验证通过**

运行：`dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~DiagnosticsReporterTests"`
预期：PASS（旧 + 新两用例）。

- [ ] **步骤 6：Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/DiagnosticsReporter.cs tests/Dc.Infrastructure.Tests/Orchestration/DiagnosticsReporterTests.cs
git commit -m "✨ feat(diag): Meter 加队列积压/丢弃指标 + drop 边沿日志"
```

---

### 任务 5：`MetricsHttpServer` Prometheus 两指标

**文件：**
- 修改：`src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs`
- 测试：`tests/Dc.Infrastructure.Tests/Orchestration/MetricsHttpServerTests.cs`

- [ ] **步骤 1：扩测试断言**

`tests/Dc.Infrastructure.Tests/Orchestration/MetricsHttpServerTests.cs` 的 `Render_WithTasks_EmitsLabeledSamplesAndHeartbeatAge`：把 `T1` 的构造改为带新字段，并加两条断言。

把 T1 构造（约 27-35 行）改为：

```csharp
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
```

在该测试的断言区追加：

```csharp
        Assert.Contains("dc_collector_task_queue_pending_bytes{task_id=\"T1\"} 8192", text);
        Assert.Contains("dc_collector_task_dropped_frames{task_id=\"T1\"} 11", text);
```

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~MetricsHttpServerTests"`
预期：`Render_WithTasks...` FAIL（缺两条指标行）。

- [ ] **步骤 3：渲染两指标**

`src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs` 的 `RenderPrometheus`，在 `dc_collector_task_heartbeat_age_seconds` 那段之后、`return sb.ToString();` 之前加：

```csharp
        Gauge(sb, "dc_collector_task_queue_pending_bytes", "每任务离线队列未发字节数。",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.QueuePendingBytes); });
        Gauge(sb, "dc_collector_task_dropped_frames", "每任务累计因队列溢出丢弃的帧数。",
            g => { foreach (var d in snap) g.Line(d.TaskId, d.DroppedFrameCount); });
```

- [ ] **步骤 4：运行测试验证通过**

运行：`dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~MetricsHttpServerTests"`
预期：PASS（含原 ParsePort 等全部）。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs tests/Dc.Infrastructure.Tests/Orchestration/MetricsHttpServerTests.cs
git commit -m "✨ feat(diag): /metrics 加队列积压/丢弃 Prometheus 指标"
```

---

### 任务 6：全量验证 + README + 推送

**文件：**
- 修改：`README.md`（诊断端点指标表）

- [ ] **步骤 1：更新 README 指标表**

`README.md` 诊断端点表格里 `GET /metrics` 行的指标列表，在 `心跳龄` 后补 `队列积压字节/累计丢弃帧数`。把该行改为：

```markdown
| `GET /metrics` | Prometheus 文本，导出 `dc_collector_*`（运行任务数、每任务值数/发布错误/重启/订阅 Tag 数/心跳龄/队列积压字节/累计丢弃帧数） |
```

- [ ] **步骤 2：本地全量构建 + 测试**

运行：
```bash
export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$PATH
dotnet build src/Dc.Cli/Dc.Cli.csproj -c Release --nologo -v q
dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj -c Release --nologo
dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj -c Release --nologo
```
预期：Dc.Cli 0 错误；Infra + Integration 全 PASS。

- [ ] **步骤 3：Commit + 推送**

```bash
git add README.md
git commit -m "📝 docs: README 诊断指标表补队列积压/丢弃"
git push origin main
```

- [ ] **步骤 4：盯 CI 到绿**

运行：`gh run list --branch main --limit 1 --json databaseId --jq '.[0].databaseId'` 取 run id，再 `gh run watch <id> --exit-status`。
预期：`Build & Test (Windows)` success（WPF 编译 + 全测试，验证 `TaskDiagnostics` 默认值未波及 WPF 测试构造点）。

---

## 自检结果

**规格覆盖度：**
- ① 接口/类型改动 → 任务 1（IOutboundQueue/OutboundQueue）、2（IPublisherHealth/BatchingTcpPublisher）、3（TaskDiagnostics/GetDiagnostics）。✓
- ② 数据流 → 任务 3 折入贯通。✓
- ③ 指标命名（双路径）→ 任务 4（Meter）、5（Prometheus）。✓
- ④ 边沿日志 → 任务 4（LogDropEdges + LogOnce 明细行）。✓
- ⑤ 测试 → 任务 1/2/3/4/5 各含；README 任务 6。✓

**与规格的有意偏差：** `TaskDiagnostics` 新字段用**可选默认值 0**（规格原写"更新所有构造点"）——严格更优：现有构造点（含 2 个 WPF 测试文件）无需改动，消除 WPF CI-only 风险。已在任务 3 步骤 4 注明。

**占位符扫描：** 无 TODO/待定；每个代码步骤含完整代码。✓

**类型一致性：** `QueuePendingBytes`/`DroppedFrameCount` 字段名跨任务 3/4/5 一致；指标名 `dc.collector.task.queue_pending_bytes`/`dropped_frames`（Meter）与 `dc_collector_task_queue_pending_bytes`/`dropped_frames`（Prometheus）镜像一致；`IPublisherHealth.PendingBytes/DroppedFrameCount` 与 `OutboundQueue` 同名委托。✓
