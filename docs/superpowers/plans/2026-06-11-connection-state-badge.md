# 连接状态徽章（编排器拥有状态）实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）逐任务实现。步骤用复选框（`- [ ]`）跟踪。

**目标：** 采集任务在掉线/重启/恢复时，诊断页有清晰可信的连接状态徽章（连接中/运行正常/重启中/故障 + 「已恢复」瞬态），并加门控 `/debug/fault` + dc-remote `fault` 子命令活体演示。

**架构：** 编排器在 RuntimeTask 上拥有 `ConnectionState`；**初次启动改 rt-first**（Connecting→Running 可观测），**看门狗重启改原地重绑**（rt 跨重连存活，Restarting→Running/Faulted 可观测，顺带修复「重连失败任务消失」缺陷）；状态进 `TaskDiagnostics` + `/metrics`；UI 渲染徽章 + 已恢复瞬态。

**技术栈：** .NET 8 / C#、`SemaphoreSlim` 锁、`Channel`、xUnit、WPF（DataTrigger pill）、HttpListener、dc-remote bash/PowerShell。

---

## 测试落机路由

| 件 | 工程 | 跑在 |
|---|---|---|
| 状态机/注入缝/端点/metrics 渲染 | `Dc.Infrastructure.Tests`(net8.0) + `Dc.Integration.Tests`(net8.0) | **本机 Linux** |
| DiagnosticsRowViewModel 已恢复瞬态 | `Dc.App.Tests`(net8.0-windows) | **office** |
| 诊断徽章 UI + 活体故障注入 | dc-remote `fault` | **office** |

- Linux：`export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj`（状态机测属 `[Collection("Timing-Sensitive")]` 串行）。
- office：dc-remote `sync`→`build`→`test`→`fault`→`shot`。

## 已核实的现有结构（对齐，勿臆造）

- `TaskRuntime`（`TaskOrchestrator.cs` 内 private class，字段名 `rt`）：`TaskId/Request/Subscriber/Publisher/Cts/PipelineTask/Tags/LastHeartbeat/StartedAt/LastValueAt/ValueCount/PublishErrorCount/RestartCount`。
- `StartUnlockedAsync(request, factory, ct)`：建 subscriber/publisher/cts → `ConnectAsync`+`SubscribeAsync`（try/catch 失败则 dispose+throw）→ 建 `TaskRuntime`（PipelineTask=CompletedTask）→ `runtime.PipelineTask = Task.Run(() => RunPipelineAsync(runtime, cts.Token))` → `_running[id]=runtime`。
- `RunPipelineAsync(rt, ct)`：`heartTask = ConsumeAsync(rt.Subscriber.Heartbeats, h => { rt.LastHeartbeat = h.Time; ... })`。
- `RestartIfStaleAsync(taskId)`：`_mutationLock` 内 → 校验在+心跳超时+有 factory → `StopUnlockedAsync` + `StartUnlockedAsync` → 回写 `RestartCount`。
- `StopUnlockedAsync(taskId)`：`_running.TryRemove` → cancel cts → await PipelineTask（`StopDrainTimeout`）→ SafeDispose subscriber/publisher。
- `GetDiagnostics()`：`_running.Values.Select(rt => new TaskDiagnostics(rt.TaskId, rt.StartedAt, rt.LastValueAt, rt.LastHeartbeat, ...ValueCount, ...PublishErrorCount, rt.RestartCount, rt.Tags.Count, PendingBytes, DroppedFrames))`。
- `OrchestratorOptions`：含 `WatchdogInterval`/`HeartbeatTimeout`/`StopDrainTimeout`。
- `MetricsHttpServer` 构造（6 参）：`(Func<IReadOnlyList<TaskDiagnostics>> diagnosticsProvider, MetricsServerOptions? options=null, ILogger<MetricsHttpServer>? logger=null, Func<byte[]?>? screenshotProvider=null, Func<LiveFlushStats?>? liveFlushProvider=null, Func<int,int,int,CancellationToken,Task<long>>? stressRunner=null)`。switch 路由含 `/healthz /readyz /metrics /screenshot /debug/stress`，default 404。`Write(ctx,status,ct,body)`、`ParseInt(s,def)` 辅助已存在。`_cts` 字段为 accept 循环 CTS。
- `RenderPrometheus(IReadOnlyList<TaskDiagnostics> snap, DateTimeOffset now, LiveFlushStats? live=null)`：`Gauge(sb,name,help, g => { foreach(var d in snap) g.Line(d.TaskId, value); })` 渲染带 `task_id` 标签的样本。`GaugeWriter.Line(string? taskId, double value)`。
- 测试夹具：`FakeOpcSubscriber`（`EmitValue/EmitHeartbeat/ConnectCalls/ThrowOnConnect/Disposed`）、`FakeOpcSubscriberFactory`（`Created` ConcurrentQueue 追踪每次 create）。`TaskOrchestratorTests` 用 `[Collection("Timing-Sensitive")]`，已有 `Watchdog_RestartsTask_WhenHeartbeatStale`（WatchdogInterval=50ms/HeartbeatTimeout=100ms）等。`Build(opts)` helper 造 (orch, daFactory, pubFactory)。
- `MetricsHttpServerE2ETests`（`Dc.Integration.Tests`）：真起 HttpListener + `GetFreePort()` + 命名参数注入 provider。
- `DiagnosticsRowViewModel`：`[ObservableProperty]` 字段 + `Apply(TaskDiagnostics d)`；已有 `HeartbeatSeverity`/`HasErrors`。`DiagnosticsViewModel` 第 82/90 行从 `GetDiagnostics()` 重建 `_rowIndex`/`Rows`。
- `DiagnosticsView.xaml` 列：(icon) 任务/启动/运行时长/累计值/速率/趋势/发送错误/重启/…（DataGridTemplateColumn + DataGridTextColumn 混用）。LiveData 质量 pill 风格（`SystemFillColor{Success,Caution,Critical}Background/Brush`）可复用。
- `ServiceRegistration.cs`：`AddSingleton<MetricsHttpServer>(sp => {...DC_DEBUG_STRESS 门控 stressRunner...})`（block lambda）。`orch.InjectSynthetic` 经 `InternalsVisibleTo("Dc.App")` 可见。

## 文件结构

**新增：** `src/Dc.Infrastructure/Orchestration/ConnectionState.cs`（枚举）。
**修改：** `TaskOrchestrator.cs`（RuntimeTask 字段 + rt-first 初启 + 原地重绑 + InjectFault + GetDiagnostics）、`TaskDiagnostics.cs`、`OrchestratorOptions.cs`、`MetricsHttpServer.cs`、`DiagnosticsReporter.cs`（Meter 镜像）、`ServiceRegistration.cs`、`DiagnosticsRowViewModel.cs`、`DiagnosticsView.xaml`、dc-remote.sh。
**测试：** 扩 `TaskOrchestratorTests`（状态转移）、新 e2e（/debug/fault）、metrics 渲染测、新 `DiagnosticsRowViewModelRecoveryTests`。

---

## 任务 1：ConnectionState 枚举 + TaskDiagnostics.State + FaultThreshold

**文件：** 创建 `src/Dc.Infrastructure/Orchestration/ConnectionState.cs`；改 `TaskDiagnostics.cs`、`OrchestratorOptions.cs`；测试 `tests/Dc.Infrastructure.Tests/Orchestration/MetricsHttpServerTests.cs`（沿用其 TaskDiagnostics 构造）。

- [ ] **步骤 1：建枚举**

```csharp
namespace Dc.Infrastructure.Orchestration;

/// <summary>采集任务连接生命周期状态（编排器拥有，UI 与 /metrics 消费）。
/// 仅描述「运行集里活任务」的连接态；用户停止的任务直接从快照消失（=行消失），非此处某值。</summary>
public enum ConnectionState
{
    Connecting, // 初次/重连的 connect 阶段进行中
    Running,    // 已连接+订阅，心跳正常流动
    Restarting, // 心跳超时，看门狗正在原地重绑重连
    Faulted     // 连续 ≥FaultThreshold 次重启仍未恢复心跳（疑似 server 长断）
}
```

- [ ] **步骤 2：TaskDiagnostics 加 State（末位带默认值，兼容现有构造点）**

`TaskDiagnostics.cs` record 末位加参数：
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
    long DroppedFrameCount = 0,
    ConnectionState State = ConnectionState.Running);
```

- [ ] **步骤 3：OrchestratorOptions 加 FaultThreshold**

`OrchestratorOptions.cs` 加（沿用现有属性写法）：
```csharp
/// <summary>连续看门狗重启仍未恢复心跳的次数阈值，达到则标记 Faulted。</summary>
public int FaultThreshold { get; init; } = 3;
```

- [ ] **步骤 4：编译验证（Linux）**

```bash
export DOTNET_ROOT=$HOME/.dotnet
~/.dotnet/dotnet build src/Dc.Infrastructure/Dc.Infrastructure.csproj -c Release --nologo -v q 2>&1 | tail -3
```
预期：0 错误（State 默认值使现有 TaskDiagnostics 构造点不破）。

- [ ] **步骤 5：Commit**
```bash
git add src/Dc.Infrastructure/Orchestration/ConnectionState.cs src/Dc.Infrastructure/Orchestration/TaskDiagnostics.cs src/Dc.Infrastructure/Orchestration/OrchestratorOptions.cs
git commit -m "✨ feat(diag): ConnectionState 枚举 + TaskDiagnostics.State + FaultThreshold"
```

---

## 任务 2：rt-first 初次启动（Connecting→Running）+ GetDiagnostics 发 State + 状态锁

**文件：** 改 `TaskOrchestrator.cs`；测试 `tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs`。

- [ ] **步骤 1：写失败测试**

```csharp
[Fact(Timeout = 10_000)]
public async Task Start_TransitionsToRunning_AndDiagnosticsReportState()
{
    var (orch, daFactory, _) = Build();
    await orch.StartAsync(DaReq("t1"));   // 沿用本测试类现有 DaReq/StartAsync helper（若名不同用实际）
    var d = orch.GetDiagnostics().Single(x => x.TaskId == "t1");
    Assert.Equal(ConnectionState.Running, d.State);
    await orch.DisposeAsync();
}
```
> 若本类无 `DaReq`/`StartAsync` 包装，参考现有 Watchdog 测怎么起任务（`Build()` + 启动请求），照抄其构造任务的方式。

- [ ] **步骤 2：运行验证失败**（Linux）：`State` 默认 Running 可能已让此测通过——故本步主要锁「rt-first 不回归」，关键断言在任务 3 的转移测。先确认编译+起任务正常。

- [ ] **步骤 3：实现 rt-first 初启 + 状态锁 + GetDiagnostics**

RuntimeTask 加字段：
```csharp
public ConnectionState State { get; set; } = ConnectionState.Connecting;
public int ConsecutiveStaleRestarts { get; set; }
public DateTimeOffset? LastRestartAt { get; set; }
```
加状态锁字段（类级）+ helper：
```csharp
private readonly object _stateLock = new();
private void SetState(TaskRuntime rt, ConnectionState s) { lock (_stateLock) rt.State = s; }
```
`StartUnlockedAsync` 改为 **rt-first**（先建 rt 入 _running 标 Connecting，再 connect）：
```csharp
private async Task StartUnlockedAsync(TaskStartRequest request, IOpcSubscriberFactory factory, CancellationToken ct)
{
    var subscriber = factory.Create(request.TaskId, request.OpcOptions);
    var publisher = _publisherFactory.Create(request.PublisherAddress);
    var cts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);

    var runtime = new TaskRuntime
    {
        TaskId = request.TaskId,
        Request = request,
        Subscriber = subscriber,
        Publisher = publisher,
        Cts = cts,
        PipelineTask = Task.CompletedTask,
        Tags = request.Tags.ToDictionary(t => t.Item),
        State = ConnectionState.Connecting,
    };
    _running[request.TaskId] = runtime;   // 先入运行集 → GetDiagnostics 立即可见「连接中」

    try
    {
        await subscriber.ConnectAsync(ct).ConfigureAwait(false);
        await subscriber.SubscribeAsync(request.Tags, ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "任务 {TaskId} ({Protocol}) 连接/订阅失败：{Message}", request.TaskId, request.Protocol, ex.Message);
        _running.TryRemove(request.TaskId, out _);   // 初次失败不残留
        await SafeDisposeAsync(subscriber).ConfigureAwait(false);
        await SafeDisposeAsync(publisher).ConfigureAwait(false);
        cts.Dispose();
        throw;
    }

    runtime.LastHeartbeat = DateTimeOffset.UtcNow;
    runtime.PipelineTask = Task.Run(() => RunPipelineAsync(runtime, cts.Token));
    SetState(runtime, ConnectionState.Running);
}
```
> 注意：现有 `StartUnlockedAsync` 在 catch 后 `throw`，调用方（`StartAsync`）已处理；保持。`TaskRuntime` 若用 `required` init 属性，`State` 用普通 `{get;set;}` 默认 Connecting 即可，构造里显式赋 Connecting 亦可。

`GetDiagnostics()` 的 `new TaskDiagnostics(...)` 末位加 `rt.State`：
```csharp
            return new TaskDiagnostics(
                rt.TaskId, rt.StartedAt, rt.LastValueAt, rt.LastHeartbeat,
                Interlocked.Read(ref rt.ValueCount),
                Interlocked.Read(ref rt.PublishErrorCount) + bgErrors,
                rt.RestartCount, rt.Tags.Count,
                health?.PendingBytes ?? 0, health?.DroppedFrameCount ?? 0,
                rt.State);
```

- [ ] **步骤 4：运行验证通过 + 无回归**（Linux）：`Dc.Infrastructure.Tests` 全绿（含现有 watchdog 测——rt-first 不应破坏重启，但任务 3 才改重启；本任务后 `StopUnlockedAsync`+`StartUnlockedAsync` 组合的旧重启路径仍工作，因 StartUnlocked 仍自洽）。

- [ ] **步骤 5：Commit**
```bash
git add src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs
git commit -m "✨ feat(diag): 初次启动 rt-first（连接中可观测）+ GetDiagnostics 发 State"
```

---

## 任务 3（crux）：看门狗重启改原地重绑 + Faulted + 心跳恢复

**文件：** 改 `TaskOrchestrator.cs`；测试 `tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs`（`[Collection("Timing-Sensitive")]` 已在类级）。

- [ ] **步骤 1：写失败测试（三例）**

```csharp
[Fact(Timeout = 15_000)]
public async Task Watchdog_RebindRestart_StaleThenRecover_TransitionsRunningRestartingRunning()
{
    var (orch, daFactory, _) = Build(new OrchestratorOptions
    {
        WatchdogInterval = TimeSpan.FromMilliseconds(50),
        HeartbeatTimeout = TimeSpan.FromMilliseconds(120),
        FaultThreshold = 3,
        StopDrainTimeout = TimeSpan.FromMilliseconds(200),
    });
    await orch.StartAsync(DaReq("t1"));
    Assert.Equal(ConnectionState.Running, State(orch, "t1"));

    // 停发心跳 → 看门狗超时 → 原地重绑重启（server 正常：重连成功）
    await WaitUntil(() => orch.GetDiagnostics().Single(x=>x.TaskId=="t1").RestartCount >= 1, 3000);
    // 重启后抓「新」subscriber（factory.Created 第 2 个）发心跳 → 恢复
    var fresh = daFactory.Created.Last();   // 重绑会 factory.Create 一个新的
    fresh.EmitHeartbeat(new HeartBeat("t1", DateTimeOffset.UtcNow));
    await WaitUntil(() => State(orch, "t1") == ConnectionState.Running
                       && orch.GetDiagnostics().Single(x=>x.TaskId=="t1").RestartCount >= 1, 3000);
    Assert.True(orch.GetDiagnostics().Single(x=>x.TaskId=="t1").RestartCount >= 1);
    await orch.DisposeAsync();
}

[Fact(Timeout = 15_000)]
public async Task Watchdog_RebindRestart_PersistentReconnectFail_StaysInRunning_AndFaulted()
{
    var (orch, daFactory, _) = Build(new OrchestratorOptions
    {
        WatchdogInterval = TimeSpan.FromMilliseconds(50),
        HeartbeatTimeout = TimeSpan.FromMilliseconds(120),
        FaultThreshold = 2,
        StopDrainTimeout = TimeSpan.FromMilliseconds(200),
    });
    await orch.StartAsync(DaReq("t1"));
    // 让后续每次重连 connect 都抛 → 任务不消失、累计到 Faulted
    daFactory.ThrowOnConnectForFutureCreates = true;   // 见步骤 3：给 factory 加该开关
    await WaitUntil(() => State(orch, "t1") == ConnectionState.Faulted, 5000);
    Assert.Contains(orch.GetDiagnostics(), x => x.TaskId == "t1"); // 仍在 _running，不消失
    Assert.Equal(ConnectionState.Faulted, State(orch, "t1"));
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
    await orch.StartAsync(DaReq("t1"));
    daFactory.ThrowOnConnectForFutureCreates = true;
    await WaitUntil(() => State(orch, "t1") == ConnectionState.Faulted, 5000);
    // server 回来：后续 create 不再抛 → 下次看门狗重连成功 → 新 sub 发心跳 → 恢复
    daFactory.ThrowOnConnectForFutureCreates = false;
    await WaitUntil(() => daFactory.Created.Count >= 3, 3000);
    daFactory.Created.Last().EmitHeartbeat(new HeartBeat("t1", DateTimeOffset.UtcNow));
    await WaitUntil(() => State(orch, "t1") == ConnectionState.Running, 3000);
    await orch.DisposeAsync();
}
```
辅助（若本类无则加私有）：
```csharp
private static ConnectionState State(TaskOrchestrator o, string id)
    => o.GetDiagnostics().Single(x => x.TaskId == id).State;
private static async Task WaitUntil(Func<bool> cond, int ms)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(20);
    Assert.True(cond(), $"条件未在 {ms}ms 内满足");
}
```

- [ ] **步骤 2：运行验证失败**（Linux）：FAIL（重绑逻辑/State 转移/factory 开关不存在）。

- [ ] **步骤 3：给 FakeOpcSubscriberFactory 加「未来 create 抛连接」开关**

`tests/Dc.Infrastructure.Tests/Fakes/FakeOpcSubscriberFactory.cs`：
```csharp
public bool ThrowOnConnectForFutureCreates { get; set; }
// Create 内：var sub = new FakeOpcSubscriber(channelId, options) { ThrowOnConnect = ThrowOnConnectForFutureCreates }; ...
```
（在现有 `Create` 里给新建的 sub 设 `ThrowOnConnect`。）

- [ ] **步骤 4：实现原地重绑 RestartIfStaleAsync**

替换现有 `RestartIfStaleAsync` 整体为：
```csharp
private async Task RestartIfStaleAsync(string taskId)
{
    await _mutationLock.WaitAsync(_hostCts.Token).ConfigureAwait(false);
    try
    {
        if (!_running.TryGetValue(taskId, out var rt)) return;
        if (DateTimeOffset.UtcNow - rt.LastHeartbeat <= _options.HeartbeatTimeout) return;
        if (!_factories.TryGetValue(rt.Request.Protocol, out var factory)) return;

        var req = rt.Request;
        SetState(rt, ConnectionState.Restarting);
        rt.LastRestartAt = DateTimeOffset.UtcNow;
        rt.ConsecutiveStaleRestarts++;
        rt.RestartCount++;
        _logger?.LogWarning("任务 {TaskId} ({Protocol}) 心跳超时（>{Timeout}），看门狗原地重连（第 {Count} 次）",
            taskId, req.Protocol, _options.HeartbeatTimeout, rt.RestartCount);

        // 拆旧管道（保持 rt 在 _running）
        rt.Cts.Cancel();
        try { await rt.PipelineTask.WaitAsync(_options.StopDrainTimeout).ConfigureAwait(false); } catch { /* 取消/超时正常 */ }
        await SafeDisposeAsync(rt.Subscriber).ConfigureAwait(false);
        try { rt.Cts.Dispose(); } catch { }

        // 重连
        var subscriber = factory.Create(req.TaskId, req.OpcOptions);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);
        try
        {
            await subscriber.ConnectAsync(_hostCts.Token).ConfigureAwait(false);
            await subscriber.SubscribeAsync(req.Tags, _hostCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "任务 {TaskId} 重连失败 → 标记故障，留在运行集等下次看门狗重试", taskId);
            await SafeDisposeAsync(subscriber).ConfigureAwait(false);
            cts.Dispose();
            // rt 留在 _running，置 Faulted；占位 Cts/PipelineTask 供后续 Stop 安全；推回心跳让下次 tick 再试。
            rt.PipelineTask = Task.CompletedTask;
            rt.Cts = new CancellationTokenSource();
            SetState(rt, ConnectionState.Faulted);
            rt.LastHeartbeat = DateTimeOffset.UtcNow - _options.HeartbeatTimeout - TimeSpan.FromSeconds(1);
            return;
        }

        // 重绑成功
        rt.Subscriber = subscriber;
        rt.Cts = cts;
        rt.LastHeartbeat = DateTimeOffset.UtcNow;
        rt.PipelineTask = Task.Run(() => RunPipelineAsync(rt, cts.Token));
        SetState(rt, rt.ConsecutiveStaleRestarts >= _options.FaultThreshold
            ? ConnectionState.Faulted   // 重连上了但反复超时 → 仍标故障，待心跳确认恢复
            : ConnectionState.Running);
    }
    finally { _mutationLock.Release(); }
}
```

- [ ] **步骤 5：实现心跳恢复（pipeline 心跳回调里）**

`RunPipelineAsync` 的 `heartTask = ConsumeAsync(rt.Subscriber.Heartbeats, h => {...})` 回调里，更新 `rt.LastHeartbeat = h.Time` 后追加：
```csharp
            rt.LastHeartbeat = h.Time;
            // 收到新鲜心跳 → 重启计数归零、若处于 Restarting/Faulted 则确认恢复
            if (rt.ConsecutiveStaleRestarts > 0 || rt.State is ConnectionState.Faulted or ConnectionState.Restarting)
            {
                rt.ConsecutiveStaleRestarts = 0;
                SetState(rt, ConnectionState.Running);
            }
```
> `SetState` 用 `_stateLock` 串行化「重启路径置 Restarting/Faulted」与「心跳路径置 Running」，避免恢复覆盖重启中。`ConsecutiveStaleRestarts` 的写：重启路径在 `_mutationLock` 内、心跳路径在 pipeline 单线程——理论可并发，但重启进行中旧 pipeline 已取消、新 pipeline 尚未起，窗口极小；如需绝对安全，归零也放进 `lock(_stateLock)`。本计划：归零与 SetState 一并放 `lock(_stateLock)`。

修订后心跳回调（含锁）：
```csharp
            rt.LastHeartbeat = h.Time;
            lock (_stateLock)
            {
                if (rt.ConsecutiveStaleRestarts > 0 || rt.State is ConnectionState.Faulted or ConnectionState.Restarting)
                {
                    rt.ConsecutiveStaleRestarts = 0;
                    rt.State = ConnectionState.Running;
                }
            }
```

- [ ] **步骤 6：StopUnlockedAsync 容错 faulted rt（二次 dispose 幂等）**

确认 `StopUnlockedAsync` 对「故障路径已 dispose subscriber、占位 Cts」的 rt 安全：`rt.Cts.Cancel()`（占位 CTS 可取消）、`await PipelineTask`（CompletedTask）、`SafeDisposeAsync(rt.Subscriber)`（FakeOpcSubscriber.DisposeAsync 幂等；`SafeDisposeAsync` 已 try 包裹）。若现有 StopUnlocked 直接 `rt.Cts.Cancel()` 无 try——占位 CTS 未 dispose 故 Cancel 安全，无需改。读 StopUnlocked 确认；若它假设 PipelineTask 仍在跑而无超时，沿用现有 `StopDrainTimeout`。

- [ ] **步骤 7：运行验证通过**（Linux）

```bash
export DOTNET_ROOT=$HOME/.dotnet
~/.dotnet/dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --nologo
```
预期：三新测 + 现有 watchdog 测全绿。**现有 `Watchdog_RestartsTask_WhenHeartbeatStale` 等可能因重启语义改变需同步调整断言（仍应：心跳停→RestartCount 增）——若失败，对齐新重绑语义修正断言（不是放松，是语义更新）。**

- [ ] **步骤 8：Commit**
```bash
git add src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs tests/Dc.Infrastructure.Tests/Fakes/FakeOpcSubscriberFactory.cs
git commit -m "✨ feat(diag): 看门狗原地重绑重启——状态可观测 + Faulted + 心跳恢复 + 修消失缺陷"
```

---

## 任务 4：/metrics 暴露 dc_collector_task_state（双路径）

**文件：** 改 `MetricsHttpServer.cs`（RenderPrometheus）、`DiagnosticsReporter.cs`（Meter 镜像）；测试 `MetricsHttpServerTests.cs`。

- [ ] **步骤 1：写失败测试**
```csharp
[Fact]
public void RenderPrometheus_EmitsTaskState()
{
    var tasks = new[]
    {
        new TaskDiagnostics("T1", Now, Now, Now, 5,0,0,3, State: ConnectionState.Running),
        new TaskDiagnostics("T2", Now, Now, Now, 0,0,2,1, State: ConnectionState.Faulted),
    };
    var text = MetricsHttpServer.RenderPrometheus(tasks, Now);
    Assert.Contains("# TYPE dc_collector_task_state gauge", text);
    Assert.Contains("dc_collector_task_state{task_id=\"T1\",state=\"running\"} 1", text);
    Assert.Contains("dc_collector_task_state{task_id=\"T2\",state=\"faulted\"} 1", text);
}
```
> `TaskDiagnostics` 用命名参数 `State:`（位置参数太多易错）。`Now` 为类内固定时间。

- [ ] **步骤 2：运行验证失败**（Linux）。

- [ ] **步骤 3：实现渲染**

`RenderPrometheus` 在 collector gauge 段追加（return 前；用 `g.Line` 的标签能力——需确认 `GaugeWriter.Line` 是否支持额外标签。**现有 Line 只支持 task_id 单标签**，故 state 维度需自定义写法）：
```csharp
        // 连接状态：每任务一行，state 标签为枚举小写名，值恒 1（表当前态）。
        Gauge(sb, "dc_collector_task_state", "每任务连接状态（state 标签：connecting/running/restarting/faulted）。",
            g =>
            {
                foreach (var d in snap)
                    g.LineKv(("task_id", d.TaskId), ("state", d.State.ToString().ToLowerInvariant()));
            });
```
若 `GaugeWriter` 无多标签写法，给它加一个 `LineKv(params (string Key, string Value)[] labels)`（值恒 1）方法（仿现有 `Line` 拼 `name{k1="v1",k2="v2"} 1`，标签值走现有转义逻辑）。先读 `GaugeWriter` 现有 `Line` 实现照其转义/格式拼多标签。

- [ ] **步骤 4：Meter 镜像（双路径约定）**

`DiagnosticsReporter.cs`（Meter 侧）：按现有 collector instrument 模式加对应 observable gauge `dc_collector_task_state`（带 task_id+state 标签，值 1）。先读 DiagnosticsReporter 现有 instrument 注册方式，照其加。**若 DiagnosticsReporter 的 Meter 仪表是按 TaskDiagnostics 快照发布**，加一个 observable 维度即可。保持指标名与 /metrics 一致（memory：双路径镜像）。

- [ ] **步骤 5：运行验证通过**（Linux）：渲染测过 + 现有 metrics 测不回归。

- [ ] **步骤 6：Commit**
```bash
git add src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs src/Dc.Infrastructure/Orchestration/DiagnosticsReporter.cs tests/Dc.Infrastructure.Tests/Orchestration/MetricsHttpServerTests.cs
git commit -m "✨ feat(diag): /metrics 暴露 dc_collector_task_state（双路径镜像）"
```

---

## 任务 5：InjectFault 注入缝 + /debug/fault 门控端点 + 装配

**文件：** 改 `TaskOrchestrator.cs`、`MetricsHttpServer.cs`、`ServiceRegistration.cs`；测试 `tests/Dc.Integration.Tests/Infrastructure/MetricsHttpServerE2ETests.cs`、`TaskOrchestratorTests.cs`。

- [ ] **步骤 1：写失败测试（注入缝 + 端点 e2e）**

注入缝测（`TaskOrchestratorTests`）：
```csharp
[Fact(Timeout = 10_000)]
public async Task InjectFault_Stall_PushesHeartbeatStale_AndHitsTask()
{
    var (orch, _, _) = Build(new OrchestratorOptions {
        WatchdogInterval = TimeSpan.FromMilliseconds(50), HeartbeatTimeout = TimeSpan.FromMilliseconds(120) });
    await orch.StartAsync(DaReq("t1"));
    Assert.True(orch.InjectFault("t1", "stall"));
    Assert.False(orch.InjectFault("nope", "stall"));
    // stall 后看门狗应触发重启（RestartCount 增）
    await WaitUntil(() => orch.GetDiagnostics().Single(x=>x.TaskId=="t1").RestartCount >= 1, 3000);
    await orch.DisposeAsync();
}
```
端点 e2e（`MetricsHttpServerE2ETests`，仿 DebugStress 测）：
```csharp
[Fact(Timeout = 15_000)]
public async Task DebugFault_404_Without_Injector_405_On_Get_And_200_With_Injector()
{
    var portA = GetFreePort();
    using (var noInj = new MetricsHttpServer(Sample, new MetricsServerOptions{Enabled=true, Prefix=$"http://127.0.0.1:{portA}/"}))
    {
        await noInj.StartAsync(CancellationToken.None);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var r = await http.PostAsync($"http://127.0.0.1:{portA}/debug/fault?task=t1&kind=stall", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        await noInj.StopAsync(CancellationToken.None);
    }
    (string Task, string Kind) got = default;
    Func<string,string,bool> injector = (t,k) => { got = (t,k); return true; };
    var portB = GetFreePort();
    using (var withInj = new MetricsHttpServer(Sample, new MetricsServerOptions{Enabled=true, Prefix=$"http://127.0.0.1:{portB}/"}, faultInjector: injector))
    {
        await withInj.StartAsync(CancellationToken.None);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using (var g = await http.GetAsync($"http://127.0.0.1:{portB}/debug/fault?task=t1&kind=stall"))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, g.StatusCode);
        using (var p = await http.PostAsync($"http://127.0.0.1:{portB}/debug/fault?task=t1&kind=stall", null))
        {
            Assert.Equal(HttpStatusCode.OK, p.StatusCode);
            Assert.Contains("\"injected\":true", await p.Content.ReadAsStringAsync());
        }
        Assert.Equal(("t1","stall"), got);
        await withInj.StopAsync(CancellationToken.None);
    }
}
```
> `using` 块包住 server 确保 Dispose；沿用该文件 `Sample`/`GetFreePort` helper。

- [ ] **步骤 2：运行验证失败**（Linux）。

- [ ] **步骤 3：实现 InjectFault（编排器）**
```csharp
/// <summary>调试用：强制某任务进故障路径。kind="stall" 把心跳推回，使下次看门狗判超时→原地重连。
/// 命中返回 true。仅门控后被调用，绝不进产线路径。</summary>
internal bool InjectFault(string taskId, string kind)
{
    if (!_running.TryGetValue(taskId, out var rt)) return false;
    switch (kind)
    {
        case "stall":
            rt.LastHeartbeat = DateTimeOffset.UtcNow - _options.HeartbeatTimeout - TimeSpan.FromSeconds(1);
            return true;
        default:
            return false;
    }
}
```

- [ ] **步骤 4：实现 /debug/fault 端点（MetricsHttpServer）**

加字段 + 构造末位参数：
```csharp
private readonly Func<string, string, bool>? _faultInjector; // (taskId,kind)->hit
// 构造末位加：Func<string,string,bool>? faultInjector = null → _faultInjector = faultInjector;
```
`Handle` switch 加（仿 /debug/stress 的门控+405）：
```csharp
            case "/debug/fault":
                if (_faultInjector is null) { Write(ctx, 404, "text/plain; charset=utf-8", "not found"); break; }
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, "text/plain; charset=utf-8", "method not allowed"); break; }
                var fq = ctx.Request.QueryString;
                var ftask = fq["task"] ?? "";
                var fkind = fq["kind"] ?? "stall";
                var hit = _faultInjector(ftask, fkind);
                Write(ctx, 200, "application/json; charset=utf-8",
                    $"{{\"injected\":{(hit ? "true" : "false")},\"task\":\"{ftask}\",\"kind\":\"{fkind}\"}}");
                break;
```
启动日志端点列表 `_faultInjector is not null` 时附 ` /debug/fault`。
> `ftask` 来自 query，拼进 JSON——理论注入面：仅门控调试端点、值是 task id（无引号转义风险低），但为稳妥用现有字符串拼接同 /debug/stress（数值）不同，**对 ftask 做最简转义**：`ftask.Replace("\"","")`（去引号防破坏 JSON）。

- [ ] **步骤 5：装配门控（ServiceRegistration，复用 DC_DEBUG_STRESS）**

在现有 MetricsHttpServer 工厂 lambda 里，stressRunner 旁加：
```csharp
    Func<string, string, bool>? faultInjector = stressEnabled
        ? (task, kind) => orch.InjectFault(task, kind)
        : null;
    return new MetricsHttpServer(
        orch.GetDiagnostics, sp.GetRequiredService<MetricsServerOptions>(),
        sp.GetService<...ILogger<MetricsHttpServer>>(),
        Dc.App.Services.Diagnostics.WpfScreenshot.Capture,
        liveFlushProvider: () => sp.GetService<LiveDataViewModel>()?.GetFlushStats(),
        stressRunner: stressRunner,
        faultInjector: faultInjector);
```
`orch.InjectFault` 是 internal，靠现有 `InternalsVisibleTo("Dc.App")` 可见。

- [ ] **步骤 6：运行验证通过**（Linux：Infra + e2e）+ office 编译 Dc.App（装配）。
```bash
~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter "FullyQualifiedName~MetricsHttpServerE2E" --nologo
~/.dotnet/dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --nologo
# 控制者另在 office 跑 build src/Dc.App 验装配
```

- [ ] **步骤 7：Commit**
```bash
git add src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs src/Dc.App/Composition/ServiceRegistration.cs tests/Dc.Integration.Tests/Infrastructure/MetricsHttpServerE2ETests.cs tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs
git commit -m "✨ feat(diag): InjectFault 注入缝 + /debug/fault 门控端点 + 装配"
```

---

## 任务 6：DiagnosticsRowViewModel State + 已恢复瞬态

**文件：** 改 `src/Dc.App/ViewModels/DiagnosticsRowViewModel.cs`；测试 `tests/Dc.App.Tests/ViewModels/DiagnosticsRowRecoveryTests.cs`（新建）。office 验证。

- [ ] **步骤 1：写失败测试**
```csharp
using Dc.App.ViewModels;
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.App.Tests.ViewModels;

public class DiagnosticsRowRecoveryTests
{
    private static TaskDiagnostics D(string id, int restart, ConnectionState s)
        => new(id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0,0,restart,1, State: s);

    [Fact]
    public void Apply_RestartingToRunning_WithRestartIncrease_SetsJustRecovered()
    {
        var vm = new DiagnosticsRowViewModel();
        vm.Apply(D("t1", 0, ConnectionState.Running));
        vm.Apply(D("t1", 1, ConnectionState.Restarting));
        vm.Apply(D("t1", 1, ConnectionState.Running));   // 重启后恢复
        Assert.True(vm.JustRecovered);
        Assert.Equal(ConnectionState.Running, vm.State);
        vm.RecoveryTickForTest(); // 模拟 5s 到点
        Assert.False(vm.JustRecovered);
    }

    [Fact]
    public void Apply_RunningToRunning_DoesNotRecover()
    {
        var vm = new DiagnosticsRowViewModel();
        vm.Apply(D("t1", 0, ConnectionState.Running));
        vm.Apply(D("t1", 0, ConnectionState.Running));
        Assert.False(vm.JustRecovered);
    }
}
```

- [ ] **步骤 2：运行验证失败**（office）。

- [ ] **步骤 3：实现 State + JustRecovered 瞬态**

`DiagnosticsRowViewModel` 加：
```csharp
[ObservableProperty] private ConnectionState _state;
[ObservableProperty] private bool _justRecovered;

private const int RecoverySeconds = 5;
private int _recoveryTicksLeft;

public void Apply(TaskDiagnostics d)
{
    // ... 现有赋值 ...
    var prevState = State;
    var prevRestart = RestartCount;   // 注意：在现有 RestartCount=d.RestartCount 赋值之前取
    State = d.State;
    // 已恢复检测：重启/故障 → 运行 且 重启数增加
    if (prevState is ConnectionState.Restarting or ConnectionState.Faulted
        && d.State == ConnectionState.Running
        && d.RestartCount > prevRestart)
    {
        JustRecovered = true;
        _recoveryTicksLeft = RecoverySeconds;
    }
    // ... 现有 RestartCount/HeartbeatSeverity 等赋值（确保 prevRestart 在 RestartCount 被覆盖前已取） ...
}

// 由诊断轮询每秒驱动（DiagnosticsViewModel 每次 Apply 后调用），或单测直接调。
public void TickRecovery()
{
    if (_recoveryTicksLeft > 0 && --_recoveryTicksLeft == 0) JustRecovered = false;
}
internal void RecoveryTickForTest() { _recoveryTicksLeft = 1; TickRecovery(); }
```
> 「已恢复」5s 倒计时用诊断页现有 ~1s 轮询驱动 `TickRecovery()`（在 `DiagnosticsViewModel` 刷新循环里对每行调一次）——无需独立 DispatcherTimer。读 `DiagnosticsViewModel` 刷新处，在每轮对 `Rows` 各行调 `TickRecovery()`。`prevRestart` 必须在现有 `RestartCount = d.RestartCount` 之前取值——调整赋值顺序。

- [ ] **步骤 4：DiagnosticsViewModel 刷新循环调 TickRecovery**

`DiagnosticsViewModel` 每轮刷新（第 82 行附近从 GetDiagnostics 重建后），对每个现存行调 `row.TickRecovery()`。读该刷新方法，在 Apply 各行后加一遍 tick（或在没有新快照的行上也 tick）。

- [ ] **步骤 5：运行验证通过**（office：`Dc.App.Tests`）。

- [ ] **步骤 6：Commit**
```bash
git add src/Dc.App/ViewModels/DiagnosticsRowViewModel.cs src/Dc.App/ViewModels/DiagnosticsViewModel.cs tests/Dc.App.Tests/ViewModels/DiagnosticsRowRecoveryTests.cs
git commit -m "✨ feat(ui): 诊断行 ConnectionState + 已恢复瞬态"
```

---

## 任务 7：DiagnosticsView 状态徽章列

**文件：** 改 `src/Dc.App/Views/DiagnosticsView.xaml`（+ 可能新增 `ConnectionStateToBrush` 转换器或纯 DataTrigger）。office 截图验证。

- [ ] **步骤 1：加「状态」列（DataGridTemplateColumn，pill + DataTrigger）**

在「任务」列后插入状态列，pill 复用 LiveData 质量 pill 风格，按 `State` 用 `DataTrigger` 切文案/色：
```xml
<DataGridTemplateColumn Header="状态" Width="96" SortMemberPath="State">
  <DataGridTemplateColumn.CellTemplate>
    <DataTemplate>
      <Border CornerRadius="999" Padding="8,2" HorizontalAlignment="Left" VerticalAlignment="Center" Margin="8,4">
        <Border.Style>
          <Style TargetType="Border">
            <Setter Property="Background" Value="{DynamicResource SystemFillColorSuccessBackgroundBrush}" />
            <Style.Triggers>
              <DataTrigger Binding="{Binding State}" Value="Connecting"><Setter Property="Background" Value="{DynamicResource SystemFillColorCautionBackgroundBrush}" /></DataTrigger>
              <DataTrigger Binding="{Binding State}" Value="Restarting"><Setter Property="Background" Value="{DynamicResource SystemFillColorCautionBackgroundBrush}" /></DataTrigger>
              <DataTrigger Binding="{Binding State}" Value="Faulted"><Setter Property="Background" Value="{DynamicResource SystemFillColorCriticalBackgroundBrush}" /></DataTrigger>
            </Style.Triggers>
          </Style>
        </Border.Style>
        <TextBlock FontSize="11" FontWeight="SemiBold">
          <TextBlock.Style>
            <Style TargetType="TextBlock">
              <Setter Property="Text" Value="运行正常" /><Setter Property="Foreground" Value="{DynamicResource SystemFillColorSuccessBrush}" />
              <Style.Triggers>
                <DataTrigger Binding="{Binding State}" Value="Connecting"><Setter Property="Text" Value="连接中" /><Setter Property="Foreground" Value="{DynamicResource SystemFillColorCautionBrush}" /></DataTrigger>
                <DataTrigger Binding="{Binding State}" Value="Restarting"><Setter Property="Text" Value="重启中" /><Setter Property="Foreground" Value="{DynamicResource SystemFillColorCautionBrush}" /></DataTrigger>
                <DataTrigger Binding="{Binding State}" Value="Faulted"><Setter Property="Text" Value="故障" /><Setter Property="Foreground" Value="{DynamicResource SystemFillColorCriticalBrush}" /></DataTrigger>
              </Style.Triggers>
            </Style>
          </TextBlock.Style>
        </TextBlock>
      </Border>
    </DataTemplate>
  </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```
- [ ] **步骤 2：「已恢复」绿闪标**

状态列右侧或同 cell 叠加：`Visibility` 绑 `JustRecovered`（`BooleanToVisibilityConverter`，诊断页若已有则复用）的小绿标「✓ 已恢复」。
```xml
<!-- 同 cell 内并排，或独立窄列 -->
<TextBlock Text="✓ 已恢复" FontSize="11" Foreground="{DynamicResource SystemFillColorSuccessBrush}"
           VerticalAlignment="Center" Margin="6,0,0,0"
           Visibility="{Binding JustRecovered, Converter={StaticResource BoolToVis}}" />
```
> 确认 DiagnosticsView 是否已有 `BooleanToVisibilityConverter`/`BoolToVis` 资源；无则加（WPF 内置 `BooleanToVisibilityConverter`）。

- [ ] **步骤 3：office 构建 + 截图人工核**（dc-remote `sync`/`build`/`run` 或留待任务 8 的 fault 演示统一截）。`DataTrigger` 绑枚举值用枚举成员名字符串（`Value="Faulted"`），WPF 支持。

- [ ] **步骤 4：Commit**
```bash
git add src/Dc.App/Views/DiagnosticsView.xaml
git commit -m "✨ feat(ui): 诊断页连接状态徽章列 + 已恢复绿闪"
```

---

## 任务 8：dc-remote fault 子命令（office 活体演示）

**文件：** 改 `~/.claude/skills/dc-remote/scripts/dc-remote.sh`（不在本仓库，按 dc-remote skill 既有方式管理）。

- [ ] **步骤 1：加 fault 子命令**

在 case 派发加（仿 stress；前置：需先有一个真采集任务在跑——文档/参数说明）：
```bash
  fault)
    task="${1:-}"; n="${2:-3}"
    [ -n "$task" ] || { echo "用法: dc-remote.sh $PROFILE fault <taskId> [stall次数]"; exit 1; }
    echo "== fault @ $PROFILE: task=$task stall x$n =="
    # 前置：App 已以 DC_DEBUG_STRESS=1 起且该 task 在跑（由 stress/run 起，或用户已建任务）。
    # 导航诊断页
    UIPS='& "$env:USERPROFILE\.dc-remote\ui.ps1"'
    _desktop_run "$UIPS -Op click -Target '诊断'" >/dev/null 2>&1 || true
    sleep 1
    # 反复 stall 逼到 Restarting/Faulted（带 & 的 URL 走 powershell 双引号块）
    for i in $(seq 1 "$n"); do
      $SSH "powershell -NoProfile -Command \"try{ (Invoke-WebRequest -UseBasicParsing -Method POST 'http://localhost:9090/debug/fault?task=$task&kind=stall' -TimeoutSec 5).Content }catch{ 'FAULT_FAIL '+\$_.Exception.Message }\"" 2>&1 | filt
      sleep 2
      st=$($SSH "curl.exe -s http://localhost:9090/metrics" 2>/dev/null | grep -aE "dc_collector_task_state\{task_id=\"$task\"" )
      echo "  [stall $i] $st"
      "$0" "$PROFILE" shot >/dev/null 2>&1 && echo "  截图 -> /tmp/dc-$PROFILE-screen.png"
    done
    # 停 stall 等恢复
    echo "-- 停止 stall，等心跳恢复 --"
    sleep 3
    st=$($SSH "curl.exe -s http://localhost:9090/metrics" 2>/dev/null | grep -aE "dc_collector_task_state\{task_id=\"$task\"" )
    echo "  [recovered?] $st"
    "$0" "$PROFILE" shot >/dev/null 2>&1
    echo "  终态截图 -> /tmp/dc-$PROFILE-screen.png"
    ;;
```
- [ ] **步骤 2：usage/help 文档加 fault 行**（仿 stress 行）。
- [ ] **步骤 3：office 实跑验收**

```
# 起 App（门控）+ 建并启动一个 UA 采集任务（指向可断的 server），或用合成 stress 起一个伪任务后...
dc-remote sync && dc-remote build
dc-remote fault <taskId> 3
```
预期：诊断页徽章经 运行正常→重启中→（连续）故障→（停 stall）已恢复→运行正常，逐张截图确认；`dc_collector_task_state` 标签随之变化。

> 注意：`stall` 注入的是真实采集任务（有 subscriber+watchdog），故需先有真任务。若当前无现成可断的 UA 任务，验收时先建一个指向本机/可控 UA server 的任务并启动。

- [ ] **步骤 4：Commit**（dc-remote 脚本按其仓库/同步机制，不入本仓库）
```bash
git -C ~/.claude/skills/dc-remote commit ... # 或按 dc-remote skill 既有提交方式
```

---

## 自检结论（计划编写者已执行）

**规格覆盖度：** §4.1→任务1；§4.3 初启 rt-first→任务2、原地重绑+恢复→任务3；§4.4→任务4；§5（UI）→任务6/7；§6（注入端点+dc-remote）→任务5/8；§8 测试分布对应各任务测试步骤。无遗漏。

**占位符扫描：** 各步含真实代码/命令。任务4 的 `GaugeWriter.LineKv` 多标签、任务6 的 `DiagnosticsViewModel` 刷新调 TickRecovery、任务3 的 `StopUnlockedAsync` 容错——均为「先读现有实现再对齐」的真实集成点，给了明确做法，非占位符。

**类型一致性：** `ConnectionState`(任务1)→全程一致；`TaskDiagnostics.State`(任务1)→GetDiagnostics 填(任务2)、渲染(任务4)、VM Apply(任务6)、测试构造(任务4/6 用命名参数 `State:`) 一致；`InjectFault(string,string)→bool`(任务5)→端点 faultInjector 签名 `Func<string,string,bool>`(任务5)→装配 lambda(任务5) 一致；`FaultThreshold`(任务1)→重启用(任务3) 一致；`ThrowOnConnectForFutureCreates`(任务3 给 factory 加)→测试用(任务3) 一致。

**关键风险（实现时重点）：**
1. 任务3 是 crux——原地重绑改重启热路径。现有 watchdog 测可能需按新语义更新断言（语义更新非放松）；故障路径的占位 Cts/二次 dispose 幂等需对照 StopUnlockedAsync 实现确认。
2. 任务4 `GaugeWriter` 多标签写法依赖现有 `Line` 实现——先读再决定加 `LineKv` 还是内联拼。
3. 任务6 `prevRestart` 必须在 `RestartCount` 被覆盖前取值（赋值顺序）。
4. 任务3 状态写竞态由 `_stateLock` 串行化（重启置 Restarting/Faulted vs 心跳置 Running）。
