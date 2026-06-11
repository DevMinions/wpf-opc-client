# LiveData 规模化 + 预防性硬化 设计规格

- 日期：2026-06-11
- 范围：`Dc.App`（LiveData VM + 合并器 + UI 防抖）、`Dc.Infrastructure`（编排器注入缝 + 合成负载发生器 + /debug/stress 端点 + flush 指标）、`Dc.Integration.Tests`（算法/发生器跨平台测）、dc-remote skill（stress 子命令）。
- 目标：大量 Tag（1k–5k）高频订阅下 LiveData 界面不卡顿；并以**可复现的测量**量化天花板（测量驱动，承接 UA 批量读性能线）。
- 主线：本轮真正交付物是打磨 **dc-remote skill**（新增 stress 闭环）；LiveData 优化是实战它的载体。

## 1. 背景与现状（已核实）

`LiveDataViewModel`（`src/Dc.App/ViewModels/LiveDataViewModel.cs`）当前数据流：

- `OnTagValueReceived(taskId, TagValue)`：仅 `_buffer.Enqueue(...)`（`ConcurrentQueue`，不碰 Dispatcher，线程安全廉价）。
- `_batchTimer`（`DispatcherTimer` @ `BatchIntervalMs=100`）→ `FlushBuffer()`：`while (TryDequeue) Apply(...)` 排空缓冲，逐条应用。
- 行按 `key = $"{taskId}::{v.Item}"` 去重存 `_rowIndex` + `Rows`（`ObservableCollection`）；值**原地更新**（`row.Apply(v)`）。故 N 个 Tag = N 行固定，不无限增长。
- `MaxRows=5000` 上限，超限淘汰最旧：`oldestKey = _rowIndex.Keys.First()` + `Rows.Remove(oldestRow)`。
- `RowsView`（`ICollectionView`）按 `TaskFilter` + `SearchText` 过滤；`OnSearchTextChanged`/`OnTaskFilterChanged` → `RowsView.Refresh()`（全量重过滤）。
- `Start()`/`Stop()` 绑 view Loaded/Unloaded（订阅事件 + 启停 timer），不可见时不空跑。
- XAML 已开行/列虚拟化 + Recycling + 延迟滚动。

诊断侧：`MetricsHttpServer`（`src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs`，跨平台 net8.0）switch 路由，已有 `/healthz` `/readyz` `/metrics` `/screenshot`（screenshot provider 从 App 注入，无头端为 null → 503）。`/metrics` 经 `RenderPrometheus(snap, now)`（public static，可单测）渲染。指标走双路径：`DiagnosticsReporter` 的 Meter + Prometheus 文本须镜像（改一处改两处）。

`TaskOrchestrator`（Infrastructure）拥有 `TagValueReceived` 事件，是 LiveData 数据源。真 OPC 采集经此事件上抛值。

### 已识别的真实瓶颈（压测必中，非臆测）

1. **缓冲不按 key 合并**：`FlushBuffer` 应用缓冲里每一条原始值。1k Tag 在 100ms 内各来 10 次 = 一次 flush 做 1 万次 `Apply`，其中大量中间值会被立即覆盖 → 纯无用功。
2. **淘汰 O(n)**：`Rows.Remove(oldestRow)` 在 `ObservableCollection` 上线性查找。证 5k 天花板时行数顶在 `MaxRows=5000`，每次 flush 都淘汰 → O(n²)。
3. **搜索无防抖**：5000 行下每敲一字符全量 `RowsView.Refresh()` 同步重过滤 → 打字顿。
4. **无任何测量**：现在是「看着应该没事」，没有证明天花板的压测/数字。

## 2. 目标与非目标

**目标**
- 抽出纯算法 `LiveValueCoalescer`（无框架依赖，Linux 可测）：排空一批 `(key, value)` → 每 key 取最新 → 输出有序结果。VM 用它替代逐条 Apply。
- 淘汰路径消除 O(n) 线性查找（最旧恒为 `Rows[0]`，key 由行重建）。
- 搜索防抖（5000 行下打字不顿）。
- flush 测量埋点（flush 耗时、合并比）暴露给 `/metrics`（双路径镜像）。
- 合成负载发生器 `SyntheticLoadGenerator`（Infra）+ 门控的 `POST /debug/stress` 端点，把 N×R Hz 合成 Tag 灌进编排器（绕过真 OPC），仅压 VM→UI 段。
- dc-remote 加 `stress` 子命令：起门控 App → 触发 → 回采 /metrics + /screenshot → 出小报告。
- **两层证明**：算法层 Linux 微基准 + 真应用 office 压测。

**非目标（YAGNI）**
- 不改订阅/采集路径性能（上轮批量读已覆盖读路径；本轮只针对 VM→UI 渲染段）。
- 负载发生器不进产线路径，不做持久化/配置化（纯调试设施，门控后存在）。
- 不引入第三方虚拟化/分页库；沿用 WPF 自带虚拟化。
- 不做 LiveData 历史/趋势图。
- `LiveValueCoalescer` 放 `Dc.App`，不上提 `Dc.Opc.Abstractions`（唯一消费方是 VM，YAGNI）。

## 3. 决策（已与用户确认）

- 触发点：预防性硬化 + 量化天花板（测量驱动）。
- 证明层：两层都要（算法 Linux 微基准 + 真应用 office 压测）。
- 喂法：B 合成负载发生器（绕过真 OPC，灌 `TagValueReceived` 上游）。
- 触发/门控：复用诊断 `MetricsHttpServer` 加 `POST /debug/stress`，门控在环境变量 `DC_DEBUG_STRESS=1`（或 DEBUG 构建）后；产线默认 404。
- 优化项：4 项全做（合并 / O(1) 淘汰 / 搜索防抖 / 测量埋点）。
- `LiveValueCoalescer` 抽到 `Dc.App`。

## 4. 组件设计

### 4.1 `LiveValueCoalescer`（新增，`src/Dc.App/ViewModels/LiveValueCoalescer.cs`，纯类）

职责：把一批原始更新合并为「每 key 最新值 + 首次出现顺序」，供 VM 一次性应用。无 WPF 依赖（不引用 ObservableCollection/Dispatcher），可 Linux 测。

```csharp
namespace Dc.App.ViewModels;

/// <summary>
/// 把一批原始 (key, value) 更新合并为每 key 仅最新值，保留各 key 首次出现顺序。
/// 纯算法、无框架依赖：高频流下砍掉会被立即覆盖的中间值。
/// </summary>
public sealed class LiveValueCoalescer<TValue>
{
    // 复用实例避免每次 flush 分配；Coalesce 内部清空后填充。
    private readonly Dictionary<string, TValue> _latest = new();
    private readonly List<string> _order = new();

    /// <summary>合并比统计：上次 Coalesce 的 (输入条数, 输出 key 数)。</summary>
    public int LastInputCount { get; private set; }
    public int LastOutputCount { get; private set; }

    /// <summary>
    /// 排空 dequeue 委托返回的所有项，合并为每 key 最新值。
    /// 回调 apply(key, latestValue) 按 key 首次出现顺序触发一次。
    /// </summary>
    public void Coalesce(Func<(bool ok, string key, TValue value)> tryDequeue,
        Action<string, TValue> apply)
    {
        _latest.Clear();
        _order.Clear();
        var input = 0;
        while (true)
        {
            var (ok, key, value) = tryDequeue();
            if (!ok) break;
            input++;
            if (!_latest.ContainsKey(key)) _order.Add(key);
            _latest[key] = value; // 覆盖为最新
        }
        LastInputCount = input;
        LastOutputCount = _order.Count;
        foreach (var key in _order) apply(key, _latest[key]);
    }
}
```

要点：
- 泛型 `TValue` 便于测试用简单类型；VM 实例化为 `LiveValueCoalescer<TagValue>`，key 由 VM 拼（`taskId::item`）。
- `tryDequeue` 抽象掉 `ConcurrentQueue`，测试可喂 `Queue<>`/数组。
- `apply` 回调里 VM 做 `_rowIndex` 查找 + `row.Apply`。合并比 = `LastInputCount / max(1, LastOutputCount)`。

### 4.2 `LiveDataViewModel` 改造

- 字段加 `private readonly LiveValueCoalescer<(string TaskId, TagValue Value)> _coalescer = new();` —— 但 key 已含 taskId，故更简洁：合并器 key=`$"{taskId}::{item}"`，value=`(taskId, TagValue)`。具体：`LiveValueCoalescer<(string TaskId, TagValue Value)>`。
- `FlushBuffer` 改为：
  ```csharp
  _coalescer.Coalesce(
      tryDequeue: () => _buffer.TryDequeue(out var it)
          ? (true, $"{it.TaskId}::{it.Value.Item}", it)
          : (false, string.Empty, default),
      apply: (_, it) => Apply(it.TaskId, it.Value));
  var applied = _coalescer.LastInputCount;     // 原始条数（速率用）
  var distinct = _coalescer.LastOutputCount;   // 实际 Apply 次数
  ```
  速率 `UpdatesPerSecond` 仍按**原始条数** `LastInputCount` 累计（反映真实数据流入）；UI 工作量按 `LastOutputCount`。
- **消除线性查找的淘汰**：根因是 `Rows.Remove(oldestRow)` 在 `ObservableCollection` 上 O(n) **线性查找**。关键观察：行只在末尾 `Add`、只从最旧端淘汰，从不中途插入 → **最旧行恒为 `Rows[0]`**；且行 VM 已带 `TaskId`+`Item`，key 可由行直接重建。故无需任何并行顺序结构（不引入 LinkedList）：
  ```csharp
  while (_rowIndex.Count > MaxRows)
  {
      var victim = Rows[0];
      Rows.RemoveAt(0);                                  // O(移位)，无查找
      _rowIndex.Remove($"{victim.TaskId}::{victim.Item}");
  }
  ```
  - 收益：把每次淘汰从「O(n) 查找 + 移位」降为「仅移位」，压测 5k 边界持续淘汰不再 O(n²) 查找。`RemoveAt(0)` 的 List 内部移位仍 O(n)，但要消除它需环形缓冲/分页 → YAGNI 过重，不做。
  - `_rowIndex` 保持 `Dictionary<string, LiveDataRowViewModel>`（无需改 value 类型）。
- 复用现有 `Apply(taskId, v)`，`_rowIndex` 结构不变。

### 4.3 搜索防抖（`LiveDataViewModel`）

- `OnSearchTextChanged` 不再立即 `RowsView.Refresh()`，改为重置一个 `DispatcherTimer _searchDebounce`（`Interval=250ms`，`DispatcherPriority.Background`）：每次输入 `Stop()`+`Start()`，`Tick` 里 `RowsView.Refresh()` 后 `Stop()`。
- `TaskFilter` 变更仍即时 Refresh（下拉选择非高频，无需防抖）。
- `Clear()`/`Dispose()` 停掉防抖 timer。

### 4.4 flush 测量埋点（双路径）

LiveData 的 flush 指标属于 App 进程级、与采集任务无关。最小侵入方案：

- `LiveDataViewModel` 维护轻量统计字段：`LastFlushMs`（上次 flush 耗时，`Stopwatch`）、累计 `TotalFlushes`、`TotalCoalesceInput`、`TotalCoalesceOutput`，并以滑动方式算 flush 毫秒的近似 p50/p95（保留最近 N=128 次 flush 耗时的小环形数组，排序取分位）。
- 暴露给 metrics：因 `MetricsHttpServer` 在 Infra、统计在 App VM，经一个 `Func<LiveFlushStats>?` provider 注入（与 screenshot provider 同模式）。`MetricsHttpServer` 构造新增可选 `Func<LiveFlushStats>? liveFlushProvider`；`/metrics` 渲染时若非空追加 gauge：
  - `dc_livedata_flush_ms_p50` / `dc_livedata_flush_ms_p95`
  - `dc_livedata_coalesce_ratio`（= 累计 input/output）
  - `dc_livedata_rows`（当前行数）
  - `dc_livedata_updates_per_second`
- **双路径镜像**：若 `DiagnosticsReporter` 侧有对应 Meter 仪表则同步加（保持 diag-metrics 双路径约定）；LiveData flush 为 UI 侧指标，按现有约定在 `RenderPrometheus` 追加同时在 Meter 注册镜像 instrument。`RenderPrometheus` 保持 public static 可单测：新增重载或参数传入 `LiveFlushStats?`。
- `LiveFlushStats` 为不可变 record（`Dc.Infrastructure` 或共享层定义，App 填充），字段：`P50Ms, P95Ms, CoalesceRatio, Rows, UpdatesPerSecond`。放 `Dc.Infrastructure`（Infra 渲染要用，App 引用 Infra 合法）。

### 4.5 `SyntheticLoadGenerator`（新增，`src/Dc.Infrastructure/Orchestration/SyntheticLoadGenerator.cs`）

```csharp
/// <summary>
/// 调试用：按指定 Tag 数与频率合成 TagValue，定速灌进编排器的 TagValueReceived 路径，
/// 绕过真 OPC，仅用于压测 VM→UI 渲染段。门控后才被构造/调用，绝不进真采集路径。
/// </summary>
public sealed class SyntheticLoadGenerator
{
    private readonly Action<string, TagValue> _inject;  // 编排器注入缝
    // RunAsync(taskId, tags, hz, seconds, ct)：
    //   每 1/hz 秒为 tags 个 key（Stress::tag{i}）各发一个递增值；
    //   每 ~50 个掺 1 个 Bad/Uncertain 质量（验证着色路径）；
    //   持续 seconds 秒或取消。返回实际发出的更新条数。
}
```

- 注入缝：`TaskOrchestrator` 加 `internal void InjectSynthetic(string taskId, TagValue v)`，仅触发现有 `TagValueReceived` 事件（与真值同路径，下游 VM 无感）。`internal` + `InternalsVisibleTo` 给发生器/测试。
- 发生器用 `PeriodicTimer` 控速，跨平台。

### 4.6 `/debug/stress` 端点 + 门控（`MetricsHttpServer`）

- 构造新增可选 `Func<StressParams, Task<long>>? stressRunner`（App 注入，封装发生器）+ 门控标志 `bool stressEnabled`。
- 门控：`stressEnabled = Environment.GetEnvironmentVariable("DC_DEBUG_STRESS") == "1"`（或 `#if DEBUG`）。仅当 true 时 `/debug/stress` 路由生效，否则落 default → 404（与产线默认一致）。
- `case "/debug/stress"`：解析 query `tags`(默认1000)、`hz`(默认10)、`seconds`(默认30)；非门控 → 404；门控 → 触发 runner（fire-and-forget 或同步等完成返回 JSON `{injected: N}`）。压测期间不阻塞 /metrics、/screenshot。
- 启动日志列出端点时，门控开则附 ` /debug/stress`。

### 4.7 dc-remote `stress` 子命令（`~/.claude/skills/dc-remote/scripts/dc-remote.sh`）

`dc-remote stress <tags> <hz> <seconds>`：
1. 以 `DC_DEBUG_STRESS=1` 起 App（沿用 `_desktop_run` 注入可见会话）。
2. 等就绪（轮询 `/healthz`）。
3. 切到 LiveData 页（沿用 `ui` 子命令导航）。
4. `curl -s -X POST ".../debug/stress?tags=$1&hz=$2&seconds=$3"`。
5. 压测中每 ~2s 轮询 `/metrics` 抓 `dc_livedata_*`；中途 + 结束各 `/screenshot` 一张。
6. 输出小报告：`N Tag @ R Hz → flush p50/p95 ms、合并比、rows、updates/s、是否丢帧（flush p95 是否超 BatchInterval）`。
- 脚本推送保持纯 ASCII（win/*.ps1 约定）。

## 5. 错误处理与边界

- 负载发生器：门控关时端点 404、发生器不构造；`seconds` 上限钳制（如 ≤300）防失控；取消令牌随 App 关闭。
- 合并器：空输入 → 0 输出，`apply` 不触发；key 拼接为 VM 职责。
- 淘汰：`MaxRows` 不变（5000）；批量淘汰一次算清超出量。
- flush 指标 provider 为空（无头 Cli 无 LiveData）→ /metrics 不渲染 LiveData 段（与 screenshot 503 同理）。
- 防抖 timer 在 `Stop()`/`Dispose()` 必停，避免页面隐藏后回调访问已清集合。

## 6. 测试

**算法层（Linux，`Dc.Integration.Tests` 或 `Dc.App.Tests`——纯类优先 `Dc.App.Tests`）：**
- `LiveValueCoalescerTests`：
  - 多 key 多次更新 → 每 key 仅最新值，按首次出现顺序 apply。
  - 单 key 高频（10000 条同 key）→ 1 次 apply，值为最后一条；`LastInputCount=10000`、`LastOutputCount=1`。
  - 空输入 → 无 apply。
  - 顺序保持：key A 先于 B 首现 → apply A 先于 B。
- `LiveValueCoalescer` **微基准**（`Dc.Integration.Tests`，跨平台）：10000 条原始（1000 key × 10 次）→ Coalesce 计时，断言输出 1000、合并比 ≈10、耗时上限（如 < 50ms，留余量不锁死）；`ITestOutputHelper` 输出耗时与合并比。

**注入/发生器层（Linux，`Dc.Integration.Tests`）：**
- `SyntheticLoadGeneratorTests`：发生器 RunAsync(tags=100, hz=10, seconds=1) → 收集 `TagValueReceived` 实际条数 ≈ 100×10×1（±容差）；key 形如 `Stress::tag{i}` 共 100 个 distinct；含少量非 Good 质量。
- 编排器注入缝：`InjectSynthetic` 触发 `TagValueReceived` 一次、参数透传。

**指标层（Linux）：**
- `RenderPrometheus` 带 `LiveFlushStats` → 输出含 `dc_livedata_flush_ms_p50/p95`、`dc_livedata_coalesce_ratio` 等行；provider 为 null → 不含 LiveData 段。

**真应用层（office Windows，dc-remote）：**
- `dc-remote stress 1000 10 30` 与 `dc-remote stress 5000 20 30` 跑通，出报告 + 截图；人工/报告确认 flush p95 ≤ BatchInterval（100ms）即「不卡」判据，5000 行搜索打字截图不顿。

**VM 单测（`Dc.App.Tests`，纯逻辑部分）：**
- 淘汰：连续 Apply 超 MaxRows → 行数稳定在 MaxRows、最旧被移除、无线性查找回归（行为断言）。
- 防抖：搜索连续变更只在静默 250ms 后 Refresh（可注入时钟/手动驱动 timer 的接口；若 DispatcherTimer 难测则将防抖判定逻辑抽小函数测）。

## 7. 涉及文件

- 新增：
  - `src/Dc.App/ViewModels/LiveValueCoalescer.cs`
  - `src/Dc.Infrastructure/Orchestration/SyntheticLoadGenerator.cs`
  - `src/Dc.Infrastructure/Orchestration/LiveFlushStats.cs`（record）
  - 测试：`LiveValueCoalescerTests`、`SyntheticLoadGeneratorTests`、（基准）`LiveCoalesceBenchmarkTests`、指标渲染测补充。
- 修改：
  - `src/Dc.App/ViewModels/LiveDataViewModel.cs`（合并器 + O(1) 淘汰结构 + 搜索防抖 + flush 统计）
  - `src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs`（`InjectSynthetic` 注入缝 + `InternalsVisibleTo`）
  - `src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs`（`/debug/stress` + 门控 + flush 指标渲染）
  - metrics 渲染/Meter 镜像（`RenderPrometheus` 重载 + `DiagnosticsReporter` 镜像 instrument）
  - `src/Dc.App/Composition/ServiceRegistration.cs`（注入 stressRunner + liveFlushProvider，门控判定）
  - `~/.claude/skills/dc-remote/scripts/dc-remote.sh`（`stress` 子命令）+ 可能的 win/*.ps1 辅助

## 8. 验收标准

- `LiveValueCoalescer` 抽出且 VM 接入：高频流下每 key 仅应用最新值；单测 + 微基准证合并比与正确性（Linux 通过）。
- 淘汰消除线性查找：5k 行边界持续淘汰不再 O(n²)（行为单测 + 压测不卡）。
- 搜索防抖：5000 行下连续输入仅静默后过滤一次（单测 + 截图不顿）。
- flush 指标经 /metrics 暴露（双路径镜像），`RenderPrometheus` 单测覆盖。
- 门控负载发生器 + `/debug/stress`：门控关 404、不构造；门控开按参数注入合成洪流，仅走 VM→UI 段。
- dc-remote `stress` 子命令跑通：起→触发→回采→截图→出报告；office 实测 1000@10 与 5000@20 天花板数字落表。
- 全量测试通过（Linux Integration + office App）。
