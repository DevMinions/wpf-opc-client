# LiveData 规模化 + 预防性硬化 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 大量 Tag（1k–5k）高频订阅下 LiveData 界面不卡顿，并以可复现测量量化天花板；同时给 dc-remote 加 `stress` 压测闭环（本轮真正交付物）。

**架构：** 抽纯算法 `LiveValueCoalescer`（Dc.App）按 key 合并最新值替代 VM 逐条 Apply；消除淘汰线性查找；搜索防抖；flush 指标经 `/metrics` 暴露；门控的 `SyntheticLoadGenerator` + `POST /debug/stress` 把合成洪流灌进编排器 `TagValueReceived` 上游（绕过真 OPC，仅压 VM→UI 段）；dc-remote `stress` 子命令做「起→触发→回采→截图→报告」闭环。

**技术栈：** .NET 8 / C#、WPF（DispatcherTimer/ObservableCollection/ICollectionView）、`ConcurrentQueue`、`PeriodicTimer`、HttpListener、xUnit + Moq、dc-remote bash/PowerShell。

---

## 测试落机路由（关键，控制者据此分流）

| 件 | 工程 | TFM | 跑在 |
|---|---|---|---|
| `LiveValueCoalescer` 单测 + 微基准 | `Dc.App.Tests` | net8.0-windows | **office Windows**（dc-remote test） |
| `LiveDataViewModel` 淘汰/防抖单测 | `Dc.App.Tests` | net8.0-windows | **office Windows** |
| `SyntheticLoadGenerator`、编排器注入缝 | `Dc.Infrastructure.Tests` | net8.0 | **本机 Linux** |
| `RenderPrometheus`(LiveFlushStats)、`/debug/stress` 门控 | `Dc.Infrastructure.Tests` | net8.0 | **本机 Linux** |
| 真应用压测 + 截图 | dc-remote `stress` | — | **office Windows** |

- Linux 跑：`export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj`
- office 跑（App 含 Coalescer/VM 测）：dc-remote `sync` → `build` → `test`（沿用既有闭环）。

## 文件结构（职责）

**新增：**
- `src/Dc.App/ViewModels/LiveValueCoalescer.cs` — 纯算法：一批 (key,value) → 每 key 最新值 + 首现序。无 WPF 依赖。
- `src/Dc.Infrastructure/Orchestration/LiveFlushStats.cs` — 不可变 record，承载 LiveData flush 指标快照。
- `src/Dc.Infrastructure/Orchestration/SyntheticLoadGenerator.cs` — 调试用合成负载发生器，定速灌注入缝。
- `tests/Dc.App.Tests/ViewModels/LiveValueCoalescerTests.cs` — 合并器单测。
- `tests/Dc.App.Tests/ViewModels/LiveCoalesceBenchmarkTests.cs` — 合并器微基准。
- `tests/Dc.App.Tests/ViewModels/LiveDataViewModelEvictionTests.cs` — 淘汰行为单测。
- `tests/Dc.Infrastructure.Tests/Orchestration/SyntheticLoadGeneratorTests.cs` — 发生器 + 注入缝单测。

**修改：**
- `src/Dc.App/ViewModels/LiveDataViewModel.cs` — 接合并器 + O(n) 淘汰修复 + 搜索防抖 + flush 统计。
- `src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs` — `internal InjectSynthetic` 注入缝。
- `src/Dc.Infrastructure/Dc.Infrastructure.csproj` — `InternalsVisibleTo` 给测试 + 发生器（同程序集无需，仅测试需要）。
- `src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs` — `RenderPrometheus` 加 LiveFlushStats 段 + `/debug/stress` 路由 + provider/runner 字段。
- `src/Dc.App/Composition/ServiceRegistration.cs` — 注入 liveFlushProvider + stressRunner（门控 `DC_DEBUG_STRESS=1`）。
- `tests/Dc.Infrastructure.Tests/Orchestration/MetricsHttpServerTests.cs` — 补 LiveFlushStats 渲染断言。
- `~/.claude/skills/dc-remote/scripts/dc-remote.sh` — `stress` 子命令。

## 已核实的现有签名（实现据此对齐，勿臆造）

- `TagValue`（`Dc.Opc.Abstractions`）：`record TagValue(string Item, object? Value, ushort Quality, DateTimeOffset Timestamp)`，含 `IsGood`(`(Q&0xC0)==0xC0`)/`IsUncertain`(`==0x40`)/`IsBad`(`==0x00`)。
- `TaskOrchestrator`（`Dc.Infrastructure.Orchestration`，`sealed class : IAsyncDisposable`）：`public event Action<string, TagValue>? TagValueReceived;`，在内部 `TagValueReceived?.Invoke(rt.TaskId, v);` 上抛。
- `MetricsHttpServer` 构造：`(Func<IReadOnlyList<TaskDiagnostics>> diagnosticsProvider, MetricsServerOptions? options = null, ILogger<MetricsHttpServer>? logger = null, Func<byte[]?>? screenshotProvider = null)`。
- `public static string RenderPrometheus(IReadOnlyList<TaskDiagnostics> snap, DateTimeOffset now)`。
- `MetricsHttpServerTests` 模式：`var text = MetricsHttpServer.RenderPrometheus(tasks, Now); Assert.Contains("...", text);`，`Now` 为固定 `DateTimeOffset`。
- `ServiceRegistration` 构造 server：`new MetricsHttpServer(orch.GetDiagnostics, options, logger, WpfScreenshot.Capture)`（行 103-108）。
- `LiveDataViewModel` 现状关键：`_buffer = ConcurrentQueue<(string TaskId, TagValue Value)>`；`_rowIndex = Dictionary<string, LiveDataRowViewModel>`；`Rows = ObservableCollection<LiveDataRowViewModel>`；`FlushBuffer()` 内 `while(_buffer.TryDequeue(out var item)) Apply(item.TaskId, item.Value)`；淘汰 `while(_rowIndex.Count>MaxRows){ oldestKey=_rowIndex.Keys.First(); oldestRow=_rowIndex[oldestKey]; _rowIndex.Remove(oldestKey); Rows.Remove(oldestRow);}`；`MaxRows=5000`，`BatchIntervalMs=100`；`Apply` 内 `key=$"{taskId}::{v.Item}"`。`LiveDataRowViewModel` 有 `TaskId`、`Item` 公共属性。

---

## 任务 1：LiveValueCoalescer（纯算法 + 单测）

**文件：**
- 创建：`src/Dc.App/ViewModels/LiveValueCoalescer.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/LiveValueCoalescerTests.cs`

- [ ] **步骤 1：编写失败的测试**

```csharp
using Dc.App.ViewModels;
using Xunit;

namespace Dc.App.Tests.ViewModels;

public class LiveValueCoalescerTests
{
    // 用 Queue 模拟 ConcurrentQueue 的 TryDequeue。
    private static Func<(bool, string, T)> DequeueFrom<T>(Queue<(string Key, T Val)> q)
        => () => q.Count > 0 ? (true, q.Peek().Key, q.Dequeue().Val) : (false, string.Empty, default!);

    [Fact]
    public void Coalesce_MultiKey_KeepsLatestPerKey_InFirstSeenOrder()
    {
        var q = new Queue<(string, int)>(new[]
        {
            ("a", 1), ("b", 2), ("a", 3), ("c", 4), ("b", 5),
        });
        var c = new LiveValueCoalescer<int>();
        var applied = new List<(string Key, int Val)>();

        c.Coalesce(DequeueFrom(q), (k, v) => applied.Add((k, v)));

        // 每 key 仅最新值，按首现序 a,b,c
        Assert.Equal(new[] { ("a", 3), ("b", 5), ("c", 4) }, applied);
        Assert.Equal(5, c.LastInputCount);
        Assert.Equal(3, c.LastOutputCount);
    }

    [Fact]
    public void Coalesce_SingleKeyHighFrequency_AppliesOnceWithLastValue()
    {
        var q = new Queue<(string, int)>(Enumerable.Range(1, 10_000).Select(i => ("k", i)));
        var c = new LiveValueCoalescer<int>();
        var applied = new List<(string, int)>();

        c.Coalesce(DequeueFrom(q), (k, v) => applied.Add((k, v)));

        Assert.Single(applied);
        Assert.Equal(("k", 10_000), applied[0]);
        Assert.Equal(10_000, c.LastInputCount);
        Assert.Equal(1, c.LastOutputCount);
    }

    [Fact]
    public void Coalesce_EmptyInput_AppliesNothing()
    {
        var q = new Queue<(string, int)>();
        var c = new LiveValueCoalescer<int>();
        var applied = 0;

        c.Coalesce(DequeueFrom(q), (_, _) => applied++);

        Assert.Equal(0, applied);
        Assert.Equal(0, c.LastInputCount);
        Assert.Equal(0, c.LastOutputCount);
    }

    [Fact]
    public void Coalesce_ReusedInstance_ResetsBetweenCalls()
    {
        var c = new LiveValueCoalescer<int>();
        var q1 = new Queue<(string, int)>(new[] { ("a", 1) });
        c.Coalesce(DequeueFrom(q1), (_, _) => { });

        var q2 = new Queue<(string, int)>(new[] { ("b", 2), ("b", 3) });
        var applied = new List<(string, int)>();
        c.Coalesce(DequeueFrom(q2), (k, v) => applied.Add((k, v)));

        Assert.Equal(new[] { ("b", 3) }, applied); // 不含上次的 a
        Assert.Equal(2, c.LastInputCount);
        Assert.Equal(1, c.LastOutputCount);
    }
}
```

- [ ] **步骤 2：运行测试验证失败**

office 跑（dc-remote sync+build+test，或本地 Windows）。预期：FAIL，`LiveValueCoalescer` 类型不存在 / 编译错误。

- [ ] **步骤 3：编写最少实现代码**

```csharp
namespace Dc.App.ViewModels;

/// <summary>
/// 把一批原始 (key, value) 更新合并为每 key 仅最新值，保留各 key 首次出现顺序。
/// 纯算法、无框架依赖：高频流下砍掉会被立即覆盖的中间值。复用实例避免每次 flush 分配。
/// </summary>
public sealed class LiveValueCoalescer<TValue>
{
    private readonly Dictionary<string, TValue> _latest = new();
    private readonly List<string> _order = new();

    /// <summary>上次 Coalesce 的输入条数。</summary>
    public int LastInputCount { get; private set; }

    /// <summary>上次 Coalesce 的输出 key 数（实际 apply 次数）。</summary>
    public int LastOutputCount { get; private set; }

    /// <summary>
    /// 排空 tryDequeue 返回的所有项，合并为每 key 最新值；
    /// 然后按 key 首次出现顺序对每 key 触发一次 apply(key, latestValue)。
    /// </summary>
    public void Coalesce(Func<(bool ok, string key, TValue value)> tryDequeue, Action<string, TValue> apply)
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
            _latest[key] = value;
        }
        LastInputCount = input;
        LastOutputCount = _order.Count;
        foreach (var key in _order) apply(key, _latest[key]);
    }
}
```

- [ ] **步骤 4：运行测试验证通过**

office 跑 `Dc.App.Tests`。预期：4 测全 PASS。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/ViewModels/LiveValueCoalescer.cs tests/Dc.App.Tests/ViewModels/LiveValueCoalescerTests.cs
git commit -m "✨ feat(ui): LiveValueCoalescer 按 key 合并最新值（纯算法）"
```

---

## 任务 2：合并器微基准（量化合并比）

**文件：**
- 测试：`tests/Dc.App.Tests/ViewModels/LiveCoalesceBenchmarkTests.cs`

- [ ] **步骤 1：编写基准测试**

```csharp
using System.Diagnostics;
using Dc.App.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Dc.App.Tests.ViewModels;

public class LiveCoalesceBenchmarkTests
{
    private readonly ITestOutputHelper _out;
    public LiveCoalesceBenchmarkTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Coalesce_1000Keys_x10_OutputsDistinct_AndRatioAbout10()
    {
        // 1000 个 key 各 10 次更新 = 10000 条原始；轮转交错（贴近真实高频流）。
        var items = new List<(string Key, int Val)>(10_000);
        for (var round = 0; round < 10; round++)
            for (var k = 0; k < 1000; k++)
                items.Add(($"Stress::tag{k}", round * 1000 + k));
        var idx = 0;

        var c = new LiveValueCoalescer<int>();
        var appliedKeys = new HashSet<string>();

        var sw = Stopwatch.StartNew();
        c.Coalesce(
            () => idx < items.Count ? (true, items[idx].Key, items[idx++].Val) : (false, string.Empty, 0),
            (k, _) => appliedKeys.Add(k));
        sw.Stop();

        var ratio = (double)c.LastInputCount / Math.Max(1, c.LastOutputCount);
        _out.WriteLine($"input={c.LastInputCount} output={c.LastOutputCount} ratio={ratio:F1} elapsed={sw.ElapsedMilliseconds}ms");

        Assert.Equal(10_000, c.LastInputCount);
        Assert.Equal(1000, c.LastOutputCount);
        Assert.Equal(1000, appliedKeys.Count);
        Assert.True(ratio > 9.0, $"合并比应≈10，实测 {ratio:F1}");
        Assert.True(sw.ElapsedMilliseconds < 50, $"1 万条合并应 < 50ms，实测 {sw.ElapsedMilliseconds}ms");
    }
}
```

- [ ] **步骤 2：运行验证通过**

office 跑。预期：PASS，输出 `ratio≈10.0`、`elapsed` 个位数 ms。

- [ ] **步骤 3：Commit**

```bash
git add tests/Dc.App.Tests/ViewModels/LiveCoalesceBenchmarkTests.cs
git commit -m "✅ test(ui): 合并器微基准（1000 key×10，合并比≈10）"
```

---

## 任务 3：LiveDataViewModel 接合并器 + 消除淘汰线性查找

**文件：**
- 修改：`src/Dc.App/ViewModels/LiveDataViewModel.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/LiveDataViewModelEvictionTests.cs`

- [ ] **步骤 1：编写淘汰行为失败测试**

> 说明：`LiveDataViewModel` 构造需 `TaskOrchestrator` 与 `Dispatcher`。测试用 `Dispatcher.CurrentDispatcher`，并直接驱动公开的内部方法。为可测，将 `FlushBuffer` 暴露为 `internal`（`InternalsVisibleTo("Dc.App.Tests")` 已存在于 Dc.App；若无则在此任务一并加）。测试通过 `OnTagValueReceived` 之外的内部入口注入：新增 `internal void EnqueueForTest(string taskId, TagValue v) => _buffer.Enqueue((taskId, v));` 仅测试用，或直接复用事件路径。这里用 `internal void FlushForTest() => FlushBuffer();` + 既有缓冲入口。

```csharp
using System.Windows.Threading;
using Dc.App.ViewModels;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.App.Tests.ViewModels;

public class LiveDataViewModelEvictionTests
{
    // 复用 NavigateCtaTests 的既有构造范式（同 Dc.App.Tests）。
    private sealed class FakePublisherFactory : IPublisherFactory
    {
        public IPublisher Create(string address) => throw new NotSupportedException();
    }

    private static TaskOrchestrator Orch()
        => new(Array.Empty<IOpcSubscriberFactory>(), new FakePublisherFactory(), new OrchestratorOptions(), null);

    private static LiveDataViewModel NewVm(out TaskOrchestrator orch)
    {
        orch = Orch();
        return new LiveDataViewModel(orch, Dispatcher.CurrentDispatcher);
    }

    [Fact]
    public void Flush_BeyondMaxRows_EvictsOldest_AndRowCountStable()
    {
        var vm = NewVm(out _);
        // 注入 MaxRows+50 个不同 key（每 key 一次）
        for (var i = 0; i < 5050; i++)
            vm.EnqueueForTest("T1", new TagValue($"item{i}", i, 0xC0, DateTimeOffset.UtcNow));
        vm.FlushForTest();

        Assert.Equal(5000, vm.Rows.Count);
        // 最旧的 item0..item49 应被淘汰，item50 成为最旧
        Assert.DoesNotContain(vm.Rows, r => r.Item == "item0");
        Assert.DoesNotContain(vm.Rows, r => r.Item == "item49");
        Assert.Contains(vm.Rows, r => r.Item == "item50");
        Assert.Contains(vm.Rows, r => r.Item == "item5049");
    }
}
```

> 若现有测试已有构造 idle `TaskOrchestrator` 的 helper，复用之；否则本步骤新增最小 `TestOrchestrator.CreateIdle()`（构造一个无任务运行的编排器实例）。实现者按 Dc.App.Tests/Dc.Infrastructure 现有构造方式对齐（`TaskOrchestrator` 的依赖见其构造函数）。

- [ ] **步骤 2：运行测试验证失败**

office 跑。预期：FAIL（`EnqueueForTest`/`FlushForTest` 不存在，或淘汰断言因当前实现仍通过但下一步要改实现验证不回归——本测主要锁淘汰正确性，先让其编译失败）。

- [ ] **步骤 3：改实现——合并器 + 消除查找淘汰**

`LiveDataViewModel` 改动：

加字段：
```csharp
private readonly LiveValueCoalescer<(string TaskId, TagValue Value)> _coalescer = new();
```

`FlushBuffer()` 改为（替换原 `while(TryDequeue) Apply` 段）：
```csharp
private void FlushBuffer()
{
    _coalescer.Coalesce(
        tryDequeue: () => _buffer.TryDequeue(out var it)
            ? (true, $"{it.TaskId}::{it.Value.Item}", it)
            : (false, string.Empty, default),
        apply: (_, it) => Apply(it.TaskId, it.Value));

    var rawCount = _coalescer.LastInputCount; // 原始流入条数（速率用）

    // 超限淘汰：最旧恒为 Rows[0]，key 由行重建 → 无线性查找
    while (_rowIndex.Count > MaxRows)
    {
        var victim = Rows[0];
        Rows.RemoveAt(0);
        _rowIndex.Remove($"{victim.TaskId}::{victim.Item}");
    }

    if (rawCount > 0 || RowCount != Rows.Count) RowCount = Rows.Count;

    _updatesAccum += rawCount;
    var elapsed = (DateTimeOffset.UtcNow - _lastRateAt).TotalSeconds;
    if (elapsed >= 1.0)
    {
        UpdatesPerSecond = _updatesAccum / elapsed;
        _updatesAccum = 0;
        _lastRateAt = DateTimeOffset.UtcNow;
    }
}
```

加测试入口（`internal`）：
```csharp
internal void EnqueueForTest(string taskId, TagValue v) => _buffer.Enqueue((taskId, v));
internal void FlushForTest() => FlushBuffer();
```

确保 `Dc.App.csproj` 有 `<InternalsVisibleTo Include="Dc.App.Tests" />`（若无则加 `AssemblyAttribute` 或 csproj ItemGroup）。

- [ ] **步骤 4：运行测试验证通过 + 无回归**

office 跑全量 `Dc.App.Tests`。预期：淘汰测 PASS，原有 LiveData 相关测不回归。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/ViewModels/LiveDataViewModel.cs src/Dc.App/Dc.App.csproj tests/Dc.App.Tests/ViewModels/LiveDataViewModelEvictionTests.cs
git commit -m "♻️ refactor(ui): LiveData 接合并器 + 消除淘汰线性查找"
```

---

## 任务 4：搜索防抖

**文件：**
- 修改：`src/Dc.App/ViewModels/LiveDataViewModel.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/LiveDataViewModelEvictionTests.cs`（同文件追加防抖测）

- [ ] **步骤 1：编写失败测试**

> 防抖核心是「连续变更只在静默后过滤一次」。直接测 DispatcherTimer 时序脆弱，故将「是否应刷新」判定与 timer 解耦：用一个 `internal int RefreshCount` 计数 `RowsView.Refresh()` 实际触发次数，测试驱动 DispatcherFrame 等待。更稳的做法：暴露 `internal void DebounceTickForTest()` 直接触发防抖 Tick，验证多次 `SearchText` 赋值只累计一次待刷新、Tick 后计数 +1。

```csharp
[Fact]
public void SearchText_RapidChanges_RefreshesOnceAfterDebounceTick()
{
    var vm = NewVm(out _);
    vm.ResetRefreshCountForTest();

    vm.SearchText = "a";
    vm.SearchText = "ab";
    vm.SearchText = "abc";
    // 防抖窗口内多次赋值，尚未到 Tick
    Assert.Equal(0, vm.RefreshCountForTest);

    vm.DebounceTickForTest(); // 模拟 250ms 静默后 Tick
    Assert.Equal(1, vm.RefreshCountForTest);
}
```

- [ ] **步骤 2：运行验证失败**

office 跑。预期：FAIL（成员不存在）。

- [ ] **步骤 3：实现防抖**

`LiveDataViewModel` 加：
```csharp
private const int SearchDebounceMs = 250;
private DispatcherTimer? _searchDebounce;
internal int RefreshCountForTest { get; private set; }
internal void ResetRefreshCountForTest() => RefreshCountForTest = 0;

private void DoRefresh()
{
    RowsView.Refresh();
    RefreshCountForTest++;
}

internal void DebounceTickForTest()
{
    _searchDebounce?.Stop();
    DoRefresh();
}
```

构造函数末尾初始化：
```csharp
_searchDebounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
{
    Interval = TimeSpan.FromMilliseconds(SearchDebounceMs)
};
_searchDebounce.Tick += (_, _) => { _searchDebounce!.Stop(); DoRefresh(); };
```

`OnSearchTextChanged` 改为重置防抖（不再即时 Refresh）：
```csharp
partial void OnSearchTextChanged(string value)
{
    _searchDebounce?.Stop();
    _searchDebounce?.Start();
}
```

`OnTaskFilterChanged` 保持即时 `DoRefresh()`（下拉选择非高频）。`Clear()` 与 `Dispose()`/`Stop()` 中 `_searchDebounce?.Stop();`。

- [ ] **步骤 4：运行验证通过**

office 跑。预期：防抖测 PASS，无回归。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/ViewModels/LiveDataViewModel.cs tests/Dc.App.Tests/ViewModels/LiveDataViewModelEvictionTests.cs
git commit -m "✨ feat(ui): LiveData 搜索防抖 250ms（5000 行打字不顿）"
```

---

## 任务 5：LiveFlushStats record + VM flush 统计

**文件：**
- 创建：`src/Dc.Infrastructure/Orchestration/LiveFlushStats.cs`
- 修改：`src/Dc.App/ViewModels/LiveDataViewModel.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/LiveDataViewModelEvictionTests.cs`（追加 flush 统计测）

- [ ] **步骤 1：建 LiveFlushStats record**

```csharp
namespace Dc.Infrastructure.Orchestration;

/// <summary>LiveData flush 指标快照（App VM 填充，/metrics 渲染消费）。</summary>
public sealed record LiveFlushStats(
    double P50Ms,
    double P95Ms,
    double CoalesceRatio,
    int Rows,
    double UpdatesPerSecond);
```

- [ ] **步骤 2：编写 VM 统计失败测试**

```csharp
[Fact]
public void FlushStats_AfterFlushes_ReportsRatioAndRows()
{
    var vm = NewVm(out _);
    // 同 key 多次 + 多 key：制造合并比
    for (var r = 0; r < 5; r++)
        for (var k = 0; k < 100; k++)
            vm.EnqueueForTest("T1", new TagValue($"item{k}", r, 0xC0, DateTimeOffset.UtcNow));
    vm.FlushForTest();

    var s = vm.GetFlushStats();
    Assert.Equal(100, s.Rows);
    Assert.True(s.CoalesceRatio > 4.5, $"500 输入/100 输出≈5，实测 {s.CoalesceRatio:F1}");
    Assert.True(s.P50Ms >= 0);
    Assert.True(s.P95Ms >= s.P50Ms);
}
```

- [ ] **步骤 3：实现统计**

`LiveDataViewModel` 加（累计合并比 + flush 耗时环形缓冲算分位）：
```csharp
private long _totalCoalesceIn;
private long _totalCoalesceOut;
private readonly double[] _flushMsRing = new double[128];
private int _flushMsCount;
private int _flushMsHead;

public LiveFlushStats GetFlushStats()
{
    double p50, p95;
    var n = Math.Min(_flushMsCount, _flushMsRing.Length);
    if (n == 0) { p50 = 0; p95 = 0; }
    else
    {
        var copy = new double[n];
        Array.Copy(_flushMsRing, copy, n);
        Array.Sort(copy);
        p50 = copy[(int)(n * 0.50)];
        p95 = copy[Math.Min(n - 1, (int)(n * 0.95))];
    }
    var ratio = _totalCoalesceOut > 0 ? (double)_totalCoalesceIn / _totalCoalesceOut : 0;
    return new LiveFlushStats(p50, p95, ratio, Rows.Count, UpdatesPerSecond);
}
```

`FlushBuffer()` 内用 `Stopwatch` 包住 coalesce + 淘汰，结束记录：
```csharp
var sw = System.Diagnostics.Stopwatch.StartNew();
// ... 现有 coalesce + 淘汰 ...
sw.Stop();
_totalCoalesceIn += _coalescer.LastInputCount;
_totalCoalesceOut += _coalescer.LastOutputCount;
_flushMsRing[_flushMsHead] = sw.Elapsed.TotalMilliseconds;
_flushMsHead = (_flushMsHead + 1) % _flushMsRing.Length;
_flushMsCount++;
```

`Dc.App` 已引用 `Dc.Infrastructure`（编排器），故可用 `LiveFlushStats`。

- [ ] **步骤 4：运行验证通过**

office 跑。预期：统计测 PASS。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/LiveFlushStats.cs src/Dc.App/ViewModels/LiveDataViewModel.cs tests/Dc.App.Tests/ViewModels/LiveDataViewModelEvictionTests.cs
git commit -m "✨ feat(ui): LiveData flush 统计（合并比/p50/p95）"
```

---

## 任务 6：/metrics 暴露 LiveData flush 段（Linux 可测）

**文件：**
- 修改：`src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs`
- 测试：`tests/Dc.Infrastructure.Tests/Orchestration/MetricsHttpServerTests.cs`

> 注：LiveData flush 指标为 App-UI 侧、与采集任务无关，无 OTel Meter 消费方，故**仅经 /metrics（Prometheus 文本）暴露**，不镜像到 DiagnosticsReporter Meter。这是对双路径约定的有意例外（双路径仅约束 collector 任务指标）。在代码注释中写明。

- [ ] **步骤 1：编写失败测试**

```csharp
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
```

- [ ] **步骤 2：运行验证失败**

Linux 跑 `Dc.Infrastructure.Tests`。预期：FAIL（重载不存在）。

- [ ] **步骤 3：实现重载 + 渲染**

`RenderPrometheus` 加可选参数（保持旧调用点不变）：
```csharp
public static string RenderPrometheus(IReadOnlyList<TaskDiagnostics> snap, DateTimeOffset now,
    LiveFlushStats? live = null)
{
    var sb = new StringBuilder(256 + snap.Count * 256);
    // ... 现有 collector gauge 渲染 ...

    // LiveData flush（仅 /metrics 暴露，无 Meter 镜像——UI 侧指标无 OTel 消费方）
    if (live is not null)
    {
        Gauge(sb, "dc_livedata_flush_ms_p50", "LiveData flush 耗时 p50（毫秒）。", g => g.Line(null, live.P50Ms));
        Gauge(sb, "dc_livedata_flush_ms_p95", "LiveData flush 耗时 p95（毫秒）。", g => g.Line(null, live.P95Ms));
        Gauge(sb, "dc_livedata_coalesce_ratio", "LiveData 合并比（原始/输出）。", g => g.Line(null, live.CoalesceRatio));
        Gauge(sb, "dc_livedata_rows", "LiveData 当前行数。", g => g.Line(null, live.Rows));
        Gauge(sb, "dc_livedata_updates_per_second", "LiveData 每秒原始更新数。", g => g.Line(null, live.UpdatesPerSecond));
    }
    return sb.ToString();
}
```

> 用现有 `Gauge(sb, name, help, g => g.Line(null, value))` 辅助（与现有 collector gauge 同款；`Line(null, v)` 为无标签单值）。若 `Gauge`/`Line` 签名不同，对齐现有用法（见文件内 `dc_collector_up` 渲染）。

`MetricsHttpServer` 加字段 + 构造参数 + `/metrics` 调用点：
```csharp
private readonly Func<LiveFlushStats?>? _liveFlushProvider;
// 构造函数追加参数：Func<LiveFlushStats?>? liveFlushProvider = null  → _liveFlushProvider = liveFlushProvider;
// case "/metrics": RenderPrometheus(_provider(), DateTimeOffset.UtcNow, _liveFlushProvider?.Invoke())
```

- [ ] **步骤 4：运行验证通过**

Linux 跑 `Dc.Infrastructure.Tests`。预期：2 新测 PASS，原有 metrics 测不回归。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs tests/Dc.Infrastructure.Tests/Orchestration/MetricsHttpServerTests.cs
git commit -m "✨ feat(diag): /metrics 暴露 LiveData flush 段（p50/p95/合并比/行数）"
```

---

## 任务 7：编排器注入缝 + SyntheticLoadGenerator（Linux 可测）

**文件：**
- 修改：`src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs`
- 修改：`src/Dc.Infrastructure/Dc.Infrastructure.csproj`（`InternalsVisibleTo Dc.Infrastructure.Tests`，若无）
- 创建：`src/Dc.Infrastructure/Orchestration/SyntheticLoadGenerator.cs`
- 测试：`tests/Dc.Infrastructure.Tests/Orchestration/SyntheticLoadGeneratorTests.cs`

- [ ] **步骤 1：编写失败测试**

```csharp
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class SyntheticLoadGeneratorTests
{
    [Fact]
    public async Task RunAsync_InjectsApproxTagsTimesHzTimesSeconds_DistinctKeys()
    {
        var received = new List<(string TaskId, TagValue V)>();
        var gen = new SyntheticLoadGenerator((taskId, v) => { lock (received) received.Add((taskId, v)); });

        var injected = await gen.RunAsync("stress", tags: 100, hz: 10, seconds: 1, CancellationToken.None);

        // 100 tags × 10 hz × 1 s = 1000，允许 ±1 个周期容差
        Assert.InRange(injected, 800, 1100);
        Assert.InRange(received.Count, 800, 1100);
        var distinct = received.Select(r => r.V.Item).Distinct().Count();
        Assert.Equal(100, distinct);
        Assert.All(received, r => Assert.StartsWith("Stress::tag", r.V.Item));
        // 含少量非 Good 质量（验证着色路径）
        Assert.Contains(received, r => !r.V.IsGood);
    }

    [Fact]
    public async Task RunAsync_Cancelled_StopsEarly()
    {
        var count = 0;
        var gen = new SyntheticLoadGenerator((_, _) => Interlocked.Increment(ref count));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var injected = await gen.RunAsync("stress", tags: 50, hz: 10, seconds: 30, cts.Token);
        Assert.True(injected < 50 * 10 * 30, "取消后应提前停止");
    }
}
```

- [ ] **步骤 2：运行验证失败**

Linux 跑。预期：FAIL（类型不存在）。

- [ ] **步骤 3：实现注入缝 + 发生器**

`TaskOrchestrator` 加：
```csharp
/// <summary>调试用合成注入：直接触发 TagValueReceived，与真值同路径。仅门控后被调用。</summary>
internal void InjectSynthetic(string taskId, TagValue v) => TagValueReceived?.Invoke(taskId, v);
```

> `InjectSynthetic` 为 `internal` 而非 public：防止外部误用进真采集路径。`SyntheticLoadGenerator` 在同程序集可直接调，无需 InternalsVisibleTo（仅测试工程需 `InternalsVisibleTo("Dc.Infrastructure.Tests")`，本任务确认/添加）。

`SyntheticLoadGenerator`：
```csharp
using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

/// <summary>
/// 调试用：按 tags 个 key × hz 频率合成 TagValue，定速灌进编排器 TagValueReceived 路径，
/// 绕过真 OPC，仅压 VM→UI 渲染段。门控后才构造/调用，绝不进真采集路径。
/// </summary>
public sealed class SyntheticLoadGenerator
{
    private readonly Action<string, TagValue> _inject;
    public SyntheticLoadGenerator(Action<string, TagValue> inject) => _inject = inject;

    /// <summary>持续 seconds 秒，每 1/hz 秒为 tags 个 key 各发一个递增值。返回实际注入条数。</summary>
    public async Task<long> RunAsync(string taskId, int tags, int hz, int seconds, CancellationToken ct)
    {
        if (tags <= 0 || hz <= 0 || seconds <= 0) return 0;
        hz = Math.Min(hz, 1000);
        seconds = Math.Min(seconds, 300); // 防失控
        var period = TimeSpan.FromSeconds(1.0 / hz);
        var totalTicks = hz * seconds;
        long injected = 0;
        long seq = 0;
        using var timer = new PeriodicTimer(period);
        for (var tick = 0; tick < totalTicks; tick++)
        {
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
            for (var i = 0; i < tags; i++)
            {
                seq++;
                // 每 ~50 个掺 1 个非 Good（Bad 0x00 / Uncertain 0x40 交替）验证着色
                ushort quality = (seq % 50 == 0) ? (ushort)0x00 : (seq % 97 == 0) ? (ushort)0x40 : (ushort)0xC0;
                _inject(taskId, new TagValue($"Stress::tag{i}", seq, quality, DateTimeOffset.UtcNow));
                injected++;
            }
        }
        return injected;
    }
}
```

- [ ] **步骤 4：运行验证通过**

Linux 跑。预期：2 测 PASS（注：第一个测约耗 1s）。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs src/Dc.Infrastructure/Orchestration/SyntheticLoadGenerator.cs src/Dc.Infrastructure/Dc.Infrastructure.csproj tests/Dc.Infrastructure.Tests/Orchestration/SyntheticLoadGeneratorTests.cs
git commit -m "✨ feat(diag): 合成负载发生器 + 编排器注入缝（门控压测用）"
```

---

## 任务 8：/debug/stress 端点 + 门控 + ServiceRegistration 装配

**文件：**
- 修改：`src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs`
- 修改：`src/Dc.App/Composition/ServiceRegistration.cs`
- 测试：`tests/Dc.Infrastructure.Tests/Orchestration/MetricsHttpServerTests.cs`（端点门控测）

> 门控采用「provider 存在即启用」模式（与 screenshot 一致）：App 仅当 `DC_DEBUG_STRESS=1` 时传入 `stressRunner`；为 null → `/debug/stress` 落 404。env 判定在 ServiceRegistration。

- [ ] **步骤 1：编写门控测试**

> `/debug/stress` 走真 HttpListener 端到端（参考现有 Disabled 端点测的真起 listener 方式）。两例：(a) 未传 runner → 404；(b) 传 runner → 200 + JSON 含注入数 + runner 被调用一次、参数透传。

```csharp
[Fact]
public async Task DebugStress_NoRunner_Returns404()
{
    using var srv = StartServer(stressRunner: null); // helper：起在随机端口
    var resp = await srv.Client.PostAsync(srv.Url("/debug/stress?tags=10&hz=5&seconds=1"), null);
    Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
}

[Fact]
public async Task DebugStress_WithRunner_InvokesWithParsedArgs_Returns200()
{
    (int Tags, int Hz, int Sec) got = default;
    Func<int, int, int, Task<long>> runner = (t, h, s) => { got = (t, h, s); return Task.FromResult(123L); };
    using var srv = StartServer(stressRunner: runner);

    var resp = await srv.Client.PostAsync(srv.Url("/debug/stress?tags=1000&hz=20&seconds=30"), null);
    var body = await resp.Content.ReadAsStringAsync();

    Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    Assert.Equal((1000, 20, 30), got);
    Assert.Contains("123", body); // injected 数
}
```

> `StartServer`/`srv` helper：实现者参考本测试文件中现有「真起 HttpListener」测的搭建方式（随机端口、`MetricsServerOptions{Enabled=true, Prefix=...}`、构造注入 runner、`StartAsync`、HttpClient、Dispose 调 `StopAsync`）。若文件无现成 helper，则新增一个最小私有 helper。

- [ ] **步骤 2：运行验证失败**

Linux 跑。预期：FAIL（路由不存在 / 构造无 runner 参数）。

- [ ] **步骤 3：实现端点 + 构造参数**

`MetricsHttpServer`：
```csharp
private readonly Func<int, int, int, Task<long>>? _stressRunner; // (tags,hz,seconds)->injected
// 构造追加参数：Func<int,int,int,Task<long>>? stressRunner = null → _stressRunner = stressRunner;
```

`Handle` 加路由：
```csharp
case "/debug/stress":
    if (_stressRunner is null) { Write(ctx, 404, "text/plain; charset=utf-8", "not found"); break; }
    var q = ctx.Request.QueryString;
    var tags = ParseInt(q["tags"], 1000);
    var hz = ParseInt(q["hz"], 10);
    var seconds = ParseInt(q["seconds"], 30);
    var injected = _stressRunner(tags, hz, seconds).GetAwaiter().GetResult();
    Write(ctx, 200, "application/json; charset=utf-8", $"{{\"injected\":{injected},\"tags\":{tags},\"hz\":{hz},\"seconds\":{seconds}}}");
    break;
```

加私有 helper：
```csharp
private static int ParseInt(string? s, int def) => int.TryParse(s, out var v) && v > 0 ? v : def;
```

启动日志端点列表：runner 非空时附 ` /debug/stress`（仿 screenshot 写法）。

`ServiceRegistration` 行 103-108 改为注入 liveFlushProvider + 门控 stressRunner：
```csharp
services.AddSingleton<MetricsHttpServer>(sp =>
{
    var orch = sp.GetRequiredService<TaskOrchestrator>();
    var stressEnabled = Environment.GetEnvironmentVariable("DC_DEBUG_STRESS") == "1";
    Func<int, int, int, Task<long>>? stressRunner = stressEnabled
        ? (tags, hz, seconds) => new SyntheticLoadGenerator(orch.InjectSynthetic).RunAsync("stress", tags, hz, seconds, CancellationToken.None)
        : null;
    return new MetricsHttpServer(
        orch.GetDiagnostics,
        sp.GetRequiredService<MetricsServerOptions>(),
        sp.GetService<Microsoft.Extensions.Logging.ILogger<MetricsHttpServer>>(),
        Dc.App.Services.Diagnostics.WpfScreenshot.Capture,
        liveFlushProvider: () => sp.GetService<LiveDataViewModel>()?.GetFlushStats(),
        stressRunner: stressRunner);
});
```

> **已核实的两个落地点：**
> 1. `LiveDataViewModel` 在 `ServiceRegistration:209` 为 **`AddSingleton`** → provider 直接 `() => sp.GetService<LiveDataViewModel>()?.GetFlushStats()`（lazy，VM 未建时返回 null → /metrics 不渲染 LiveData 段，与 screenshot 503 同理）。无需任何「当前活跃实例」注册表。
> 2. `orch.InjectSynthetic` 为 internal，而 `ServiceRegistration` 在 `Dc.App` 程序集 → **必须**在 `Dc.Infrastructure` 加 `InternalsVisibleTo("Dc.App")`（保持 internal 防外部程序集误用进真采集路径）。与任务7给测试的 `InternalsVisibleTo("Dc.Infrastructure.Tests")` 并列。
>
> `MetricsServerOptions.Enabled` 默认 false（产线诊断端口默认关）；压测时需经配置开启诊断端口（dc-remote stress 起 App 时确保 `Diagnostics:Http:Enabled=true`，见任务9）。
> 构造内 `new SyntheticLoadGenerator(...)` 每请求新建无妨（轻量无状态）。

- [ ] **步骤 4：运行验证通过**

Linux 跑 `Dc.Infrastructure.Tests`。预期：门控 2 测 PASS。**App 装配编译**走 office：dc-remote `sync`+`build` 确认 `ServiceRegistration` 改动编译通过（涉及 Dc.App）。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/MetricsHttpServer.cs src/Dc.Infrastructure/Dc.Infrastructure.csproj src/Dc.App/Composition/ServiceRegistration.cs tests/Dc.Infrastructure.Tests/Orchestration/MetricsHttpServerTests.cs
git commit -m "✨ feat(diag): /debug/stress 门控端点 + 装配合成负载发生器"
```

---

## 任务 9：dc-remote `stress` 子命令（office 真压测闭环）

**文件：**
- 修改：`~/.claude/skills/dc-remote/scripts/dc-remote.sh`

> 主线交付物。复用现有 helper：`_desktop_run`（注入可见会话起 App）、`shot`（/screenshot）、`ui`（导航 LiveData）、`$SSH`、`filt`、metrics base `http://localhost:9090`。脚本与推送的 win/*.ps1 保持纯 ASCII。

- [ ] **步骤 1：加 stress 子命令派发**

在 `case "$cmd"` 派发块加（参数：tags hz seconds，默认 1000/10/30）：
```bash
  stress)
    tags="${1:-1000}"; hz="${2:-10}"; secs="${3:-30}"
    echo "[stress] tags=$tags hz=$hz seconds=$secs"
    # 1) 以门控起 App（DC_DEBUG_STRESS=1）。沿用 run 的桌面注入，追加环境变量。
    _desktop_run "\$env:DC_DEBUG_STRESS='1'; & \"\$env:USERPROFILE\\path-to-app\\Dc.App.exe\"" || exit 1
    # 2) 等就绪
    $SSH "powershell -NoProfile -Command \"for(\$i=0;\$i -lt 30;\$i++){ try{ if((Invoke-WebRequest -UseBasicParsing http://localhost:9090/healthz -TimeoutSec 2).StatusCode -eq 200){'READY';break} }catch{}; Start-Sleep 1 }\"" | filt
    # 3) 切到 LiveData 页（UIA 导航；目标名对齐导航项 AutomationId/名称）
    _desktop_run "$UIPS -Op click -Target '实时数据'" || true
    # 4) 触发压测
    $SSH "powershell -NoProfile -Command \"(Invoke-WebRequest -UseBasicParsing -Method POST 'http://localhost:9090/debug/stress?tags=$tags&hz=$hz&seconds=$secs' -TimeoutSec $((secs+15))).Content\"" | filt
    # 5) 压测中途截图 + 回采指标，结束再截一张
    $SSH "powershell -NoProfile -Command \"(Invoke-WebRequest -UseBasicParsing http://localhost:9090/metrics -TimeoutSec 5).Content\"" 2>&1 | filt | grep -a 'dc_livedata_'
    "$0" shot
    ;;
```

> 实际 App.exe 路径、导航项 Target、`UIPS` 变量沿用脚本内既有定义（`run`/`ui` 子命令已有）。`/debug/stress` 同步返回（端点内 `GetAwaiter().GetResult()` 等满 seconds），故步骤 4 的超时设为 `secs+15`。

- [ ] **步骤 2：出小报告（解析 dc_livedata_*）**

压测后拉一次 metrics，提取关键行打印判据：
```bash
    # 报告：p95 是否超 BatchInterval(100ms) => 是否卡
    m=$($SSH "powershell -NoProfile -Command \"(Invoke-WebRequest -UseBasicParsing http://localhost:9090/metrics -TimeoutSec 5).Content\"" 2>&1 | filt)
    p95=$(echo "$m" | grep -a '^dc_livedata_flush_ms_p95 ' | awk '{print $2}')
    ratio=$(echo "$m" | grep -a '^dc_livedata_coalesce_ratio ' | awk '{print $2}')
    rows=$(echo "$m" | grep -a '^dc_livedata_rows ' | awk '{print $2}')
    ups=$(echo "$m" | grep -a '^dc_livedata_updates_per_second ' | awk '{print $2}')
    echo "==== STRESS REPORT ===="
    echo "tags=$tags hz=$hz seconds=$secs"
    echo "rows=$rows updates/s=$ups coalesce_ratio=$ratio flush_p95_ms=$p95"
    awk -v p="$p95" 'BEGIN{ if (p+0 <= 100) print "VERDICT: OK (p95<=100ms 不卡)"; else print "VERDICT: JANK (p95>100ms)"}'
```

- [ ] **步骤 3：手动验收（office）**

```
dc-remote sync && dc-remote build && dc-remote stress 1000 10 30
dc-remote stress 5000 20 30
```
预期：两档跑通，打印 REPORT；截图显示 LiveData 满屏滚动值、质量着色正常；1000@10 档 VERDICT OK。5000@20 档记录实测 p95 作为天花板数字（不强制 OK，作为量化结果）。

- [ ] **步骤 4：Commit**

```bash
git add ~/.claude/skills/dc-remote/scripts/dc-remote.sh   # 注：技能脚本在 ~ 下，按 dc-remote 既有提交方式处理
git commit -m "✨ feat(dc-remote): stress 子命令——LiveData 压测闭环（触发/回采/截图/报告）"
```

> dc-remote 脚本不在本仓库；按 memory `dc-remote-skill` 与 `skill-evolution-during-use` 的既有方式管理（脚本所在的 skill 仓库/同步机制）。本仓库内不提交该文件。

---

## 自检结论（计划编写者已执行）

**规格覆盖度：** 规格 §4.1→任务1/2；§4.2→任务3；§4.3→任务4；§4.4→任务5/6；§4.5→任务7；§4.6→任务8；§4.7→任务9。§6 测试分布对应各任务测试步骤 + 任务9手动验收。§8 验收标准逐条有任务承载。无遗漏。

**占位符扫描：** 各步骤含真实代码/命令/预期。任务3/8 中两处「按现有方式对齐」（TestOrchestrator 构造、LiveDataViewModel 的 DI 注册形态）为**真实需现场核对的集成点**，已给出明确决策路径（查注册形态→二选一方案），非占位符。

**类型一致性：** `LiveValueCoalescer<TValue>`（任务1）→ VM 用 `LiveValueCoalescer<(string,TagValue)>`（任务3）一致；`LiveFlushStats(P50Ms,P95Ms,CoalesceRatio,Rows,UpdatesPerSecond)`（任务5）→ 渲染（任务6）字段一致；`InjectSynthetic(string,TagValue)`（任务7）→ 装配调用（任务8）一致；`RunAsync(taskId,tags,hz,seconds,ct)`（任务7）→ stressRunner 签名 `(tags,hz,seconds)->Task<long>`（任务8）经 lambda 适配一致；`/debug/stress?tags&hz&seconds`（任务8）→ dc-remote 调用（任务9）一致。

**集成点（已现场核实，落地点确定）：**
1. ✅ 任务3 `TaskOrchestrator` 测试构造——复用 `NavigateCtaTests` 既有范式 `new(Array.Empty<IOpcSubscriberFactory>(), new FakePublisherFactory(), new OrchestratorOptions(), null)`，已写入任务3。
2. ✅ 任务8 `liveFlushProvider`——`LiveDataViewModel` 为 `AddSingleton`（`ServiceRegistration:209`），provider 直取 `sp.GetService<LiveDataViewModel>()?.GetFlushStats()`，无需注册表。
3. ✅ 任务8 `InternalsVisibleTo("Dc.App")` 必加（`Dc.App` 现无任何 InternalsVisibleTo）；任务3 `InternalsVisibleTo("Dc.App.Tests")`、任务7 `InternalsVisibleTo("Dc.Infrastructure.Tests")` 同此确认须新增。
4. 任务9 压测前确保诊断端口开启（`Diagnostics:Http:Enabled=true`）——现有 `shot`/`verify` 已依赖 `localhost:9090`，office App 起法应已开启；stress 子命令沿用同一起法。
