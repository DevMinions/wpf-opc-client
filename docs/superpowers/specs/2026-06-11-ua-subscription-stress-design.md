# 真实 OPC UA 订阅路径压测 设计规格

- 日期：2026-06-11
- 范围：`tests/Dc.Integration.Tests/Ua/`（节点发生器扩展 + 订阅吞吐/正确性压测 + 编排器级真 UA 连接状态测）。被测代码 `Dc.Opc.Ua`（OpcUaSubscriber）、`Dc.Infrastructure`（TaskOrchestrator）均跨平台 net8.0，**本机 Linux 可压**。
- 目标：前三轮（批量读 / LiveData 规模化 / 连接状态徽章）的性能与可靠性都用**合成注入**（绕过真订阅路径）。这次压**真订阅路径本身**——进程内真 UA server 大量节点高频变化，验证 `OpcUaSubscriber` 在真实负载下的**交付吞吐天花板 + 正确性**；并用真 UA + 真 server 停启**确定性证明连接状态徽章状态机**（补上 task8 活体演示缺的真任务维度）。
- 主线：本轮真正交付物仍是打磨 **dc-remote skill**（本方向是纯 Linux 集成测，dc-remote 介入少；但产出的真 UA 任务能力为后续 dc-remote 活体演示铺路）。

## 1. 背景与现状（已核实）

- **OpcUaSubscriber 订阅参数**（`src/Dc.Opc.Ua/OpcUaSubscriber.cs`）：`Subscription.PublishingInterval = (int)_options.SamplingInterval.TotalMilliseconds`；每 `MonitoredItem.SamplingInterval` 同值；`QueueSize = 1`；`MaxNotificationsPerPublish = 1000`。`OnNotification` 把每个交付的 `DataValue` 写进无界 `Channel<TagValue> _values`（`TagValues` reader）。心跳走 `_heartbeats`。
- **协议语义（关键）**：`QueueSize=1` + 采样/发布间隔 P → 节点变化快于 P 时，server 端**合并**，每发布周期只交付该节点最新值。故**交付吞吐 ≈ N 节点 × (1/P) values/s**（每节点每周期至多 1），**不是** N×(server tick 率)。中间值被合并是协议设计，非「丢失」。
- **现有 UA 测试基建**（`tests/Dc.Integration.Tests/Ua/`）：
  - `MinimalUaNodeManager`（CustomNodeManager2）：建 `_demoInt`（一个 ticker 每隔一会 +1）+ `_extraIntVars` 个**静态** `Bench.{i}` Int32（批量读用，不变）。构造参数 `extraIntVars`。
  - `MinimalUaServer`/`TestUaServerHost`/`EmbeddedUaServerFixture`：起进程内 server，`TestUaServerHost(port, extraIntVars)`、`FindFreePort()`。
  - 现有测试：`UaSubscriberSmokeTests`（订阅单 int 收到值）、`UaBrowserSmokeTests`、`UaBatchReadTests`（extraIntVars=1000 批量读基准）、`UaReconnectTests`（server 重启后自动重连）、`HeadlessCollectorE2ETests`（DB→采集→TCP 发布）。
  - **缺口**：server 端只有 1 个 ticker 变量；无「大量节点高频变化」能力；无真订阅路径的吞吐/正确性压测；连接状态徽章状态机只用 fake subscriber 测过（task3），无真 UA + 真 server 停启的端到端验证。
- `TaskOrchestrator`：真 UA 任务经 `StartAsync(TaskStartRequest{Protocol=Ua,...})`，内部用 `OpcUaSubscriberFactory` 建真 subscriber；`GetDiagnostics()` 暴露 `ConnectionState State`；看门狗心跳超时→原地重绑重连；`ConnectionState`：Connecting/Running/Restarting/Faulted。

## 2. 目标与非目标

**目标**
- 节点发生器能造 N 个单调计数器节点，按可配 tick 间隔变化，并暴露 server 端最终计数供正确性断言。
- 订阅吞吐压测：`OpcUaSubscriber` 订阅 N 节点，在 `TagValues` 输出层测**交付吞吐（values/s）+ 正确性（每节点单调非递减、不重复、最终值对）**。
- 编排器级真 UA 连接状态测：真 UA 任务 + 进程内 server 停/启 → 确定性断言 ConnectionState 转移（Running→Restarting/Faulted→恢复 Running）。
- 全部本机 Linux 跑（`Dc.Integration.Tests` 跨平台）。

**非目标（YAGNI）**
- 不测端到端经编排器+publisher 的吞吐（Q2 选订阅器输出层；headless E2E 已覆盖 DB→采集→发布的功能正确性）。
- 不测端到端延迟分布（Q1 选吞吐+正确性，非延迟）。
- 不做重连风暴/慢 server 的吞吐版（Q1 未选可靠性版；连接状态测覆盖基本停启恢复即可）。
- 不改 `OpcUaSubscriber`/编排器生产代码（除非压测暴露真 bug —— 那时另议）。
- 不引入 dc-remote 活体演示（纯 Linux 集成测；真任务能力为后续铺路但本轮不接 dc-remote）。

## 3. 决策（已与用户确认）
- 压测重点：吞吐天花板 + 正确性（非可靠性、非延迟）。
- 测量边界：`OpcUaSubscriber` 的 `TagValues` 输出层（隔离 UA 订阅路径，不混下游 TCP 发布）。
- 协议语义：交付吞吐 = N/P 量级；正确性 = 单调+不重复+最终值对（允许协议合并跳号，不算丢失）。
- 含编排器级真 UA 连接状态测（§4 决策点已确认纳入）。

## 4. 节点发生器扩展

文件：`tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaNodeManager.cs`（+ `MinimalUaServer.cs`/`TestUaServerHost.cs` 透传参数）

- 加压测计数器节点集（与现有 `_demoInt`/`Bench.{i}` 并存、独立）：
  - 构造增参 `int stressNodes = 0`、`TimeSpan stressTick = default`（默认不建压测节点，保持现有测试不变）。
  - `CreateAddressSpace` 里：`stressNodes > 0` 时建 N 个 `Stress.{i}`（i=0..N-1）Int32 标量节点，初值 0，存进 `BaseDataVariableState[] _stressVars`。
  - 一个 `Timer _stressTicker`（间隔 `stressTick`，默认 50ms）每拍给**全部 N 个 +1**（在 server 线程安全地更新 `Value` + `Timestamp`，仿现有 `_demoInt` ticker 写法），并 `_stressTickCount++` 记总拍数。
  - 暴露 `public int StressTickCount => _stressTickCount;`（= 每节点当前计数，因每拍全 +1）供测试读 server 端最终值。
- `TestUaServerHost`/`MinimalUaServer` 构造透传 `stressNodes`/`stressTick` 到 node manager。`FindFreePort()` 不变。

## 5. 订阅吞吐 + 正确性压测

文件：新增 `tests/Dc.Integration.Tests/Ua/UaSubscriptionStressTests.cs`（`[Collection("Ua")]` 或独立 host，跨平台 Linux）

- 用 `ITestOutputHelper` 输出指标。
- **测试 A — 吞吐天花板**（如 N=500 节点 @ tick 20ms，SamplingInterval P=100ms，duration 10s）：
  - 起 `TestUaServerHost(port, stressNodes:500, stressTick:20ms)`，`OpcUaSubscriber.ConnectAsync` + `SubscribeAsync(500 个 Stress.{i} 的 TagDescriptor，SamplingInterval=100ms)`。
  - 后台任务排空 `TagValues` reader 持续 duration，累计 `received` 总数 + 每节点最后值。
  - 计算 `throughput = received / duration.TotalSeconds`。
  - 断言：`throughput` ≥ 地板（理论 N/P = 500/0.1 = 5000/s，留余量断言 ≥ 2500/s，不锁死）；`ITestOutputHelper` 输出实测 values/s。
- **测试 B — 正确性**（中等档，如 N=100 @ tick 50ms，P=50ms，duration 5s）：
  - 同上订阅 + 排空，但按节点收集**完整收到序列**。
  - 排空结束后停 ticker、settle 1×P，再排空残余。
  - 断言每节点：收到序列**单调非递减**（`v[k] >= v[k-1]`，证不倒退/不重复倒灌）；**无相邻重复**（可选，QueueSize=1 同值不重发；若有重复也接受但记录）；**最终收到值** == server 端 `StressTickCount`（在 settle 后；允许 ±1 抖动）。
  - 跳号（协议合并）允许、计入「合并比」输出，不判失败。
- **边界**：空订阅、单节点冒烟可复用现有 `UaSubscriberSmokeTests`，不重复。

## 6. 编排器级真 UA 连接状态测

文件：新增 `tests/Dc.Integration.Tests/Ua/UaConnectionStateE2ETests.cs`（跨平台 Linux）

- 用**真 OpcUaSubscriber**（经 `TaskOrchestrator` + `OpcUaSubscriberFactory`）+ 进程内 server，确定性验证连接状态徽章状态机（之前 task3 是 fake subscriber，本测是真 UA）。
- **已核实的关键机制（影响测试时序）**：
  - **心跳源**：`HeartbeatLoopAsync` 每 `HeartbeatInterval` 拍，**仅当 `_session.Connected` 时**写心跳（`OpcUaSubscriber.cs:230-235`）。停 server → KeepAlive 探到死会话 → `Connected=false` → **心跳停**。✓ 看门狗据此可触发。
  - **双重重连（重要）**：OpcUaSubscriber 自己有会话级自动重连——`OnKeepAlive` 探到异常即启 `SessionReconnectHandler`（秒级，处理**瞬断**）；编排器看门狗是另一层（心跳超时→原地重绑，处理**持续断**）。**故：server 短暂停启（短于 HeartbeatTimeout）会被 subscriber 自己悄悄重连，编排器状态不进 Restarting**。要让编排器状态进 Restarting/Faulted，**server 必须停足够久（超 HeartbeatTimeout + 看门狗间隔，且久到 subscriber 自身重连尝试失败）**。这是真实的分层韧性，测试须据此设计。
- **测试**（WatchdogInterval/HeartbeatTimeout/FaultThreshold 调小加速；server-down 时长 > HeartbeatTimeout 的数倍）：
  1. 起进程内 server（在跑即有 KeepAlive→心跳）。
  2. `orch.StartAsync(UA 任务指向 server)` → `WaitFor(State==Running)`。
  3. **停 server 并保持停**（`host.DisposeAsync()`）→ 心跳停 → subscriber 自身重连失败（server 真没了）→ 心跳超时 > HeartbeatTimeout → 看门狗原地重绑重连，新 ConnectAsync 也失败 → `WaitFor(State==Faulted)`（连续失败累计达 FaultThreshold）。
  4. **同端口重启 server** → 看门狗下次 tick 重连成功（或 subscriber 自身重连成功）+ 心跳恢复 → `WaitFor(State==Running)`、`RestartCount` 增。
  - 断言全程状态转移如徽章语义；`ITestOutputHelper` 输出 RestartCount/状态序列。
- 注意：server 同端口重起（`TestUaServerHost` 固定端口）；端口释放可能有延迟，重起加重试/小延迟。**此测时序敏感** → `[Collection("Timing-Sensitive")]` 串行 + 宽松 WaitFor 超时（参考 `UaReconnectTests` 90s 稳健度）。server-down 时长须 > HeartbeatTimeout × (FaultThreshold+1) 量级，确保编排器进 Faulted。

## 7. 错误处理与边界
- 压测节点与现有 `_demoInt`/`Bench.{i}` 并存、默认关（`stressNodes=0`），不破坏现有 UA 测试。
- 吞吐地板断言留足余量（理论值/2），避免 CI 争用 flaky；时序测宽松超时 + 串行隔离。
- 正确性「最终值」断言留 settle 窗口（停 ticker 后等 1-2×P 再比），容 ±1。
- server 停/启端口复用：`TestUaServerHost` 固定端口重起；若端口未及时释放，重起加重试/小延迟。
- ticker 线程安全更新节点 Value（仿现有 `_demoInt` 写法，server 锁内或 node manager 约定）。

## 8. 测试（本规格产物即测试，元层面）
- 全部 `Dc.Integration.Tests`（net8.0 跨平台），本机 Linux：`export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj`。
- 吞吐测、正确性测、连接状态 E2E 测各自独立 `[Fact(Timeout=...)]`，时序敏感的串行隔离。
- 现有 UA 测试（smoke/browse/batch/reconnect/headless）必须不回归。

## 9. 涉及文件
- 改：`MinimalUaNodeManager.cs`（压测计数器节点 + ticker + StressTickCount）、`MinimalUaServer.cs`/`TestUaServerHost.cs`（透传 stressNodes/stressTick）。
- 新增：`UaSubscriptionStressTests.cs`（吞吐+正确性）、`UaConnectionStateE2ETests.cs`（编排器级真 UA 状态机）。

## 10. 验收标准
- 节点发生器能造 N 个单调计数器节点按 tick 变化，暴露 server 端最终计数。
- 订阅吞吐压测：真 `OpcUaSubscriber` 订阅 N 节点，`TagValues` 输出层测出 values/s（≥理论 N/P 的合理比例）+ 每节点单调/不重复/最终值对；输出实测吞吐与合并比。
- 编排器级真 UA 连接状态测：真 UA 任务经 server 停/启，确定性走 Running→Restarting/Faulted→恢复 Running，RestartCount 增。
- 协议合并语义在测试与文档中诚实表述（吞吐=N/P 量级，正确性≠每 tick 送达）。
- 现有 UA 测试不回归；全部 Linux 跑通。
