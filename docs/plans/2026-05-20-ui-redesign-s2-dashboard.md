# UI 重设计 S2 — Dashboard 真实实现 实施计划

**Goal:** 把 S1 的 Dashboard 占位卡换成 C 风格（暗色 NOC/SCADA 风）真实仪表盘：左栏告警列表，中央健康度大数字 + 6 环形指标，右栏任务速率列表。

**Architecture:**
新增 `HealthSnapshot` / `AlertItem` 数据契约，`HealthEvaluator` 把 `TaskDiagnostics[]` 转成快照（健康度评分、告警、6 个 KPI）。`DashboardViewModel` 用 `DispatcherTimer` 1s 拉取 `TaskOrchestrator.GetDiagnostics()` + `RunningTaskIds`，喂给 `HealthEvaluator` 输出 `HealthSnapshot`，绑定到 XAML。Dashboard 视觉永远暗色（不跟随主题），匹配 SCADA 状态板心智模型。

**Tech Stack:** .NET 8 + WPF + Wpf.Ui + CommunityToolkit.Mvvm + xUnit + Moq

**Spec:** `wpf/docs/specs/2026-05-19-ui-redesign-fluent-design.md` (Stage S2)
**Mockup**: `/tmp/dc-mockups/c-status-board.html`

---

## 已锁定决策

| 项 | 决策 | 原因 |
|---|---|---|
| 数据源 | `TaskOrchestrator.GetDiagnostics()` + `RunningTaskIds` | 已有 API，零侵入 |
| 刷新 | DispatcherTimer 1s | 与现 DiagnosticsView 一致 |
| 健康度公式 | 100 起点；每"任务停止" -15、每心跳过期 -5、每"高错误率任务" -3；底 0 | 简单易解释 |
| 告警分级 | 严重（停止）/ 警告（心跳/错误）；显示前 5 条 | 信息密度可控 |
| 队列积压 | S2 显示 "—"（hard-code 0 帧 + TODO 注释） | publisher per-task，统计需要新 API，S2 不打破层级 |
| 主题 | Dashboard 永远暗色（不跟随系统/亮/暗切换） | SCADA 状态板心智模型，与 c-status-board mockup 一致 |
| 6 环 | 任务 / Tags / 速率 / 错误/h / 队列 / 运行时长 | 完全照搬 mockup |
| 速率窗口 | 1s 内 ValueCount 差值 | 简单 |
| 错误/h | S2 简化为累计 PublishErrorCount（TODO 改滑动窗口） | YAGNI |
| 运行时长 | Now - 最早 StartedAt（无任务则 "—"） | 不引 Process.StartTime |

---

## 前置说明

dotnet 路径：`/home/adamyu/.dotnet/dotnet`，PATH 先 export。

构建命令统一：
```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Linux 上 `Dc.App.Tests` 跑不了 net8.0-windows runtime，只能 build verification — 同 S1。

测试基线（S1 完成时）：infra 48 + integration 10 + Dc.App.Tests 14。S2 完成时 Dc.App.Tests 应 +6~8 个测试。

---

## Task 1: HealthSnapshot 数据契约 + HealthEvaluator (TDD)

**Files:**
- Create: `wpf/src/Dc.App/Dashboard/HealthSnapshot.cs`
- Create: `wpf/src/Dc.App/Dashboard/AlertItem.cs`
- Create: `wpf/src/Dc.App/Dashboard/HealthEvaluator.cs`
- Create: `wpf/tests/Dc.App.Tests/Dashboard/HealthEvaluatorTests.cs`

- [ ] **Step 1: 数据契约**

`wpf/src/Dc.App/Dashboard/AlertItem.cs`:

```csharp
namespace Dc.App.Dashboard;

public enum AlertSeverity { Critical, Warning }

public sealed record AlertItem(
    AlertSeverity Severity,
    string TaskId,
    string TaskName,         // 与 TaskId 相同，预留 S3 改成 Server label
    string Message,
    DateTimeOffset OccurredAt);
```

`wpf/src/Dc.App/Dashboard/HealthSnapshot.cs`:

```csharp
namespace Dc.App.Dashboard;

public sealed record HealthSnapshot(
    int HealthScore,              // 0-100
    int RunningTasks,
    int TotalTasks,
    int ActiveTags,
    double MessagesPerSecond,     // 1s 窗口
    long ErrorsTotal,             // 累计（TODO: 滑动 1h 窗口）
    long QueueBackedFrames,       // 占位 0
    TimeSpan? Uptime,
    IReadOnlyList<AlertItem> Alerts,
    IReadOnlyList<TaskRowSummary> Tasks);

public sealed record TaskRowSummary(
    string TaskId,
    bool IsRunning,
    AlertSeverity? Severity,      // null = 正常；Critical/Warning 与告警匹配
    double RatePerSecond);
```

- [ ] **Step 2: 写 HealthEvaluator 测试（Red）**

`wpf/tests/Dc.App.Tests/Dashboard/HealthEvaluatorTests.cs`:

```csharp
using Dc.App.Dashboard;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.Tests.Dashboard;

public class HealthEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(120);

    private static TaskDiagnostics Diag(
        string id,
        DateTimeOffset? lastHeartbeat = null,
        long valueCount = 0,
        long errors = 0,
        int restarts = 0,
        int tags = 0,
        DateTimeOffset? startedAt = null) =>
        new(id, startedAt ?? Now.AddMinutes(-10), Now.AddSeconds(-2),
            lastHeartbeat ?? Now.AddSeconds(-1), valueCount, errors, restarts, tags);

    [Fact]
    public void Empty_NoTasks_HealthIs100_ZeroAlerts()
    {
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: Array.Empty<TaskDiagnostics>(),
            runningTaskIds: Array.Empty<string>(),
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(100, snap.HealthScore);
        Assert.Empty(snap.Alerts);
        Assert.Equal(0, snap.RunningTasks);
    }

    [Fact]
    public void StoppedTask_GeneratesCriticalAlert_Score_Minus15()
    {
        var diag = Diag("t1");
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: new[] { diag },
            runningTaskIds: Array.Empty<string>(),   // diag 在 diagnostics 中但 不在 running
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(85, snap.HealthScore);
        var alert = Assert.Single(snap.Alerts);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("已停止", alert.Message);
    }

    [Fact]
    public void StaleHeartbeat_GeneratesWarning_Score_Minus5()
    {
        var diag = Diag("t1", lastHeartbeat: Now.AddSeconds(-150));  // > 120s timeout
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: new[] { diag },
            runningTaskIds: new[] { "t1" },
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(95, snap.HealthScore);
        var alert = Assert.Single(snap.Alerts);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("心跳", alert.Message);
    }

    [Fact]
    public void ErrorRatePresent_GeneratesWarning_Score_Minus3()
    {
        var diag = Diag("t1", errors: 5);
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: new[] { diag },
            runningTaskIds: new[] { "t1" },
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(97, snap.HealthScore);
        var alert = Assert.Single(snap.Alerts);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("错误", alert.Message);
    }

    [Fact]
    public void HealthScore_FloorIs0_NotNegative()
    {
        var stopped1 = Diag("t1");
        var stopped2 = Diag("t2");
        var stopped3 = Diag("t3");
        var stopped4 = Diag("t4");
        var stopped5 = Diag("t5");
        var stopped6 = Diag("t6");
        var stopped7 = Diag("t7");
        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: new[] { stopped1, stopped2, stopped3, stopped4, stopped5, stopped6, stopped7 },
            runningTaskIds: Array.Empty<string>(),
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        // 7 stopped × -15 = -105，clamp 到 0
        Assert.Equal(0, snap.HealthScore);
    }

    [Fact]
    public void Rate_UsesPreviousSnapshotForDelta()
    {
        var prev = new HealthSnapshot(100, 1, 1, 0, 0, 0, 0, null,
            Array.Empty<AlertItem>(),
            new[] { new TaskRowSummary("t1", true, null, 0) });

        // 模拟 1s 前 ValueCount=100，现在 ValueCount=180 → 80/s
        // HealthEvaluator 用 previous snapshot 的 timestamp 推断窗口
        // 实现里可以把 previous 用 dictionary by task id 暴露 last value count
        // S2 用最简: 把 previous valueCounts dict 当作输入

        var diag = Diag("t1", valueCount: 180);
        var snap = HealthEvaluator.Evaluate(
            previous: prev,
            previousValueCounts: new Dictionary<string, (long Count, DateTimeOffset At)>
            {
                ["t1"] = (100, Now.AddSeconds(-1))
            },
            diagnostics: new[] { diag },
            runningTaskIds: new[] { "t1" },
            now: Now,
            heartbeatTimeout: HeartbeatTimeout);

        Assert.Equal(80.0, snap.MessagesPerSecond, precision: 1);
        var row = Assert.Single(snap.Tasks);
        Assert.Equal(80.0, row.RatePerSecond, precision: 1);
    }

    [Fact]
    public void Alerts_LimitedTo5_AndSortedBySeverityThenTime()
    {
        var t = Now.AddSeconds(-30);
        var diagnostics = new[]
        {
            Diag("warn1", errors: 5, startedAt: t),
            Diag("warn2", lastHeartbeat: Now.AddSeconds(-200), startedAt: t),
            Diag("stop1", startedAt: t),
            Diag("stop2", startedAt: t),
            Diag("warn3", errors: 3, startedAt: t),
            Diag("warn4", errors: 7, startedAt: t)
        };
        var running = new[] { "warn1", "warn2", "warn3", "warn4" };

        var snap = HealthEvaluator.Evaluate(null, diagnostics, running, Now, HeartbeatTimeout);

        Assert.Equal(5, snap.Alerts.Count);
        Assert.Equal(AlertSeverity.Critical, snap.Alerts[0].Severity);
        Assert.Equal(AlertSeverity.Critical, snap.Alerts[1].Severity);
        // 剩 3 个 warning
        Assert.All(snap.Alerts.Skip(2), a => Assert.Equal(AlertSeverity.Warning, a.Severity));
    }
}
```

注意：测试用到 `HealthEvaluator.Evaluate(...)` 两种重载（带/不带 previousValueCounts）。实现要给两个签名，简化测试。

- [ ] **Step 3: 跑 Red**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: BUILD FAILED — HealthEvaluator 不存在。

- [ ] **Step 4: 实现 HealthEvaluator**

`wpf/src/Dc.App/Dashboard/HealthEvaluator.cs`:

```csharp
using Dc.Infrastructure.Orchestration;

namespace Dc.App.Dashboard;

public static class HealthEvaluator
{
    private const int ScoreStoppedPenalty = 15;
    private const int ScoreStalePenalty = 5;
    private const int ScoreErrorPenalty = 3;
    private const int MaxAlertsShown = 5;

    public static HealthSnapshot Evaluate(
        HealthSnapshot? previous,
        IReadOnlyList<TaskDiagnostics> diagnostics,
        IReadOnlyCollection<string> runningTaskIds,
        DateTimeOffset now,
        TimeSpan heartbeatTimeout)
        => Evaluate(previous, previousValueCounts: null, diagnostics, runningTaskIds, now, heartbeatTimeout);

    public static HealthSnapshot Evaluate(
        HealthSnapshot? previous,
        IReadOnlyDictionary<string, (long Count, DateTimeOffset At)>? previousValueCounts,
        IReadOnlyList<TaskDiagnostics> diagnostics,
        IReadOnlyCollection<string> runningTaskIds,
        DateTimeOffset now,
        TimeSpan heartbeatTimeout)
    {
        var running = new HashSet<string>(runningTaskIds, StringComparer.Ordinal);
        int score = 100;
        var alerts = new List<AlertItem>();
        var rows = new List<TaskRowSummary>(diagnostics.Count);
        double totalRate = 0;
        int activeTags = 0;
        long totalErrors = 0;
        DateTimeOffset? earliestStart = null;

        foreach (var d in diagnostics)
        {
            bool isRunning = running.Contains(d.TaskId);
            AlertSeverity? severity = null;
            string? message = null;

            if (!isRunning)
            {
                severity = AlertSeverity.Critical;
                message = "已停止";
                score -= ScoreStoppedPenalty;
            }
            else if (d.LastHeartbeatAt is { } hb && (now - hb) > heartbeatTimeout)
            {
                severity = AlertSeverity.Warning;
                var late = (now - hb).TotalSeconds;
                message = $"心跳延迟 {late:F0}s";
                score -= ScoreStalePenalty;
            }
            else if (d.PublishErrorCount > 0)
            {
                severity = AlertSeverity.Warning;
                message = $"发送错误 {d.PublishErrorCount}";
                score -= ScoreErrorPenalty;
            }

            if (severity is not null)
            {
                alerts.Add(new AlertItem(
                    severity.Value, d.TaskId, d.TaskId, message!, d.LastHeartbeatAt ?? d.StartedAt));
            }

            double rate = 0;
            if (isRunning && previousValueCounts is not null
                && previousValueCounts.TryGetValue(d.TaskId, out var prev))
            {
                var elapsed = (now - prev.At).TotalSeconds;
                if (elapsed > 0.001)
                {
                    rate = (d.ValueCount - prev.Count) / elapsed;
                    if (rate < 0) rate = 0;  // 重启后 ValueCount 重置
                }
            }
            totalRate += rate;

            activeTags += d.SubscribedTagCount;
            totalErrors += d.PublishErrorCount;
            if (earliestStart is null || d.StartedAt < earliestStart) earliestStart = d.StartedAt;

            rows.Add(new TaskRowSummary(d.TaskId, isRunning, severity, rate));
        }

        score = Math.Max(0, Math.Min(100, score));

        var sortedAlerts = alerts
            .OrderBy(a => a.Severity)               // Critical 在前（enum value 0）
            .ThenByDescending(a => a.OccurredAt)
            .Take(MaxAlertsShown)
            .ToList();

        TimeSpan? uptime = earliestStart is null ? null : now - earliestStart;

        return new HealthSnapshot(
            HealthScore: score,
            RunningTasks: running.Count,
            TotalTasks: diagnostics.Count,
            ActiveTags: activeTags,
            MessagesPerSecond: totalRate,
            ErrorsTotal: totalErrors,
            QueueBackedFrames: 0,                   // S2 占位
            Uptime: uptime,
            Alerts: sortedAlerts,
            Tasks: rows);
    }
}
```

- [ ] **Step 5: 跑 Green**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Dashboard/ wpf/tests/Dc.App.Tests/Dashboard/
git commit -m ":sparkles: S2.1: HealthEvaluator + HealthSnapshot 模型（7 unit tests）"
```

---

## Task 2: DashboardViewModel (TDD)

**Files:**
- Create: `wpf/src/Dc.App/ViewModels/Dashboard/DashboardViewModel.cs` (overwrite S1 占位)
- Create: `wpf/tests/Dc.App.Tests/ViewModels/Dashboard/DashboardViewModelTests.cs`

- [ ] **Step 1: 写测试**

`wpf/tests/Dc.App.Tests/ViewModels/Dashboard/DashboardViewModelTests.cs`:

```csharp
using Dc.App.Dashboard;
using Dc.App.ViewModels.Dashboard;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.Tests.ViewModels.Dashboard;

public class DashboardViewModelTests
{
    private sealed class FakeOrchestratorView : IDashboardOrchestratorView
    {
        public IReadOnlyList<TaskDiagnostics> Diagnostics { get; set; } = Array.Empty<TaskDiagnostics>();
        public IReadOnlyCollection<string> RunningTaskIds { get; set; } = Array.Empty<string>();

        IReadOnlyList<TaskDiagnostics> IDashboardOrchestratorView.GetDiagnostics() => Diagnostics;
        IReadOnlyCollection<string> IDashboardOrchestratorView.RunningTaskIds => RunningTaskIds;
    }

    private static readonly DateTimeOffset Now = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Refresh_NoTasks_Snapshot100()
    {
        var orch = new FakeOrchestratorView();
        var vm = new DashboardViewModel(orch, () => Now, TimeSpan.FromSeconds(120));

        vm.Refresh();

        Assert.Equal(100, vm.HealthScore);
        Assert.Empty(vm.Alerts);
        Assert.Equal("0", vm.RunningTasksDisplay);
        Assert.Equal("—", vm.UptimeDisplay);
    }

    [Fact]
    public void Refresh_RunningTask_PopulatesRow()
    {
        var diag = new TaskDiagnostics("t1", Now.AddMinutes(-5), Now.AddSeconds(-1),
            Now.AddSeconds(-1), 100, 0, 0, 50);
        var orch = new FakeOrchestratorView
        {
            Diagnostics = new[] { diag },
            RunningTaskIds = new[] { "t1" }
        };
        var vm = new DashboardViewModel(orch, () => Now, TimeSpan.FromSeconds(120));

        vm.Refresh();

        Assert.Equal(100, vm.HealthScore);
        Assert.Empty(vm.Alerts);
        Assert.Equal("1", vm.RunningTasksDisplay);
        Assert.Equal(50, vm.ActiveTags);
        Assert.Single(vm.Tasks);
    }

    [Fact]
    public void Refresh_StoppedTask_PopulatesAlert()
    {
        var diag = new TaskDiagnostics("t1", Now.AddMinutes(-5), Now.AddSeconds(-1),
            Now.AddSeconds(-1), 100, 0, 0, 50);
        var orch = new FakeOrchestratorView
        {
            Diagnostics = new[] { diag },
            RunningTaskIds = Array.Empty<string>()
        };
        var vm = new DashboardViewModel(orch, () => Now, TimeSpan.FromSeconds(120));

        vm.Refresh();

        Assert.Equal(85, vm.HealthScore);
        Assert.Single(vm.Alerts);
        Assert.Equal(AlertSeverity.Critical, vm.Alerts[0].Severity);
    }

    [Fact]
    public void Refresh_TwiceWithGrowingValueCount_ProducesRate()
    {
        var fakeNow = Now;
        DateTimeOffset Clock() => fakeNow;

        var diag1 = new TaskDiagnostics("t1", Now.AddMinutes(-5), Now.AddSeconds(-1),
            Now.AddSeconds(-1), 100, 0, 0, 50);

        var orch = new FakeOrchestratorView
        {
            Diagnostics = new[] { diag1 },
            RunningTaskIds = new[] { "t1" }
        };
        var vm = new DashboardViewModel(orch, Clock, TimeSpan.FromSeconds(120));

        vm.Refresh();   // 第一次刷新，建立 baseline
        Assert.Equal(0.0, vm.MessagesPerSecond, precision: 1);

        fakeNow = Now.AddSeconds(1);
        orch.Diagnostics = new[]
        {
            new TaskDiagnostics("t1", diag1.StartedAt, fakeNow,
                fakeNow, 180, 0, 0, 50)
        };

        vm.Refresh();   // 第二次刷新，速率 = (180-100)/1 = 80
        Assert.Equal(80.0, vm.MessagesPerSecond, precision: 1);
    }
}
```

- [ ] **Step 2: 实现 IDashboardOrchestratorView + DashboardViewModel**

`wpf/src/Dc.App/ViewModels/Dashboard/IDashboardOrchestratorView.cs`:

```csharp
using Dc.Infrastructure.Orchestration;

namespace Dc.App.ViewModels.Dashboard;

/// 用于解耦 DashboardViewModel 与具体 TaskOrchestrator —— 方便单测。
public interface IDashboardOrchestratorView
{
    IReadOnlyList<TaskDiagnostics> GetDiagnostics();
    IReadOnlyCollection<string> RunningTaskIds { get; }
}
```

替换 `wpf/src/Dc.App/ViewModels/Dashboard/DashboardViewModel.cs`（S1 占位）整体为：

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dc.App.Dashboard;

namespace Dc.App.ViewModels.Dashboard;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IDashboardOrchestratorView _orch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _heartbeatTimeout;

    private HealthSnapshot? _previousSnapshot;
    private Dictionary<string, (long Count, DateTimeOffset At)> _previousValueCounts = new();

    [ObservableProperty] private int _healthScore = 100;
    [ObservableProperty] private string _runningTasksDisplay = "0";
    [ObservableProperty] private string _totalTasksDisplay = "0";
    [ObservableProperty] private int _activeTags;
    [ObservableProperty] private double _messagesPerSecond;
    [ObservableProperty] private long _errorsTotal;
    [ObservableProperty] private long _queueBackedFrames;
    [ObservableProperty] private string _uptimeDisplay = "—";
    [ObservableProperty] private string _messagesPerSecondDisplay = "0";
    [ObservableProperty] private string _errorsTotalDisplay = "0";

    public ObservableCollection<AlertItem> Alerts { get; } = new();
    public ObservableCollection<TaskRowSummary> Tasks { get; } = new();

    public DashboardViewModel(
        IDashboardOrchestratorView orchestratorView,
        Func<DateTimeOffset> clock,
        TimeSpan heartbeatTimeout)
    {
        _orch = orchestratorView;
        _clock = clock;
        _heartbeatTimeout = heartbeatTimeout;
    }

    public void Refresh()
    {
        var now = _clock();
        var diagnostics = _orch.GetDiagnostics();
        var running = _orch.RunningTaskIds;

        var snap = HealthEvaluator.Evaluate(
            _previousSnapshot,
            _previousValueCounts.Count == 0 ? null : _previousValueCounts,
            diagnostics,
            running,
            now,
            _heartbeatTimeout);

        HealthScore = snap.HealthScore;
        RunningTasksDisplay = snap.RunningTasks.ToString();
        TotalTasksDisplay = snap.TotalTasks.ToString();
        ActiveTags = snap.ActiveTags;
        MessagesPerSecond = snap.MessagesPerSecond;
        MessagesPerSecondDisplay = FormatRate(snap.MessagesPerSecond);
        ErrorsTotal = snap.ErrorsTotal;
        ErrorsTotalDisplay = snap.ErrorsTotal.ToString();
        QueueBackedFrames = snap.QueueBackedFrames;
        UptimeDisplay = FormatUptime(snap.Uptime);

        Alerts.Clear();
        foreach (var a in snap.Alerts) Alerts.Add(a);

        Tasks.Clear();
        foreach (var t in snap.Tasks) Tasks.Add(t);

        // 下一轮基线
        _previousSnapshot = snap;
        _previousValueCounts = diagnostics.ToDictionary(
            d => d.TaskId,
            d => (d.ValueCount, now),
            StringComparer.Ordinal);
    }

    private static string FormatRate(double rate)
    {
        if (rate >= 1000) return $"{rate / 1000:F1}k";
        return rate.ToString("F0");
    }

    private static string FormatUptime(TimeSpan? span)
    {
        if (span is null) return "—";
        var s = span.Value;
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d {s.Hours}h";
        if (s.TotalHours >= 1) return $"{s.Hours}h {s.Minutes}m";
        return $"{s.Minutes}m {s.Seconds}s";
    }
}
```

- [ ] **Step 3: 跑测试**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/Dashboard/ wpf/tests/Dc.App.Tests/ViewModels/Dashboard/
git commit -m ":sparkles: S2.2: DashboardViewModel 拉取 + 速率窗口（4 unit tests）"
```

---

## Task 3: DashboardView.xaml C 风格暗色实现

**Files:**
- Modify: `wpf/src/Dc.App/Views/Dashboard/DashboardView.xaml`
- Modify: `wpf/src/Dc.App/Views/Dashboard/DashboardView.xaml.cs`（保持原样，新加 fallback converter）
- Create: `wpf/src/Dc.App/Views/Dashboard/Converters.cs`

- [ ] **Step 1: 共享 converters**

`wpf/src/Dc.App/Views/Dashboard/Converters.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Dc.App.Dashboard;

namespace Dc.App.Views.Dashboard;

public sealed class HealthScoreToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Good = new(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush Bad  = new(Color.FromRgb(0xF8, 0x71, 0x71));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int score) return Good;
        return score >= 90 ? Good : score >= 70 ? Warn : Bad;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class AlertSeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Critical = new(Color.FromArgb(0x40, 0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush Warning  = new(Color.FromArgb(0x40, 0xD9, 0x77, 0x06));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AlertSeverity sev && sev == AlertSeverity.Critical ? Critical : Warning;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class AlertSeverityToAccentConverter : IValueConverter
{
    private static readonly SolidColorBrush Critical = new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush Warning  = new(Color.FromRgb(0xD9, 0x77, 0x06));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AlertSeverity sev && sev == AlertSeverity.Critical ? Critical : Warning;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class AlertSeverityToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AlertSeverity sev && sev == AlertSeverity.Critical ? "🛑" : "⚠";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TaskRowSeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Good = new(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush Bad  = new(Color.FromRgb(0xF8, 0x71, 0x71));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            AlertSeverity.Critical => Bad,
            AlertSeverity.Warning  => Warn,
            _ => Good
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 2: DashboardView.xaml 替换占位**

替换 `wpf/src/Dc.App/Views/Dashboard/DashboardView.xaml` 整体为：

```xml
<UserControl x:Class="Dc.App.Views.Dashboard.DashboardView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:conv="clr-namespace:Dc.App.Views.Dashboard"
             xmlns:dashboard="clr-namespace:Dc.App.Dashboard"
             Background="#0F1419">
    <UserControl.Resources>
        <conv:HealthScoreToColorConverter x:Key="HealthColor" />
        <conv:AlertSeverityToBrushConverter x:Key="AlertBg" />
        <conv:AlertSeverityToAccentConverter x:Key="AlertAccent" />
        <conv:AlertSeverityToIconConverter x:Key="AlertIcon" />
        <conv:TaskRowSeverityToBrushConverter x:Key="TaskRowColor" />

        <SolidColorBrush x:Key="DcCardBg" Color="#0A1218" Opacity="0.5" />
        <SolidColorBrush x:Key="DcCardBorder" Color="#1A2530" />
        <SolidColorBrush x:Key="DcText" Color="#E8EAED" />
        <SolidColorBrush x:Key="DcTextMuted" Color="#80FFFFFF" />
        <SolidColorBrush x:Key="DcAccent" Color="#0067C0" />

        <Style TargetType="Border" x:Key="DcCard">
            <Setter Property="Background" Value="{StaticResource DcCardBg}" />
            <Setter Property="BorderBrush" Value="{StaticResource DcCardBorder}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="12" />
            <Setter Property="Padding" Value="14,12" />
        </Style>
        <Style TargetType="TextBlock" x:Key="DcCardHeader">
            <Setter Property="Foreground" Value="{StaticResource DcText}" />
            <Setter Property="FontSize" Value="13" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Margin" Value="0,0,0,8" />
        </Style>
        <Style TargetType="TextBlock" x:Key="DcRingValue">
            <Setter Property="Foreground" Value="{StaticResource DcText}" />
            <Setter Property="FontSize" Value="20" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="HorizontalAlignment" Value="Center" />
        </Style>
        <Style TargetType="TextBlock" x:Key="DcRingLabel">
            <Setter Property="Foreground" Value="{StaticResource DcTextMuted}" />
            <Setter Property="FontSize" Value="10" />
            <Setter Property="HorizontalAlignment" Value="Center" />
            <Setter Property="Margin" Value="0,2,0,0" />
        </Style>
    </UserControl.Resources>

    <Grid Margin="20,18">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0" Margin="0,0,0,14">
            <TextBlock Text="系统状态板"
                       Foreground="{StaticResource DcText}"
                       FontSize="22" FontWeight="SemiBold" />
            <TextBlock Foreground="{StaticResource DcTextMuted}"
                       FontSize="12" Margin="0,2,0,0">
                <Run Text="{Binding RunningTasksDisplay}" /><Run Text=" 个任务运行 · " /><Run Text="{Binding TotalTasksDisplay}" /><Run Text=" 总计 · 每秒刷新" />
            </TextBlock>
        </StackPanel>

        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="320" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="320" />
            </Grid.ColumnDefinitions>

            <!-- 左：告警 -->
            <Border Grid.Column="0" Style="{StaticResource DcCard}" Margin="0,0,7,0">
                <DockPanel>
                    <TextBlock DockPanel.Dock="Top" Style="{StaticResource DcCardHeader}">
                        当前告警
                        <Run Foreground="{StaticResource DcTextMuted}" FontWeight="Normal" Text="{Binding Alerts.Count, StringFormat=' · {0}'}" />
                    </TextBlock>
                    <TextBlock DockPanel.Dock="Top"
                               Visibility="{Binding Alerts.Count, Converter={x:Static conv:ZeroToVisibleConverter.Default}}"
                               Foreground="{StaticResource DcTextMuted}"
                               FontSize="12">无告警 · 全部正常</TextBlock>
                    <ItemsControl ItemsSource="{Binding Alerts}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate DataType="{x:Type dashboard:AlertItem}">
                                <Border Background="{Binding Severity, Converter={StaticResource AlertBg}}"
                                        BorderBrush="{Binding Severity, Converter={StaticResource AlertAccent}}"
                                        BorderThickness="3,0,0,0"
                                        CornerRadius="6"
                                        Padding="12,10"
                                        Margin="0,0,0,8">
                                    <DockPanel>
                                        <TextBlock DockPanel.Dock="Left"
                                                   Text="{Binding Severity, Converter={StaticResource AlertIcon}}"
                                                   FontSize="16" Margin="0,2,12,0" VerticalAlignment="Top" />
                                        <StackPanel>
                                            <TextBlock Foreground="{StaticResource DcText}" FontSize="13" FontWeight="SemiBold"
                                                       Text="{Binding TaskId}" />
                                            <TextBlock Foreground="{StaticResource DcText}" FontSize="12"
                                                       Text="{Binding Message}" />
                                            <TextBlock Foreground="{StaticResource DcTextMuted}" FontSize="11"
                                                       Text="{Binding OccurredAt, StringFormat='HH:mm:ss'}" />
                                        </StackPanel>
                                    </DockPanel>
                                </Border>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </DockPanel>
            </Border>

            <!-- 中：健康度大数 + 6 环 -->
            <Grid Grid.Column="1" Margin="7,0">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                </Grid.RowDefinitions>

                <Border Grid.Row="0" Margin="0,0,0,14">
                    <Border.Background>
                        <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                            <GradientStop Color="#280067C0" Offset="0" />
                            <GradientStop Color="#208C52FF" Offset="1" />
                        </LinearGradientBrush>
                    </Border.Background>
                    <Border.BorderBrush>
                        <SolidColorBrush Color="#0067C0" Opacity="0.45" />
                    </Border.BorderBrush>
                    <Border.Style>
                        <Style TargetType="Border">
                            <Setter Property="BorderThickness" Value="1" />
                            <Setter Property="CornerRadius" Value="12" />
                            <Setter Property="Padding" Value="24,22" />
                        </Style>
                    </Border.Style>
                    <StackPanel HorizontalAlignment="Center">
                        <TextBlock Text="{Binding HealthScore}"
                                   FontSize="72" FontWeight="SemiBold" LineHeight="80"
                                   Foreground="{Binding HealthScore, Converter={StaticResource HealthColor}}"
                                   HorizontalAlignment="Center" />
                        <TextBlock Text="系统健康度" Foreground="{StaticResource DcTextMuted}"
                                   FontSize="13" Margin="0,6,0,0"
                                   HorizontalAlignment="Center" />
                    </StackPanel>
                </Border>

                <UniformGrid Grid.Row="1" Columns="3" Rows="2">
                    <Border Style="{StaticResource DcCard}" Margin="0,0,6,6">
                        <StackPanel>
                            <TextBlock Style="{StaticResource DcRingValue}" Text="{Binding RunningTasksDisplay}" />
                            <TextBlock Style="{StaticResource DcRingLabel}" Text="运行中任务" />
                        </StackPanel>
                    </Border>
                    <Border Style="{StaticResource DcCard}" Margin="3,0,3,6">
                        <StackPanel>
                            <TextBlock Style="{StaticResource DcRingValue}" Text="{Binding ActiveTags}" />
                            <TextBlock Style="{StaticResource DcRingLabel}" Text="活跃 Tag" />
                        </StackPanel>
                    </Border>
                    <Border Style="{StaticResource DcCard}" Margin="6,0,0,6">
                        <StackPanel>
                            <TextBlock Style="{StaticResource DcRingValue}" Text="{Binding MessagesPerSecondDisplay, StringFormat={}{0}/s}" />
                            <TextBlock Style="{StaticResource DcRingLabel}" Text="消息速率" />
                        </StackPanel>
                    </Border>
                    <Border Style="{StaticResource DcCard}" Margin="0,6,6,0">
                        <StackPanel>
                            <TextBlock Style="{StaticResource DcRingValue}" Text="{Binding ErrorsTotalDisplay}" />
                            <TextBlock Style="{StaticResource DcRingLabel}" Text="累计错误" />
                        </StackPanel>
                    </Border>
                    <Border Style="{StaticResource DcCard}" Margin="3,6,3,0">
                        <StackPanel>
                            <TextBlock Style="{StaticResource DcRingValue}" Text="—" />
                            <TextBlock Style="{StaticResource DcRingLabel}" Text="队列积压" />
                        </StackPanel>
                    </Border>
                    <Border Style="{StaticResource DcCard}" Margin="6,6,0,0">
                        <StackPanel>
                            <TextBlock Style="{StaticResource DcRingValue}" Text="{Binding UptimeDisplay}" />
                            <TextBlock Style="{StaticResource DcRingLabel}" Text="运行时长" />
                        </StackPanel>
                    </Border>
                </UniformGrid>
            </Grid>

            <!-- 右：任务列表 -->
            <Border Grid.Column="2" Style="{StaticResource DcCard}" Margin="7,0,0,0">
                <DockPanel>
                    <TextBlock DockPanel.Dock="Top" Style="{StaticResource DcCardHeader}" Text="任务状态" />
                    <ItemsControl ItemsSource="{Binding Tasks}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate DataType="{x:Type dashboard:TaskRowSummary}">
                                <Grid Margin="0,3,0,3">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="10" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="Auto" />
                                    </Grid.ColumnDefinitions>
                                    <Ellipse Grid.Column="0" Width="8" Height="8" Margin="0,4,8,0"
                                             Fill="{Binding Severity, Converter={StaticResource TaskRowColor}}" />
                                    <TextBlock Grid.Column="1" Foreground="{StaticResource DcText}" FontSize="12"
                                               Text="{Binding TaskId}" />
                                    <TextBlock Grid.Column="2" Foreground="{Binding Severity, Converter={StaticResource TaskRowColor}}"
                                               FontSize="11"
                                               Text="{Binding RatePerSecond, StringFormat={}{0:F0}/s}" />
                                </Grid>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </DockPanel>
            </Border>
        </Grid>
    </Grid>
</UserControl>
```

`Visibility="{Binding Alerts.Count, Converter={x:Static conv:ZeroToVisibleConverter.Default}}"` 需要再加一个 converter，扩展 `Converters.cs`：

```csharp
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public static readonly ZeroToVisibleConverter Default = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value switch
        {
            int i => i,
            long l => (int)l,
            _ => -1
        };
        return n == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

把 `ZeroToVisibleConverter` 追加到 `Converters.cs` 末尾。

- [ ] **Step 3: 构建验证**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -8
```

Expected: Build succeeded. 0 Error(s).

- [ ] **Step 4: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Views/Dashboard/
git commit -m ":sparkles: S2.3: DashboardView C 风格暗色（健康度大数+6环+告警+任务）"
```

---

## Task 4: DI 注册 + DispatcherTimer wireup

**Files:**
- Modify: `wpf/src/Dc.App/Composition/ServiceRegistration.cs`
- Modify: `wpf/src/Dc.App/ViewModels/Dashboard/DashboardViewModel.cs`（加 IDisposable + Start/Stop）

- [ ] **Step 1: 加 Start/Stop + DispatcherTimer 到 DashboardViewModel**

修改 `wpf/src/Dc.App/ViewModels/Dashboard/DashboardViewModel.cs`，在 class 末尾加：

```csharp
    public void Start(System.Windows.Threading.Dispatcher dispatcher)
    {
        if (_timer is not null) return;
        _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Refresh();
        Refresh();   // 立即刷一次
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private System.Windows.Threading.DispatcherTimer? _timer;
```

(把现有 `Refresh()` 保留)

- [ ] **Step 2: ServiceRegistration 注册 IDashboardOrchestratorView + DashboardViewModel ctor**

打开 `wpf/src/Dc.App/Composition/ServiceRegistration.cs`。找到 S1 Task 12 加的这行：
```csharp
        services.AddSingleton<Dc.App.ViewModels.Dashboard.DashboardViewModel>();
```

替换为：
```csharp
        // DashboardOrchestratorView 适配 TaskOrchestrator 到 IDashboardOrchestratorView
        services.AddSingleton<Dc.App.ViewModels.Dashboard.IDashboardOrchestratorView>(sp =>
            new Dc.App.ViewModels.Dashboard.TaskOrchestratorView(
                sp.GetRequiredService<TaskOrchestrator>()));

        services.AddSingleton<Dc.App.ViewModels.Dashboard.DashboardViewModel>(sp =>
        {
            var orchView = sp.GetRequiredService<Dc.App.ViewModels.Dashboard.IDashboardOrchestratorView>();
            var opts = sp.GetRequiredService<OrchestratorOptions>();
            return new Dc.App.ViewModels.Dashboard.DashboardViewModel(
                orchView,
                () => DateTimeOffset.UtcNow,
                opts.HeartbeatTimeout);
        });
```

新建 `wpf/src/Dc.App/ViewModels/Dashboard/TaskOrchestratorView.cs`:

```csharp
using Dc.Infrastructure.Orchestration;

namespace Dc.App.ViewModels.Dashboard;

public sealed class TaskOrchestratorView : IDashboardOrchestratorView
{
    private readonly TaskOrchestrator _orch;
    public TaskOrchestratorView(TaskOrchestrator orch) => _orch = orch;

    public IReadOnlyList<TaskDiagnostics> GetDiagnostics() => _orch.GetDiagnostics();
    public IReadOnlyCollection<string> RunningTaskIds => _orch.RunningTaskIds;
}
```

- [ ] **Step 3: ShellWindow OnLoaded / OnClosed 启停 DashboardViewModel timer**

修改 `wpf/src/Dc.App/Views/Shell/ShellWindow.xaml.cs`，在 ctor 末尾加：

```csharp
        Loaded += OnLoaded;
        Closed += OnClosed;
```

加新方法：

```csharp
    private Dc.App.ViewModels.Dashboard.DashboardViewModel? _dashboardVm;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 在 UI 线程上拿 DashboardViewModel，启动 1s 刷新
        if (_vm.CurrentContent is Dc.App.ViewModels.Dashboard.DashboardViewModel dashVm)
        {
            dashVm.Start(Dispatcher);
            _dashboardVm = dashVm;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _dashboardVm?.Stop();
    }
```

注意：这只在初始 CurrentContent = Dashboard 时启动。如果未来 Dashboard 不是默认起点，需要 hook ShellViewModel.PropertyChanged on CurrentContent 来 Start/Stop。当前 S1 spec 仪表盘是默认入口，这个保守实现足够。

- [ ] **Step 4: 构建验证**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Composition/ServiceRegistration.cs \
        wpf/src/Dc.App/ViewModels/Dashboard/TaskOrchestratorView.cs \
        wpf/src/Dc.App/ViewModels/Dashboard/DashboardViewModel.cs \
        wpf/src/Dc.App/Views/Shell/ShellWindow.xaml.cs
git commit -m ":sparkles: S2.4: DI 注册 + DispatcherTimer 1s 刷新"
```

---

## Task 5: 全量回归 + push

- [ ] **Step 1: 全套测试**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.Infrastructure.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo 2>&1 | tail -5
dotnet test tests/Dc.Integration.Tests   -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo 2>&1 | tail -5
dotnet build src/Dc.App tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -3
```

Expected:
- Infra: 48 passed
- Integration: 10 passed
- Dc.App.Tests build: 0 errors (Linux 跑不了测试 runtime)

- [ ] **Step 2: Push**

```bash
cd /home/adamyu/workspace/dc
git push origin wpf-opc-collector
```

- [ ] **Step 3: PR #5 评论 S2 完成**

通过 Gitea API：

```bash
CREDS=$(grep "git.adamyu.top" ~/.git-credentials | sed 's|https://||;s|@.*||')
cat > /tmp/s2_comment.json <<'EOF'
{"body":"### S2 · Dashboard 真实实现 完成（Linux 验证）\n\n- HealthEvaluator + HealthSnapshot 模型 + 7 unit tests\n- DashboardViewModel 拉 TaskOrchestrator.GetDiagnostics()，1s 刷新，速率窗口工作\n- C 风格暗色三栏布局：告警 / 健康度大数+6环 / 任务列表\n- 主题切换不影响 Dashboard（永远暗色，SCADA 心智）\n- 队列积压暂占位 \"—\"（per-task IOutboundQueue 统计需要新 API，S2b 再补）\n- 累计错误率简化为累计值（滑动 1h 窗口 = TODO）\n\nWindows 端 walkthrough：\n1. 启动进入 Dashboard 看到健康度 100\n2. 启动一个 UA 任务 → 健康度仍 100，运行中任务=1\n3. 故意停一个任务 → 健康度 -15，左栏出现 Critical 告警\n4. 暗色覆盖 — 切到亮色主题 Dashboard 仍是暗色"}
EOF
curl -sk -u "$CREDS" -H "Content-Type: application/json" \
  -X POST "https://git.adamyu.top:20443/api/v1/repos/adamyu/dc/issues/5/comments" \
  -d @/tmp/s2_comment.json -o /tmp/c.json -w "HTTP %{http_code}\n"
jq -r '.html_url // .message' /tmp/c.json
rm -f /tmp/s2_comment.json /tmp/c.json
```

Expected: HTTP 201。

---

## 验收

- Infra 48 + Integration 10 + Dc.App.Tests 25+ (S1 14 + S2 11) 全绿 build
- 提交链：5 个 commit (S2.1~S2.4 + S2.5 自身不 commit)
- PR #5 评论已加 S2 完成段
- Windows 真机验证留给用户
