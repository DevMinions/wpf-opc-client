# 设计：发布缓冲/丢弃可观测

- 日期：2026-06-02
- 状态：已批准（待实现）
- 范围：无头 `Dc.Cli` + WPF 共用的采集诊断链路（`Dc.Infrastructure`）

## 背景与动机

调查发布失败/背压行为（`BatchingTcpPublisher` + `OutboundQueue`）后确认：**背压设计健全，无内存无限堆积、无隐患**。

- 队列启用：broker 断网时帧转入文件队列（store-and-forward），恢复后先 drain 旧队列再发新帧（保 FIFO）。
- 队列满（到 `MaxBytes`）：先 `Compact()` 去已发段；仍超则 `DropOldestUntilFits()` **丢最旧的未发帧**（保最新数据）。
- 队列禁用：持续失败 → 丢该批 + 计数（队列是 opt-in 的持久化）。

但调查暴露**两个真实的可观测性缺口**：

1. 离线队列积压 `IOutboundQueue.PendingBytes`（注释明写"供监控展示"）**未暴露为指标** —— broker 断网时运维看不到 backlog 涨势。
2. `DropOldestUntilFits` 的 drop-oldest 是**静默的** —— 队列溢出丢老帧时无计数/日志，运维无法察觉数据丢失。

> 注：`IPublisherHealth.SendErrorCount` 已折入 `dc_collector_task_publish_errors` 指标，**已可见**，不在本设计范围。本设计只新增"积压字节"与"丢弃帧数"两项。

## 目标

让发布侧的"缓冲/丢弃"对无头运维可见：每任务暴露离线队列积压字节与累计丢弃帧数（指标），并在开始/停止丢弃时打边沿日志。**不改 WPF Dashboard UI**（仅给诊断记录加字段，WPF 暂不展示，向后兼容）。

## 非目标

- 不改背压/丢弃策略本身（drop-oldest 保持不变）。
- 不在 WPF DiagPanel 上展示新字段。
- 不暴露 `SendErrorCount`（已可见）。
- 不做即时（亚秒级）丢弃日志——边沿日志精度受 `ReportInterval`（默认 30s）限制，对分钟级断网事件足够。

## 选定方案：Alt 1

扩展现有 `IPublisherHealth` 接缝暴露计数 + 队列保持纯净（无 logger）+ 边沿日志集中在 `DiagnosticsReporter`。
（备选 Alt 2 给 `OutboundQueue` 注入 logger 当场打日志——污染纯文件类、换 30s 精度不划算；Alt 3 事件回调——YAGNI。均不取。）

## 详细设计

### ① 接口/类型改动

- **`IOutboundQueue`**（`Dc.Infrastructure/Messaging`）
  ＋ `long DroppedFrameCount { get; }` —— 累计因 `MaxBytes` 溢出被 drop-oldest 丢弃的帧数。

- **`OutboundQueue`**
  - ＋ 私有字段 `long _droppedFrameCount;`
  - 在 `DropOldestUntilFits()` 的 `if (TryReadRecordHeader(...))` 分支（成功跳过一条完整记录 = 丢一帧）自增；`else`（损坏 resync 越过的垃圾）不计。
  - getter `DroppedFrameCount` 用 `lock (_lock)` 读（镜像 `PendingBytes` 的并发约定）。
  - 自增点已在 `Enqueue` 的 `lock (_lock)` 内（C# `lock` 同线程可重入），无需额外同步。

- **`IPublisherHealth`**（`Dc.Infrastructure/Messaging`）
  ＋ `long PendingBytes { get; }`
  ＋ `long DroppedFrameCount { get; }`

- **`BatchingTcpPublisher`**（生产路径唯一的 publisher——`TcpPublisherFactory.Create` 只 new 它；已实现 `IPublisherHealth`）
  - `PendingBytes => _queue?.PendingBytes ?? 0;`
  - `DroppedFrameCount => _queue?.DroppedFrameCount ?? 0;`
  - 无队列（禁用）时返回 0。
  - **`TcpPublisher` 不动**：它仅测试直接用、且为同步发布器（`PublishAsync` 内联 await，调用方可 try/catch 观测），未实现 `IPublisherHealth`。`GetDiagnostics` 的 `as IPublisherHealth` 对它得 null → `?? 0`，无害。

- **`TaskDiagnostics`** record（`Dc.Infrastructure/Orchestration`）
  - 末尾追加 `long QueuePendingBytes, long DroppedFrameCount`（append 减少改动）。
  - 构造点：`TaskOrchestrator.GetDiagnostics` + 测试。

### ② 数据流

```
OutboundQueue (PendingBytes / DroppedFrameCount)
   │  via IPublisherHealth（与现有 SendErrorCount 同款）
   ▼
TaskOrchestrator.GetDiagnostics  ── rt.Publisher as IPublisherHealth
   ▼
TaskDiagnostics { …, QueuePendingBytes, DroppedFrameCount }   ← 单一快照
   ├── DiagnosticsReporter  → Meter 指标 + 结构化日志 + 边沿日志
   └── MetricsHttpServer    → /metrics Prometheus 文本
```

`GetDiagnostics` 折入方式与现有 `bgErrors` 一致：

```csharp
var health = rt.Publisher as IPublisherHealth;
// …
QueuePendingBytes: health?.PendingBytes ?? 0,
DroppedFrameCount: health?.DroppedFrameCount ?? 0,
```

### ③ 指标命名（双路径对齐）

两条对外路径指标名必须镜像（见记忆 diag-metrics-dual-path-alignment）：

| Meter（`DiagnosticsReporter`，ObservableGauge） | Prometheus（`MetricsHttpServer.RenderPrometheus`） | 单位 | 含义 |
|---|---|---|---|
| `dc.collector.task.queue_pending_bytes` | `dc_collector_task_queue_pending_bytes{task_id="…"}` | `By` | 每任务离线队列未发字节 |
| `dc.collector.task.dropped_frames` | `dc_collector_task_dropped_frames{task_id="…"}` | `{frames}` | 每任务累计溢出丢弃帧数 |

两者均带 `task.id` / `task_id` 维度，与现有每任务指标一致。

### ④ 日志（边沿触发，在 `DiagnosticsReporter`）

- Reporter 持 per-task 状态字典：`Dictionary<string,(long lastDropped, bool isDropping)>`。
- 每个 LogLoop tick（`ReportInterval`，默认 30s）对每个任务比对当前 `DroppedFrameCount`(cur) 与 last：
  - `cur > last && !isDropping` → `LogWarning("诊断 task={Id} 离线队列溢出，开始丢弃最旧帧（累计丢 {Dropped}）")`，置 `isDropping=true`。
  - `cur == last && isDropping` → `LogInformation("诊断 task={Id} 队列停止丢弃（累计丢 {Dropped}）")`，置 `isDropping=false`。
  - `cur < last`（任务重启 → 队列重建归零）→ 重置该任务状态、不打日志。
  - 快照中消失的 task → 从字典移除（防无界增长）。
- `LogOnce` 的每任务结构化行追加 `积压={QueuePendingBytes}B 丢弃={DroppedFrameCount}`。
- 边沿检测只在 `EnableLogging` 的日志循环跑；指标（Meter/Prometheus）独立于日志开关。

### ⑤ 错误处理

- 队列计数读取在锁内，发布器委托空安全（`?? 0`）。
- 边沿日志的状态字典仅 Reporter 单线程日志循环访问（无并发）。
- 任务重启导致计数归零 → 显式 `cur < last` 规则避免误报"停止丢弃"。

## 测试（全跨平台 net8.0，本地可验）

1. **`OutboundQueue`**：灌入超过 `MaxBytes` 的帧 → `DroppedFrameCount` 增量等于被丢帧数；`PendingBytes ≤ MaxBytes`；未溢出时 `DroppedFrameCount==0`。
2. **`BatchingTcpPublisher`**：`IPublisherHealth.PendingBytes` / `DroppedFrameCount` 委托队列；无队列时返回 0。（`TcpPublisher` 不实现该接口，不测。）
3. **`MetricsHttpServerTests`**：扩 `RenderPrometheus` 断言两条新指标行（含 `task_id`）；更新 `TaskDiagnostics` 构造为 10 参。
4. **`DiagnosticsReporter`**：用 `Func` 快照提供递增 `DroppedFrameCount`，跨两 tick 断言"进入丢弃"WARN 一次、"停止丢弃"INFO 一次；`cur<last` 不误报（沿用现有 fake logger 测试模式）。

## 已知波及点 / 风险

- `TaskDiagnostics` 加字段波及**所有构造它的测试**（Infra 已知；可能含 `Dc.App.Tests`）——实现时全局 grep `new TaskDiagnostics(` 更新；WPF 侧构造点本地编不了，靠 Windows CI 验证。
- 边沿日志 30s 延迟为有意取舍（Alt 1）。

## 验证标准

- Infra + Integration 测试本地全绿（含新增）。
- `Dc.Cli` 0 警告构建。
- 推送后 Windows CI 全绿（WPF 编译 + 全测试）。
- `/metrics` 输出含两条新指标行；broker 断网联调时 `queue_pending_bytes` 涨、溢出后 `dropped_frames` 增并伴随一条 WARN。
