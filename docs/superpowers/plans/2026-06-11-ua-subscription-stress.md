# 真实 OPC UA 订阅路径压测 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）逐任务实现。步骤用复选框（`- [ ]`）跟踪。

**目标：** 进程内真 UA server 造大量快变节点，在 `OpcUaSubscriber.TagValues` 输出层压测真订阅路径的交付吞吐 + 正确性；并用真 UA + 真 server 停启确定性验证连接状态徽章状态机。

**架构：** 扩 `MinimalUaNodeManager` 加 N 个单调计数器节点 + ticker（每拍全 +1）+ 暴露 `StressTickCount`；新增 `UaSubscriptionStressTests`（吞吐测 A + 正确性测 B，TagValues 层）；新增 `UaConnectionStateE2ETests`（编排器 + 真 UA factory + server 停启验 ConnectionState）。全 Linux 跑。

**技术栈：** OPC UA Foundation stack（Opc.Ua.Server CustomNodeManager2）、xUnit、`Channel<TagValue>`、TaskOrchestrator。

---

## 测试落机：全部本机 Linux
`export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj`（Dc.Integration.Tests 跨平台 net8.0）。时序敏感测串行。

## 已核实的现有结构（对齐，勿臆造）

- `MinimalUaNodeManager`（`tests/Dc.Integration.Tests/Ua/Fixtures/`，`internal sealed : CustomNodeManager2`，`TestNamespace="urn:dc:integrationtest:ua"`，NamespaceIndex 在客户端为 **ns=2**）：构造 `(IServerInternal server, ApplicationConfiguration configuration, int extraIntVars=0)`。`CreateAddressSpace` 里建 `Demo` folder + `_demoInt`（`Demo.Int32`）+ N 个静态 `Bench.{i}`，末尾 `AddPredefinedNode`，再建 `_ticker`（Timer 每 200ms：`lock(Lock){ _demoInt.Value=((int)Value)+1; Timestamp=UtcNow; StatusCode=Good; ClearChangeMasks(SystemContext,false);}`）。`Dispose(bool)` 里 `_ticker?.Dispose()`。
- `MinimalUaServer`（`: StandardServer`）：构造 `(int extraIntVars=0)`；`CreateMasterNodeManager` 里 `new MinimalUaNodeManager(server, configuration, _extraIntVars)`。
- `TestUaServerHost`：构造 `(int port, string? pkiRoot=null, int extraIntVars=0)`；`EndpointUrl => Endpoint.ToString()`；`StartAsync()` 里 `_server = new MinimalUaServer(_extraIntVars)`；`Stop()`（同步）；`DisposeAsync()`；static `FindFreePort()`。`UaReconnectTests` 用同一 host 实例 `Stop()` → `Task.Delay(3s)` → `StartAsync()` 同端口重起。
- 订阅 API：`OpcConnectionOptions { ServerUri, SamplingInterval, HeartbeatInterval, KeepAliveInterval, ReconnectPeriod }`（`SamplingInterval` 默认 1s）；`new OpcUaSubscriber(channelId, options)`；`await sub.ConnectAsync()`；`await sub.SubscribeAsync(new[]{ new TagDescriptor(Id, Item, DataType) })`（`TagDescriptor(string Id, string Item, int DataType)`，`Item="ns=2;s=Demo.Int32"`，DataType:0）；排空 `sub.TagValues.ReadAsync(ct)` / `TryRead(out _)`。`OpcUaSubscriber` 内 `PublishingInterval=SamplingInterval`、`QueueSize=1`。
- 编排器真 UA：`new TaskOrchestrator(new IOpcSubscriberFactory[]{ new OpcUaSubscriberFactory() }, publisherFactory, new OrchestratorOptions{...}, logger)`；`TaskStartRequest(string TaskId, OpcProtocol Protocol, OpcConnectionOptions OpcOptions, string PublisherAddress, IReadOnlyCollection<TagDescriptor> Tags)`；`await orch.StartAsync(req)`；`orch.GetDiagnostics().Single(x=>x.TaskId==id).State`（`ConnectionState` Connecting/Running/Restarting/Faulted）；`OrchestratorOptions` 有 `WatchdogInterval/HeartbeatTimeout/FaultThreshold/StopDrainTimeout`。
- `ConnectionState`/`TaskDiagnostics.State` 在 `Dc.Infrastructure.Orchestration`。`[Collection("Ua")]`（共享 server fixture）、`[Collection("Timing-Sensitive")]`（DisableParallelization）。

## 文件结构
- 改：`tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaNodeManager.cs`（压测计数器节点 + ticker + StressTickCount）、`MinimalUaServer.cs`、`TestUaServerHost.cs`（透传 stressNodes/stressTick + 暴露 StressTickCount）。
- 新增：`tests/Dc.Integration.Tests/Ua/UaSubscriptionStressTests.cs`（吞吐 A + 正确性 B）、`tests/Dc.Integration.Tests/Ua/UaConnectionStateE2ETests.cs`（真 UA 状态机）。

---

## 任务 1：节点发生器加压测计数器节点 + StressTickCount

**文件：** 改 `MinimalUaNodeManager.cs`、`MinimalUaServer.cs`、`TestUaServerHost.cs`；测试 `tests/Dc.Integration.Tests/Ua/Fixtures/`（新建小冒烟测或并入任务2，见步骤）。

- [ ] **步骤 1：写失败冒烟测（新建 `tests/Dc.Integration.Tests/Ua/StressNodesSmokeTests.cs`）**

```csharp
using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class StressNodesSmokeTests
{
    [Fact(Timeout = 30_000)]
    public async Task StressNodes_TickAndDeliverChangingValues_AndTickCountIncreases()
    {
        await using var host = new TestUaServerHost(TestUaServerHost.FindFreePort(),
            stressNodes: 10, stressTick: TimeSpan.FromMilliseconds(50));
        await host.StartAsync();

        await using var sub = new OpcUaSubscriber("stress-smoke", new OpcConnectionOptions
        {
            ServerUri = host.EndpointUrl,
            SamplingInterval = TimeSpan.FromMilliseconds(100),
            HeartbeatInterval = TimeSpan.FromSeconds(5),
        });
        await sub.ConnectAsync();
        await sub.SubscribeAsync(new[] { new TagDescriptor("s0", "ns=2;s=Stress.0", 0) });

        // 收两条「不同」的值，证明节点在变化（不是静态）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var v1 = await sub.TagValues.ReadAsync(cts.Token);
        object? first = v1.Value;
        object? second = first;
        while (Equals(second, first))
            second = (await sub.TagValues.ReadAsync(cts.Token)).Value;
        Assert.Equal("ns=2;s=Stress.0", v1.Item);

        Assert.True(host.StressTickCount > 0, "server 端 tick 计数应增长");
    }
}
```

- [ ] **步骤 2：运行验证失败**（Linux）：FAIL（`stressNodes`/`stressTick` 参数、`StressTickCount` 不存在）。

- [ ] **步骤 3：MinimalUaNodeManager 加压测节点 + ticker**

构造增参 + 字段：
```csharp
private readonly int _stressNodes;
private readonly TimeSpan _stressTick;
private BaseDataVariableState[]? _stressVars;
private Timer? _stressTicker;
private int _stressTickCount;

public MinimalUaNodeManager(IServerInternal server, ApplicationConfiguration configuration,
    int extraIntVars = 0, int stressNodes = 0, TimeSpan stressTick = default)
    : base(server, configuration, TestNamespace)
{
    _extraIntVars = extraIntVars;
    _stressNodes = stressNodes;
    _stressTick = stressTick == default ? TimeSpan.FromMilliseconds(50) : stressTick;
}

/// <summary>server 端压测计数器当前值（每拍全部 +1，故 = 每节点当前值）。供测试读最终值。</summary>
public int StressTickCount { get { lock (Lock) return _stressTickCount; } }
```
`CreateAddressSpace` 里，在 `AddPredefinedNode(SystemContext, folder)` **之前**建压测节点（仿 Bench 循环）：
```csharp
            if (_stressNodes > 0)
            {
                _stressVars = new BaseDataVariableState[_stressNodes];
                for (var i = 0; i < _stressNodes; i++)
                {
                    var sv = new BaseDataVariableState(folder)
                    {
                        NodeId = new NodeId($"Stress.{i}", NamespaceIndex),
                        BrowseName = new QualifiedName($"Stress.{i}", NamespaceIndex),
                        DisplayName = new LocalizedText($"Stress.{i}"),
                        DataType = DataTypeIds.Int32,
                        ValueRank = ValueRanks.Scalar,
                        AccessLevel = AccessLevels.CurrentRead,
                        UserAccessLevel = AccessLevels.CurrentRead,
                        Value = 0,
                        StatusCode = StatusCodes.Good,
                        Timestamp = DateTime.UtcNow
                    };
                    folder.AddChild(sv);
                    _stressVars[i] = sv;
                }
            }
```
在现有 `_ticker` 建立之后，追加压测 ticker（每拍全部 +1 并 ClearChangeMasks）：
```csharp
            if (_stressNodes > 0)
            {
                _stressTicker = new Timer(_ =>
                {
                    lock (Lock)
                    {
                        if (_stressVars is null) return;
                        _stressTickCount++;
                        foreach (var sv in _stressVars)
                        {
                            sv.Value = _stressTickCount;
                            sv.Timestamp = DateTime.UtcNow;
                            sv.StatusCode = StatusCodes.Good;
                            sv.ClearChangeMasks(SystemContext, includeChildren: false);
                        }
                    }
                }, state: null, dueTime: _stressTick, period: _stressTick);
            }
```
`Dispose(bool)` 里追加 `_stressTicker?.Dispose(); _stressTicker = null;`。

- [ ] **步骤 4：MinimalUaServer 透传 + 暴露 StressTickCount**
```csharp
private readonly int _stressNodes;
private readonly TimeSpan _stressTick;
private MinimalUaNodeManager? _nodeManager;

public MinimalUaServer(int extraIntVars = 0, int stressNodes = 0, TimeSpan stressTick = default)
{
    _extraIntVars = extraIntVars;
    _stressNodes = stressNodes;
    _stressTick = stressTick;
}

public int StressTickCount => _nodeManager?.StressTickCount ?? 0;
```
`CreateMasterNodeManager` 里捕获引用：
```csharp
        _nodeManager = new MinimalUaNodeManager(server, configuration, _extraIntVars, _stressNodes, _stressTick);
        var nodeManagers = new INodeManager[] { _nodeManager };
        return new MasterNodeManager(server, configuration, dynamicNamespaceUri: null, nodeManagers);
```

- [ ] **步骤 5：TestUaServerHost 透传 + 暴露 StressTickCount**
```csharp
// 构造增参：int stressNodes = 0, TimeSpan stressTick = default
public TestUaServerHost(int port, string? pkiRoot = null, int extraIntVars = 0,
    int stressNodes = 0, TimeSpan stressTick = default)
{
    Port = port;
    _extraIntVars = extraIntVars;
    _stressNodes = stressNodes;
    _stressTick = stressTick;
    // ...现有 pkiRoot 等不变...
}
public int StressTickCount => _server?.StressTickCount ?? 0;
// StartAsync 里：_server = new MinimalUaServer(_extraIntVars, _stressNodes, _stressTick);
```
（先读 TestUaServerHost 现有构造体/StartAsync，按实际加字段 `_stressNodes`/`_stressTick` 并在 `new MinimalUaServer(...)` 传入。）

- [ ] **步骤 6：运行验证通过 + 无回归**（Linux）
```bash
export DOTNET_ROOT=$HOME/.dotnet
~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter "FullyQualifiedName~Ua" --nologo
```
预期：冒烟测过 + 现有 UA 测（smoke/browse/batch/reconnect/headless）全不回归（stressNodes 默认 0，现有行为不变）。

- [ ] **步骤 7：Commit**
```bash
git add tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaNodeManager.cs tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaServer.cs tests/Dc.Integration.Tests/Ua/Fixtures/TestUaServerHost.cs tests/Dc.Integration.Tests/Ua/StressNodesSmokeTests.cs
git commit -m "✨ test(ua): 节点发生器加压测计数器节点 + ticker + StressTickCount"
```

---

## 任务 2：订阅吞吐（测A）+ 正确性（测B）

**文件：** 新增 `tests/Dc.Integration.Tests/Ua/UaSubscriptionStressTests.cs`。

- [ ] **步骤 1：写吞吐 + 正确性测**

```csharp
using System.Diagnostics;
using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class UaSubscriptionStressTests
{
    private readonly ITestOutputHelper _out;
    public UaSubscriptionStressTests(ITestOutputHelper o) => _out = o;

    private static TagDescriptor[] StressTags(int n)
    {
        var tags = new TagDescriptor[n];
        for (var i = 0; i < n; i++) tags[i] = new TagDescriptor($"s{i}", $"ns=2;s=Stress.{i}", 0);
        return tags;
    }

    [Fact(Timeout = 60_000)]
    public async Task Throughput_ManyNodes_DeliversNearNOverP()
    {
        const int N = 500;
        var P = TimeSpan.FromMilliseconds(100);
        var duration = TimeSpan.FromSeconds(10);

        await using var host = new TestUaServerHost(TestUaServerHost.FindFreePort(),
            stressNodes: N, stressTick: TimeSpan.FromMilliseconds(50));
        await host.StartAsync();

        await using var sub = new OpcUaSubscriber("stress-tp", new OpcConnectionOptions
        {
            ServerUri = host.EndpointUrl, SamplingInterval = P, HeartbeatInterval = TimeSpan.FromSeconds(5),
        });
        await sub.ConnectAsync();
        await sub.SubscribeAsync(StressTags(N));

        // 预热：等首批通知到达后再开始计时（排除订阅建立的冷启动）
        using (var warm = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            await sub.TagValues.ReadAsync(warm.Token);
        while (sub.TagValues.TryRead(out _)) { }

        long received = 0;
        var sw = Stopwatch.StartNew();
        using (var cts = new CancellationTokenSource(duration))
        {
            try { while (true) { await sub.TagValues.ReadAsync(cts.Token); received++; } }
            catch (OperationCanceledException) { }
        }
        sw.Stop();

        var thru = received / sw.Elapsed.TotalSeconds;
        _out.WriteLine($"N={N} P={P.TotalMilliseconds}ms received={received} throughput={thru:F0}/s (理论 N/P={N/P.TotalSeconds:F0}/s)");
        // 理论 5000/s；留一半余量防 CI 争用
        Assert.True(thru >= 2500, $"交付吞吐应 ≥2500/s，实测 {thru:F0}/s");
    }

    [Fact(Timeout = 60_000)]
    public async Task Correctness_PerNode_MonotonicAndFinalMatchesServer()
    {
        const int N = 100;
        var P = TimeSpan.FromMilliseconds(50);
        var duration = TimeSpan.FromSeconds(5);

        await using var host = new TestUaServerHost(TestUaServerHost.FindFreePort(),
            stressNodes: N, stressTick: TimeSpan.FromMilliseconds(50));
        await host.StartAsync();

        await using var sub = new OpcUaSubscriber("stress-correct", new OpcConnectionOptions
        {
            ServerUri = host.EndpointUrl, SamplingInterval = P, HeartbeatInterval = TimeSpan.FromSeconds(5),
        });
        await sub.ConnectAsync();
        await sub.SubscribeAsync(StressTags(N));

        var lastByNode = new Dictionary<string, int>();
        var monotonicViolations = 0;
        long total = 0;
        using (var cts = new CancellationTokenSource(duration))
        {
            try
            {
                while (true)
                {
                    var v = await sub.TagValues.ReadAsync(cts.Token);
                    total++;
                    var cur = Convert.ToInt32(v.Value);
                    if (lastByNode.TryGetValue(v.Item, out var prev) && cur < prev) monotonicViolations++;
                    lastByNode[v.Item] = cur;
                }
            }
            catch (OperationCanceledException) { }
        }

        // settle：停 ticker 不现实(server 持续 tick)，改为再 settle 2×P 排空后取每节点最后值，与 server 当前 StressTickCount 比
        await Task.Delay(TimeSpan.FromMilliseconds(P.TotalMilliseconds * 3));
        while (sub.TagValues.TryRead(out var v))
        {
            total++;
            lastByNode[v.Item] = Convert.ToInt32(v.Value);
        }
        var serverNow = host.StressTickCount;

        _out.WriteLine($"N={N} total={total} 单调违例={monotonicViolations} server当前={serverNow}");
        Assert.Equal(0, monotonicViolations); // 每节点单调非递减，无倒灌/重复倒序
        Assert.Equal(N, lastByNode.Count);     // 每个订阅节点都收到过值
        // 每节点最终收到值应接近 server 当前计数（持续 tick，留几拍容差）
        foreach (var kv in lastByNode)
            Assert.True(serverNow - kv.Value <= 5,
                $"{kv.Key} 最终值 {kv.Value} 落后 server {serverNow} 超过 5 拍");
    }
}
```
> 说明：server 持续 tick（无「停 ticker」接口，YAGNI 不加），故正确性测改为「最终收到值落后 server 当前计数 ≤ 几拍」+「单调非递减」，等价证明无协议外丢失/乱序。`Convert.ToInt32(v.Value)`：UA Int32 通过 SDK 可能是 `int`，Convert 兜底。

- [ ] **步骤 2：运行验证**（Linux）
```bash
export DOTNET_ROOT=$HOME/.dotnet
~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter "FullyQualifiedName~UaSubscriptionStress" --nologo
```
预期：两测过；输出实测吞吐（应 ≥2500/s，常见 ~5000/s）、单调违例 0。**若吞吐不达标或单调违例 >0，停下报告**——可能是 server tick 过载（500 节点×20/s ClearChangeMasks）或真 bug，需分析（调 stressTick 慢一点 vs 暴露真问题）。

- [ ] **步骤 3：Commit**
```bash
git add tests/Dc.Integration.Tests/Ua/UaSubscriptionStressTests.cs
git commit -m "✅ test(ua): 订阅路径吞吐(N/P 量级)+正确性(单调/最终值对)压测"
```

---

## 任务 3：编排器级真 UA 连接状态 E2E 测

**文件：** 新增 `tests/Dc.Integration.Tests/Ua/UaConnectionStateE2ETests.cs`。

- [ ] **步骤 1：写状态机测（真 UA + server 停启）**

```csharp
using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Dc.Integration.Tests.Ua;

// 真 OpcUaSubscriber 经编排器 + 真 server 停启，确定性验证连接状态徽章状态机。
// 时序敏感：server-down 须 > HeartbeatTimeout×(FaultThreshold+1) 才进 Faulted（subscriber 自身重连先于看门狗，处理瞬断）。
[Collection("Timing-Sensitive")]
public class UaConnectionStateE2ETests
{
    private readonly ITestOutputHelper _out;
    public UaConnectionStateE2ETests(ITestOutputHelper o) => _out = o;

    // 无操作 publisher：本测只关心连接状态，不关心发布。
    private sealed class NoopPublisher : IPublisher
    {
        public Task PublishAsync<T>(T message, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoopPublisherFactory : IPublisherFactory
    {
        public IPublisher Create(string address) => new NoopPublisher();
    }

    private static ConnectionState State(TaskOrchestrator o, string id)
        => o.GetDiagnostics().Single(x => x.TaskId == id).State;

    private static async Task WaitForState(TaskOrchestrator o, string id, ConnectionState want, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (o.GetDiagnostics().Any(x => x.TaskId == id && x.State == want)) return;
            await Task.Delay(50);
        }
        Assert.True(o.GetDiagnostics().Any(x => x.TaskId == id && x.State == want),
            $"{id} 未在 {ms}ms 内到达 {want}（当前 {(o.GetDiagnostics().FirstOrDefault(x=>x.TaskId==id)?.State.ToString() ?? "缺失")}）");
    }

    [Fact(Timeout = 120_000)]
    public async Task RealUaTask_ServerDownThenUp_TransitionsRunningFaultedRunning()
    {
        var port = TestUaServerHost.FindFreePort();
        var host = new TestUaServerHost(port);
        await host.StartAsync();

        await using var orch = new TaskOrchestrator(
            new IOpcSubscriberFactory[] { new OpcUaSubscriberFactory() },
            new NoopPublisherFactory(),
            new OrchestratorOptions
            {
                WatchdogInterval = TimeSpan.FromMilliseconds(500),
                HeartbeatTimeout = TimeSpan.FromSeconds(2),
                FaultThreshold = 2,
                StopDrainTimeout = TimeSpan.FromSeconds(1),
            },
            null);

        var req = new TaskStartRequest(
            TaskId: "ua-state",
            Protocol: OpcProtocol.Ua,
            OpcOptions: new OpcConnectionOptions
            {
                ServerUri = host.EndpointUrl,
                SamplingInterval = TimeSpan.FromMilliseconds(200),
                HeartbeatInterval = TimeSpan.FromMilliseconds(500),
                KeepAliveInterval = TimeSpan.FromMilliseconds(500),
                ReconnectPeriod = TimeSpan.FromMilliseconds(500),
            },
            PublisherAddress: "noop",
            Tags: new[] { new TagDescriptor("d", "ns=2;s=Demo.Int32", 0) });

        await orch.StartAsync(req);
        await WaitForState(orch, "ua-state", ConnectionState.Running, 15_000);
        _out.WriteLine("→ Running");

        // 停 server 并保持停：心跳停 → subscriber 自身重连失败 → 看门狗超时累计 → Faulted
        host.Stop();
        await WaitForState(orch, "ua-state", ConnectionState.Faulted, 30_000);
        Assert.Contains(orch.GetDiagnostics(), x => x.TaskId == "ua-state"); // 不消失
        _out.WriteLine("→ Faulted（server 停）");

        // 同端口重启 → 重连成功 + 心跳恢复 → Running，RestartCount 增
        await host.StartAsync();
        await WaitForState(orch, "ua-state", ConnectionState.Running, 30_000);
        var restarts = orch.GetDiagnostics().Single(x => x.TaskId == "ua-state").RestartCount;
        _out.WriteLine($"→ Running 恢复，RestartCount={restarts}");
        Assert.True(restarts >= 1, "应至少重启一次");

        await host.DisposeAsync();
    }
}
```
> 关键点：HeartbeatInterval/KeepAliveInterval 调到 500ms 让断线快速被探到；HeartbeatTimeout=2s + FaultThreshold=2 + WatchdogInterval=500ms → server 停约 2-6s 内进 Faulted，故 `WaitForState(Faulted, 30s)` 留足。`IPublisher`/`IPublisherFactory` 签名**已核实**：`IPublisher : IAsyncDisposable { Task PublishAsync<T>(T message, CancellationToken ct = default); }`、`IPublisherFactory { IPublisher Create(string address); }`——上面 NoopPublisher 已按此写（泛型 PublishAsync<T>）。`Dc.Infrastructure.Tests` 有 FakePublisher 但属另一程序集，本测内联 Noop 更简。

- [ ] **步骤 2：运行验证**（Linux）
```bash
export DOTNET_ROOT=$HOME/.dotnet
~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter "FullyQualifiedName~UaConnectionStateE2E" --nologo
```
预期：测过，输出 Running→Faulted→Running 序列 + RestartCount≥1。**时序敏感**：失败先重跑一次确认稳定性；若 Faulted 一直不到，检查心跳是否真停（subscriber `s.Connected` 判定）/ server 停得是否干净。**若真不稳或暴露真 bug，停下报告。**

- [ ] **步骤 3：Commit**
```bash
git add tests/Dc.Integration.Tests/Ua/UaConnectionStateE2ETests.cs
git commit -m "✅ test(ua): 编排器级真 UA 连接状态机(server 停启 Running→Faulted→恢复)"
```

---

## 自检结论（计划编写者已执行）

**规格覆盖度：** §4 节点发生器→任务1；§5 吞吐A+正确性B→任务2；§6 真 UA 状态机→任务3；§7 边界（默认关/地板余量/settle/端口复用/线程安全 ticker）分散在各任务的实现与断言。§8/§9 对应。无遗漏。

**占位符扫描：** 各步含真实代码/命令。任务3 的 `NoopPublisher`/`IPublisher` 签名「先读 Dc.Infrastructure.Messaging 确认」、任务1 的 TestUaServerHost 构造体「按实际加字段」——均为真实集成点，给了明确做法，非占位符。

**类型一致性：** `stressNodes/stressTick`(任务1 三处签名)一致；`StressTickCount`(任务1 暴露→任务2 读)一致；`TagDescriptor("ns=2;s=Stress.{i}")`/`OpcConnectionOptions`/`OpcUaSubscriber`(任务2)与现有 API 一致；`TaskStartRequest`/`TaskOrchestrator`/`ConnectionState`/`OrchestratorOptions`(任务3)与现有签名一致；`NoopPublisherFactory`(任务3 内定义自洽)。

**关键风险（实现时重点）：**
1. 任务2 吞吐：500 节点每 50ms 全 +1+ClearChangeMasks = server 端 ~10000 ops/s，可能过载致吞吐不达标——若不达 2500/s，先判 server 过载（放慢 stressTick 或减 N）vs 真订阅瓶颈，报告而非盲目放宽断言。
2. 任务3 时序：subscriber 自身重连(SessionReconnectHandler) vs 看门狗的竞争——server 必须停够久；HeartbeatInterval/KeepAlive 调快加速探测。失败先重跑判 flaky。
3. 任务3 `IPublisher.PublishAsync` 真实签名需现场核对（NoopPublisher 对齐）。
4. 现有 UA 测试不回归（stressNodes 默认 0 保证）。
