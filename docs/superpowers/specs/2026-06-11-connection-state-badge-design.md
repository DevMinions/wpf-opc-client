# 连接状态徽章（编排器拥有状态）设计规格

- 日期：2026-06-11
- 范围：`Dc.Infrastructure.Orchestration`（状态机 + 注入缝 + 端点）、`Dc.App`（徽章呈现 + 已恢复瞬态）、`Dc.Integration.Tests`/`Dc.Infrastructure.Tests`（状态转移 + 渲染测）、dc-remote skill（`fault` 子命令）。
- 目标：OPC server 掉线 / 下游 broker 不可达 / 看门狗重启时，每个采集任务有一个**清晰、可信的连接状态**（运行正常 / 连接中 / 重启中 / 故障 / 已停止 + 「已恢复」瞬态确认），用户一眼能读懂「断了 / 在重连 / 真宕机 / 恢复了」。
- 主线：本轮真正交付物是继续打磨 **dc-remote skill**（新增 `fault` 注入子命令）；连接状态 UX 是实战它的载体。

## 1. 背景与现状（已核实）

- **故障模型 = 看门狗粗粒度重启**：`IOpcSubscriber`（`Dc.Opc.Abstractions`）无连接状态信号，只有 `ConnectAsync/SubscribeAsync/UnsubscribeAsync` + `TagValues`/`Heartbeats` 两个 `ChannelReader`。故障靠 `TaskOrchestrator.WatchdogLoopAsync` 检测**心跳超时**（`now - LastHeartbeat > HeartbeatTimeout`）→ `RestartIfStaleAsync` 拆除+重建整个任务，`RestartCount++`。
- `RuntimeTask`（`rt`，编排器内部 private class）持有每任务运行态：`Subscriber/Publisher/Cts/PipelineTask/Tags/LastHeartbeat/StartedAt/LastValueAt/ValueCount/PublishErrorCount/RestartCount`。
- `GetDiagnostics()` 从 `rt` 组装 `TaskDiagnostics`（record：`TaskId/StartedAt/LastValueAt/LastHeartbeatAt/ValueCount/PublishErrorCount/RestartCount/SubscribedTagCount/QueuePendingBytes/DroppedFrameCount`）。
- `RestartIfStaleAsync` 锁内：重校验任务仍在 + 心跳确超时 → `StopUnlockedAsync` + `StartUnlockedAsync` → 回写 `RestartCount = prev+1`。
- **UI 现状**：`DiagnosticsRowViewModel.Apply(TaskDiagnostics)` 已派生 `HeartbeatSeverity`(0 正常 /1 >5s /2 >2min)、`HasErrors`、`ValuesPerSecond`、`RateHistory`。`HealthEvaluator` 算健康分 + 告警（Critical「已停止」、Warning「心跳延迟 Ns」「发送错误 N」）。**没有显式的每任务连接状态**——用户只能从健康分/告警/RestartCount 数字推断。
- `MetricsHttpServer`：`/metrics` 经 `RenderPrometheus(snap, now, live)` 渲染 collector 指标；门控压测端点 `/debug/stress`（`DC_DEBUG_STRESS=1` 时 `stressRunner` 非 null 才启用，否则 404）已建。指标双路径约定：collector 指标须在 DiagnosticsReporter Meter 与 RenderPrometheus 两处镜像。
- 注入缝先例：`TaskOrchestrator.InjectSynthetic`（internal，`InternalsVisibleTo("Dc.App"/"Dc.Infrastructure.Tests")`）。

## 2. 目标与非目标

**目标**
- 编排器拥有一个权威的每任务 `ConnectionState`，在现有生命周期转移点置位，看门狗重启时精确标「重启中」、连续失败标「故障」。
- `TaskDiagnostics` 携带 `State`；`/metrics` 暴露（双路径镜像）。
- 诊断页渲染状态徽章 + 「已恢复」瞬态确认。
- 门控 `/debug/fault` 端点强制某真任务进故障路径，配 dc-remote `fault` 子命令做活体演示（触发→截图徽章）。
- 状态机正确性由 Linux 集成测证明（可控 fake subscriber）。

**非目标（YAGNI）**
- 不给 `IOpcSubscriber` 加跨协议连接状态事件（保持现有粗粒度重启模型；状态由编排器生命周期 + 心跳派生）。
- 不做故障历史/时间线（本轮只做「当前状态徽章」；时间线是另一方向）。
- 不做主动弹窗通知（徽章为主；通知是另一方向）。
- 不改看门狗的重启策略/退避（只观测+标注，不改行为）。
- `/debug/fault` 仅门控调试设施，不进产线路径、不持久化。

## 3. 决策（已与用户确认）
- 核心缺口：显式连接状态徽章。
- 状态来源：编排器拥有（非纯 UI 派生）。
- 状态集：富集——含「故障」态（区分 blip vs 宕机）+「已恢复」瞬态。
- 故障注入：A——加门控 `/debug/fault` 端点 + dc-remote `fault` 子命令（多实战 dc-remote）。

## 4. 状态机

文件：`src/Dc.Infrastructure/Orchestration/`（新增枚举 + `TaskOrchestrator` 改）

### 4.1 枚举 `ConnectionState`（新增 `ConnectionState.cs`）
```csharp
namespace Dc.Infrastructure.Orchestration;

/// <summary>采集任务连接生命周期状态（编排器拥有，UI 与 /metrics 消费）。
/// 仅描述「在运行集里的活任务」可能的连接态——被用户停止的任务直接移出 _running、
/// 从 GetDiagnostics 快照消失（=行消失），不是这里的某个值（见 §5.3）。</summary>
public enum ConnectionState
{
    Connecting, // 初次/重连的 connect 阶段进行中
    Running,    // 已连接+订阅，心跳正常流动
    Restarting, // 心跳超时，看门狗正在拆除+重建
    Faulted     // 连续 ≥FaultThreshold 次重启仍未恢复心跳（疑似 server 长断）
}
```

> 已核实：`GetDiagnostics()` 只 `_running.Values.Select(...)`，故快照只含活任务；`Stopped` 不作为 ConnectionState 值（否则是永不发出的死枚举）。「已停止」由「行从快照消失」体现。

### 4.2 RuntimeTask 加字段
- `public ConnectionState State { get; set; } = ConnectionState.Connecting;`
- `public int ConsecutiveStaleRestarts { get; set; }`（连续因心跳超时重启次数，恢复后归零）
- `public DateTimeOffset? LastRestartAt { get; set; }`（判定「重启后是否已恢复心跳」用）

### 4.3 转移点（编排器置位）
- `StartUnlockedAsync` 进入连接阶段：`rt.State = Connecting`（构造 rt 时默认即 Connecting）。
- 订阅成功、pipeline 起：`rt.State = Running`。
- `RestartIfStaleAsync` 锁内确认要重启时：`rt.State = Restarting`、`rt.LastRestartAt = now`、`rt.ConsecutiveStaleRestarts++`。随后 Stop+Start（Start 内部又 Connecting→Running）。
  - 重启完成回写时：若 `ConsecutiveStaleRestarts >= FaultThreshold` 则置 `rt.State = Faulted`（覆盖 Start 设的 Running，表示「虽重启起来了但反复超时」）；否则保持 Running。
- **心跳恢复归零 + 退出 Faulted**：pipeline 收到心跳时更新 `LastHeartbeat`（现有逻辑）。新增：收到心跳且 `LastRestartAt` 之后已稳定（`now - LastRestartAt > HeartbeatTimeout` 且心跳新鲜）→ `ConsecutiveStaleRestarts = 0`，若当前是 Faulted 则回 `Running`。
  - 实现位置：心跳处理处（pipeline 消费 Heartbeats 通道更新 `LastHeartbeat` 的地方）顺带判定。
- 用户 `StopAsync` → 任务移出 `_running`（`StopUnlockedAsync` 走 `_running.TryRemove`）→ 从 GetDiagnostics 快照消失（无 Stopped 态值；见 §5.3）。
- `FaultThreshold` 放 `OrchestratorOptions`（默认 3），便于测试调小。

> 并发：`rt.State` 等字段的读写都在 `_mutationLock` 内（重启路径）或 pipeline 单线程（心跳更新）。`GetDiagnostics` 读 `rt.State` 为单字段读（enum=int，原子），与现有 `RestartCount` 读同等宽松度，一致即可。

### 4.4 TaskDiagnostics + /metrics
- `TaskDiagnostics` record 末尾加 `ConnectionState State = ConnectionState.Running`（带默认值，兼容现有构造点/测试）。
- `GetDiagnostics()` 组装时填 `rt.State`。
- `/metrics`：`RenderPrometheus` 加 `dc_collector_task_state{task_id="X",state="running"} 1`（每任务一行，state 标签为小写枚举名，值恒 1 表「当前态」）。**双路径**：DiagnosticsReporter 的 Meter 侧也加对应 instrument（按现有 collector 指标镜像约定；若 Meter 侧以 observable gauge 发布，加同名带 state 标签的测点）。

## 5. UI 呈现

文件：`src/Dc.App/ViewModels/DiagnosticsRowViewModel.cs`、`src/Dc.App/Views/DiagnosticsView.xaml`

### 5.1 DiagnosticsRowViewModel
- 加 `[ObservableProperty] ConnectionState _state;`（直接来自 `TaskDiagnostics.State`）。
- 加 `[ObservableProperty] bool _justRecovered;`（「已恢复」瞬态标志）。
- `Apply(d)`：
  - 记录 `prevState = State`、`prevRestart = RestartCount`；赋 `State = d.State`、`RestartCount = d.RestartCount`。
  - **已恢复检测**：若 `prevState is Restarting or Faulted` 且 `d.State == Running` 且 `d.RestartCount > prevRestart` → 触发 `JustRecovered = true` 并启动一个 ~5s 倒计时（基于诊断轮询 tick 计数或 DispatcherTimer），到点 `JustRecovered = false`。倒计时实现与 LiveData 防抖同款用注入 dispatcher 的 DispatcherTimer（VM 已在 UI 线程更新）；为可测，倒计时逻辑抽成可直接驱动的内部方法（如 `internal void RecoveryTickForTest()`）。
- 现有 `HeartbeatSeverity`/`HasErrors` 保留（Running 态下的子警示叠加）。

### 5.2 DiagnosticsView.xaml
- 诊断表加「状态」列（或在任务名旁）渲染徽章：`State` → 文案 + 软底色 pill（复用 LiveData 质量 pill 风格）：
  - Running 绿（`SystemFillColorSuccess*`）/ Connecting·Restarting 黄（`SystemFillColorCaution*`，可加轻动效）/ Faulted 红（`SystemFillColorCritical*`）。
  - `JustRecovered==true` 时叠加「已恢复」绿闪标（5s）。
- 用 `DataTrigger`（绑 `State` 枚举值）切换 pill 文案/色，与诊断页现有 pill/着色一致。

### 5.3 「已停止」呈现
- 用户 Stop 后任务从 `GetDiagnostics` 快照消失，`DiagnosticsViewModel` 随快照增删行（`_rowIndex` + `Rows`，已核实第 82/90 行从 GetDiagnostics 重建）→ **该任务的行直接消失**。这就是「已停止」的现有体现，本规格不改该行为（YAGNI），也不引入 Stopped 徽章。HealthEvaluator 对「配置了但不在 running 集」的既有告警（若有）保留不动。

## 6. 故障注入端点 + dc-remote fault 子命令

### 6.1 /debug/fault（门控，沿用 /debug/stress 范式）
文件：`MetricsHttpServer.cs`、`TaskOrchestrator.cs`、`ServiceRegistration.cs`
- 编排器加 internal 注入缝：`internal bool InjectFault(string taskId, string kind)`——
  - `kind="stall"`：强制该任务心跳「失速」——把 `rt.LastHeartbeat` 推回到 `now - HeartbeatTimeout - 1s`，使下一次看门狗 tick 判定超时 → 走真实重启路径（State→Restarting→…）。返回是否命中任务。
  - （YAGNI：先只做 stall 一种 kind，足以演示 Restarting/Recovered；持续 stall（看门狗每次重启后再被 stall）可演示 Faulted——见 dc-remote 子命令用法。）
- `MetricsHttpServer` 加可选 `Func<string, string, bool>? faultInjector`（taskId,kind→hit），构造末位参数；`/debug/fault?task=X&kind=stall` 路由：injector 为 null→404；非 null→执行返回 JSON `{"injected":true/false,"task":"X","kind":"stall"}`；非 POST→405（与 /debug/stress 一致）。
- `ServiceRegistration`：门控同 `DC_DEBUG_STRESS=1`（复用同一开关，调试设施统一）时注入 `faultInjector: (task,kind) => orch.InjectFault(task,kind)`，否则 null。
- 取消/线程：InjectFault 仅改 `rt.LastHeartbeat`（一次性、轻量、锁内或原子写），不起后台任务，无取消顾虑。

### 6.2 dc-remote `fault` 子命令
文件：`~/.claude/skills/dc-remote/scripts/dc-remote.sh`
- `dc-remote fault <taskId> [stall-count]`：
  1. 确保 App 以门控起（`DC_DEBUG_STRESS=1`）+ 有真任务在跑（前置：用户已建并启动一个采集任务；或文档说明需先有任务）。
  2. 导航诊断页（`ui click 诊断`）。
  3. `POST /debug/fault?task=<taskId>&kind=stall`，截图（应见 Restarting/Faulted 徽章）。
  4. 反复 stall `stall-count` 次（每隔看门狗间隔触发，逼到 Faulted），各截一张。
  5. 停止 stall，等心跳恢复，截图（应见「已恢复」绿闪 → Running）。
  6. 输出每步状态（从 `/metrics` 的 `dc_collector_task_state` 读）+ 截图路径。
- 脚本与 win/*.ps1 纯 ASCII 约定；带 `&` 的 URL 走 powershell 双引号块（见 dc-remote-http-query-amp 经验，curl 单引号会被 cmd 拆）。

## 7. 错误处理与边界
- `InjectFault` 命中不存在的 task → 返回 false、端点 200 `{"injected":false}`（不报错）。
- Faulted 判定阈值 `FaultThreshold` 默认 3；可经 OrchestratorOptions 调（测试调成 2 加速）。
- 「已恢复」瞬态在页面切走/Dispose 时停倒计时（避免回调访问已弃 VM）。
- 状态字段读写宽松一致性同现有 RestartCount（enum 单字段读原子），不引锁升级。
- 门控关（DC_DEBUG_STRESS 未设）→ /debug/fault 404、InjectFault 不可达、faultInjector 不构造，产线无注入面。
- 无头 Cli：不传 faultInjector（同 stressRunner），/debug/fault 404；状态机仍工作（Cli 也有看门狗），/metrics 仍暴露 state。

## 8. 测试
**状态机（Linux，`Dc.Infrastructure.Tests`/`Dc.Integration.Tests`）：**
- 可控 fake `IOpcSubscriber`（Heartbeats 通道可暂停/恢复），驱动 `TaskOrchestrator`（WatchdogInterval/HeartbeatTimeout/FaultThreshold 调小）：
  - 正常起 → State=Running。
  - 暂停心跳 → 看门狗超时 → State 经 Restarting → 重启后（心跳仍停）再超时 → 累计达 FaultThreshold → State=Faulted。
  - 恢复心跳 → State 回 Running、ConsecutiveStaleRestarts 归零。
  - RestartCount 随每次重启递增。
- `InjectFault("X","stall")` → 命中后下次看门狗 tick 触发重启路径（断言 State 转移）；不存在 task → false。

**UI 瞬态（`Dc.App.Tests`，office）：**
- `DiagnosticsRowViewModel.Apply`：构造序列 Restarting→Running(+RestartCount) → `JustRecovered==true`；`RecoveryTickForTest()` 数次后 → `JustRecovered==false`。Faulted→Running 同样触发。Running→Running 不触发。

**/metrics 渲染（Linux）：**
- `RenderPrometheus` 含 `dc_collector_task_state{task_id="...",state="running"} 1`；多任务多状态各一行；状态标签为枚举小写名。

**端点门控（Linux，`Dc.Integration.Tests` 真起 HttpListener）：**
- 无 injector → POST /debug/fault 404；GET → 405；有 injector → 200 + JSON，injector 收到 (task,kind)。

**活体（office，dc-remote）：**
- `dc-remote fault <task> 3` 跑通：诊断页徽章经 Restarting→Faulted→（停 stall）已恢复→Running，逐张截图确认。

## 9. 涉及文件
- 新增：`src/Dc.Infrastructure/Orchestration/ConnectionState.cs`；fake subscriber 测试夹具（若现有无可控心跳的）；测试文件若干。
- 修改：`TaskOrchestrator.cs`（RuntimeTask 字段 + 转移 + InjectFault + GetDiagnostics）、`TaskDiagnostics.cs`（+State）、`OrchestratorOptions.cs`（+FaultThreshold）、`MetricsHttpServer.cs`（state 渲染 + /debug/fault + faultInjector）、DiagnosticsReporter/Meter（state 镜像）、`ServiceRegistration.cs`（门控 faultInjector）、`DiagnosticsRowViewModel.cs`（State + JustRecovered 瞬态）、`DiagnosticsView.xaml`（徽章列）、`~/.claude/skills/dc-remote/scripts/dc-remote.sh`（fault 子命令）。

## 10. 验收标准
- 采集任务在掉线/重启/恢复时，诊断页徽章正确反映 连接中/运行正常/重启中/故障 + 「已恢复」瞬态；用户停止的任务行随快照消失（即「已停止」）。
- 连续重启失败升级为「故障」红态，恢复后回「运行正常」。
- `/metrics` 暴露 `dc_collector_task_state`（双路径镜像）。
- 门控 `/debug/fault` 注入：门控关 404、门控开按 kind 注入；dc-remote `fault` 子命令活体演示徽章全程并截图。
- 状态机由 Linux 集成测覆盖全部转移；UI 瞬态与 metrics 渲染各有测。
- 全量测试通过（Linux + office App）。
