# UI 重设计 S3a — 采集任务工作台（master + 概览 + Tag） 实施计划

**Goal:** 把"采集任务"从现有 3 栏并排（任务列表 | 分组 | Tag）改成 master-detail + tabbed detail：左 master 任务列表（搜索/状态筛选/汇总胶囊/新建导入），右 detail 选中任务的 tab（概览 + Tag，剩 4 tab 留 S3b/c）。

**Architecture:**
新增 `TaskWorkspaceViewModel`（master 协调器）+ `TaskMasterRow`（列表行）+ `WorkspaceOverviewViewModel`（概览 tab，含客户端采样 sparkline）。Tag tab 直接复用现有 `TagsViewModel`（`IsEmbedded=true` + taskId 预过滤）。汇总胶囊的告警数复用 S2 的 `HealthEvaluator`。Sparkline 客户端采样（不动 orchestrator）。完成后 shell "workspace" 路由从旧 `TasksViewModel` 切到 `TaskWorkspaceViewModel`，删旧 `TasksView`/`TasksViewModel`。

**Tech Stack:** .NET 8 + WPF + Wpf.Ui + CommunityToolkit.Mvvm + EF Core + xUnit + Moq

**Spec:** `wpf/docs/specs/2026-05-19-ui-redesign-fluent-design.md` (Stage S3, §2.2)
**Mockup:** `/tmp/dc-mockups/c2-workspace.html`

---

## 已锁定决策

| 项 | 决策 |
|---|---|
| 范围 | S3a = master + 概览 + Tag（3 tab）；剩 分组/实时/诊断/配置 → S3b/c |
| 概览深度 | KPI 数字 + 心跳/速率 sparkline |
| sparkline 数据 | 客户端采样（VM 内 1s 采 ValueCount delta，capped 60 点），不改 orchestrator |
| master 控件 | 搜索框 + 状态筛选(全部/运行/停止) + 新建/导入 + 汇总胶囊(运行N/停N/告警N) |
| 告警数来源 | 复用 `HealthEvaluator`（S2）算 alerts.Count |
| Tag tab | 复用 `TagsViewModel`（IsEmbedded）+ 预设 taskId 范围 |
| 旧 TasksView | S3a 完成后删除（routing 切到 workspace VM） |

---

## 前置说明

dotnet 在 `/home/adamyu/.dotnet/dotnet`，PATH 先 export。Linux 上 Dc.App.Tests 跑不了 net8.0-windows runtime，build 验证为准。

构建命令：
```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

测试基线：infra 48 + integration 10 + Dc.App.Tests 25（S1 14 + S2 11）。S3a 完成时 Dc.App.Tests +约 10。

关键既有类型：
- `TaskDiagnostics(string TaskId, DateTimeOffset StartedAt, DateTimeOffset? LastValueAt, DateTimeOffset? LastHeartbeatAt, long ValueCount, long PublishErrorCount, int RestartCount, int SubscribedTagCount)`
- `IDashboardOrchestratorView`（S2）— `GetDiagnostics()` + `RunningTaskIds`
- `HealthEvaluator.Evaluate(...)`（S2）→ `HealthSnapshot`（含 `Alerts`）
- `TagsViewModel`（现有）— 有 `IsEmbedded` bool + `GroupFilter` + 按 taskId 工作
- `TaskOrchestrator` — `StartAsync(TaskStartRequest)` / `StopAsync(taskId)` / `RunningTaskIds`
- `CollectorTask` entity（已确认字段）— `Id`(EntityBase) / `Server` / `Node` / `Clsid?` / `Type`(byte，**协议字段，不叫 Protocol**：1=DA 2=UA 3=AE) / `Interval` / `Deviation` / `TcpAddress` / `CreatedAt`(EntityBase) / `UpdatedAt`(EntityBase)

---

## Task 1: TaskMasterRow + TaskWorkspaceViewModel (TDD)

**Files:**
- Create: `wpf/src/Dc.App/ViewModels/Workspace/TaskMasterRow.cs`
- Create: `wpf/src/Dc.App/ViewModels/Workspace/WorkspaceStatusFilter.cs`
- Create: `wpf/src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs`
- Create: `wpf/tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs`

> Master 协调器。为可测，把"任务数据来源"抽象成 `IWorkspaceTaskSource`（返回 `CollectorTask` 列表），并复用 S2 的 `IDashboardOrchestratorView` 拿运行态 + 诊断。

- [ ] **Step 1: 状态筛选枚举 + master row**

`wpf/src/Dc.App/ViewModels/Workspace/WorkspaceStatusFilter.cs`:

```csharp
namespace Dc.App.ViewModels.Workspace;

public enum WorkspaceStatusFilter { All, Running, Stopped }
```

`wpf/src/Dc.App/ViewModels/Workspace/TaskMasterRow.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace Dc.App.ViewModels.Workspace;

public sealed partial class TaskMasterRow : ObservableObject
{
    public string TaskId { get; }
    public string Name { get; }          // 取 Server，空则 TaskId
    public string Protocol { get; }      // "UA"/"DA"/"AE"

    [ObservableProperty] private int _tagCount;
    [ObservableProperty] private double _ratePerSecond;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasAlert;

    public TaskMasterRow(string taskId, string name, string protocol)
    {
        TaskId = taskId;
        Name = name;
        Protocol = protocol;
    }
}
```

- [ ] **Step 2: 任务数据源抽象**

`wpf/src/Dc.App/ViewModels/Workspace/IWorkspaceTaskSource.cs`:

```csharp
using Dc.Domain.Entities;

namespace Dc.App.ViewModels.Workspace;

public interface IWorkspaceTaskSource
{
    Task<IReadOnlyList<CollectorTask>> LoadTasksAsync();
}
```

- [ ] **Step 3: 写测试（Red）**

`wpf/tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs`:

```csharp
using Dc.App.ViewModels.Workspace;
using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;
using Dc.App.ViewModels.Dashboard;

namespace Dc.App.Tests.ViewModels.Workspace;

public class TaskWorkspaceViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTaskSource : IWorkspaceTaskSource
    {
        public List<CollectorTask> Tasks { get; set; } = new();
        public Task<IReadOnlyList<CollectorTask>> LoadTasksAsync()
            => Task.FromResult<IReadOnlyList<CollectorTask>>(Tasks);
    }

    private sealed class FakeOrchView : IDashboardOrchestratorView
    {
        public IReadOnlyList<TaskDiagnostics> Diags { get; set; } = Array.Empty<TaskDiagnostics>();
        public IReadOnlyCollection<string> Running { get; set; } = Array.Empty<string>();
        public IReadOnlyList<TaskDiagnostics> GetDiagnostics() => Diags;
        public IReadOnlyCollection<string> RunningTaskIds => Running;
    }

    private static CollectorTask Task1(string id, string server = "炉温", byte type = 2)
        => new() { Id = id, Server = server, Node = "opc.tcp://x", Type = type,
                   TcpAddress = "10.0.0.1:9000", Interval = 1000, Deviation = 1, CreatedAt = Now.UtcDateTime };

    private static (FakeTaskSource src, FakeOrchView orch, TaskWorkspaceViewModel vm) Build()
    {
        var src = new FakeTaskSource();
        var orch = new FakeOrchView();
        var vm = new TaskWorkspaceViewModel(src, orch, () => Now, TimeSpan.FromSeconds(120));
        return (src, orch, vm);
    }

    [Fact]
    public async Task Load_PopulatesRows()
    {
        var (src, _, vm) = Build();
        src.Tasks = new() { Task1("t1"), Task1("t2", "压力") };

        await vm.LoadAsync();

        Assert.Equal(2, vm.AllTasks.Count);
        Assert.Equal("炉温", vm.AllTasks[0].Name);
    }

    [Fact]
    public async Task Load_MarksRunningRows()
    {
        var (src, orch, vm) = Build();
        src.Tasks = new() { Task1("t1"), Task1("t2") };
        orch.Running = new[] { "t1" };

        await vm.LoadAsync();

        Assert.True(vm.AllTasks.Single(r => r.TaskId == "t1").IsRunning);
        Assert.False(vm.AllTasks.Single(r => r.TaskId == "t2").IsRunning);
    }

    [Fact]
    public async Task Summary_CountsRunningStoppedAlert()
    {
        var (src, orch, vm) = Build();
        src.Tasks = new() { Task1("t1"), Task1("t2"), Task1("t3") };
        orch.Running = new[] { "t1", "t2" };
        // t3 在 DB 但不 running → stopped。诊断里 t3 出现 → HealthEvaluator 认为 critical 告警
        orch.Diags = new[]
        {
            new TaskDiagnostics("t1", Now.AddMinutes(-5), Now, Now, 10, 0, 0, 5),
            new TaskDiagnostics("t2", Now.AddMinutes(-5), Now, Now, 10, 0, 0, 5),
            new TaskDiagnostics("t3", Now.AddMinutes(-5), Now, Now, 10, 0, 0, 5)
        };

        await vm.LoadAsync();

        Assert.Equal(2, vm.RunningCount);
        Assert.Equal(1, vm.StoppedCount);
        Assert.Equal(1, vm.AlertCount);   // t3 停止 = 1 critical
    }

    [Fact]
    public async Task SearchText_FiltersByNameOrServer()
    {
        var (src, _, vm) = Build();
        src.Tasks = new() { Task1("t1", "炉温监测"), Task1("t2", "压力站") };
        await vm.LoadAsync();

        vm.SearchText = "压力";

        var visible = vm.FilteredTasks.Cast<TaskMasterRow>().ToList();
        Assert.Single(visible);
        Assert.Equal("t2", visible[0].TaskId);
    }

    [Fact]
    public async Task StatusFilter_Running_ShowsOnlyRunning()
    {
        var (src, orch, vm) = Build();
        src.Tasks = new() { Task1("t1"), Task1("t2") };
        orch.Running = new[] { "t1" };
        await vm.LoadAsync();

        vm.StatusFilter = WorkspaceStatusFilter.Running;

        var visible = vm.FilteredTasks.Cast<TaskMasterRow>().ToList();
        Assert.Single(visible);
        Assert.Equal("t1", visible[0].TaskId);
    }

    [Fact]
    public async Task SelectingTask_SetsSelectedAndExposesTaskId()
    {
        var (src, _, vm) = Build();
        src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();

        vm.SelectedTask = vm.AllTasks[0];

        Assert.NotNull(vm.SelectedTask);
        Assert.Equal("t1", vm.SelectedTask!.TaskId);
    }
}
```

- [ ] **Step 4: Red 验证**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: FAILED — TaskWorkspaceViewModel 不存在。

- [ ] **Step 5: 实现 TaskWorkspaceViewModel**

`wpf/src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Dc.App.Dashboard;
using Dc.App.ViewModels.Dashboard;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.ViewModels.Workspace;

public sealed partial class TaskWorkspaceViewModel : ObservableObject
{
    private readonly IWorkspaceTaskSource _source;
    private readonly IDashboardOrchestratorView _orch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _heartbeatTimeout;

    public ObservableCollection<TaskMasterRow> AllTasks { get; } = new();
    public ICollectionView FilteredTasks { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private WorkspaceStatusFilter _statusFilter = WorkspaceStatusFilter.All;
    [ObservableProperty] private TaskMasterRow? _selectedTask;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _stoppedCount;
    [ObservableProperty] private int _alertCount;

    public TaskWorkspaceViewModel(
        IWorkspaceTaskSource source,
        IDashboardOrchestratorView orchestratorView,
        Func<DateTimeOffset> clock,
        TimeSpan heartbeatTimeout)
    {
        _source = source;
        _orch = orchestratorView;
        _clock = clock;
        _heartbeatTimeout = heartbeatTimeout;

        FilteredTasks = CollectionViewSource.GetDefaultView(AllTasks);
        FilteredTasks.Filter = FilterRow;
    }

    partial void OnSearchTextChanged(string value) => FilteredTasks.Refresh();
    partial void OnStatusFilterChanged(WorkspaceStatusFilter value) => FilteredTasks.Refresh();

    private bool FilterRow(object obj)
    {
        if (obj is not TaskMasterRow row) return false;

        if (StatusFilter == WorkspaceStatusFilter.Running && !row.IsRunning) return false;
        if (StatusFilter == WorkspaceStatusFilter.Stopped && row.IsRunning) return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            if (row.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && row.TaskId.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }
        return true;
    }

    public async Task LoadAsync()
    {
        var tasks = await _source.LoadTasksAsync();
        var running = new HashSet<string>(_orch.RunningTaskIds, StringComparer.Ordinal);
        var diagnostics = _orch.GetDiagnostics();
        var diagByTask = diagnostics.ToDictionary(d => d.TaskId, StringComparer.Ordinal);

        AllTasks.Clear();
        foreach (var t in tasks)
        {
            var name = string.IsNullOrWhiteSpace(t.Server) ? t.Id : t.Server;
            var row = new TaskMasterRow(t.Id, name, ProtocolLabel(t.Type))
            {
                IsRunning = running.Contains(t.Id),
                TagCount = diagByTask.TryGetValue(t.Id, out var d) ? d.SubscribedTagCount : 0
            };
            AllTasks.Add(row);
        }

        RunningCount = AllTasks.Count(r => r.IsRunning);
        StoppedCount = AllTasks.Count - RunningCount;

        var snap = HealthEvaluator.Evaluate(
            previous: null,
            diagnostics: diagnostics,
            runningTaskIds: _orch.RunningTaskIds,
            now: _clock(),
            heartbeatTimeout: _heartbeatTimeout);
        AlertCount = snap.Alerts.Count;

        // 标记有告警的行
        var alertTaskIds = snap.Alerts.Select(a => a.TaskId).ToHashSet(StringComparer.Ordinal);
        foreach (var row in AllTasks) row.HasAlert = alertTaskIds.Contains(row.TaskId);

        FilteredTasks.Refresh();
    }

    private static string ProtocolLabel(byte type) => type switch
    {
        1 => "DA",
        2 => "UA",
        3 => "AE",
        _ => "?"
    };
}
```

- [ ] **Step 6: Green 验证**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: Build succeeded.

> **构造已确认**：`CollectorTask.Type` 是 byte（1=DA 2=UA 3=AE），不叫 Protocol。`Id`/`CreatedAt` 在 `EntityBase`。helper 与 `ProtocolLabel` 已按此写好。

- [ ] **Step 7: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/Workspace/ wpf/tests/Dc.App.Tests/ViewModels/Workspace/
git commit -m ":sparkles: S3a.1: TaskWorkspaceViewModel master 协调器（6 unit tests）"
```

---

## Task 2: WorkspaceOverviewViewModel — KPI + sparkline (TDD)

**Files:**
- Create: `wpf/src/Dc.App/ViewModels/Workspace/WorkspaceOverviewViewModel.cs`
- Create: `wpf/tests/Dc.App.Tests/ViewModels/Workspace/WorkspaceOverviewViewModelTests.cs`

> 概览 tab。给定一个 taskId，1s 采样该任务 ValueCount delta，维护 capped(60) 速率历史，输出供 sparkline 用的归一化点。KPI 直接从 `TaskDiagnostics` 取。

- [ ] **Step 1: 写测试（Red）**

`wpf/tests/Dc.App.Tests/ViewModels/Workspace/WorkspaceOverviewViewModelTests.cs`:

```csharp
using Dc.App.ViewModels.Dashboard;
using Dc.App.ViewModels.Workspace;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.Tests.ViewModels.Workspace;

public class WorkspaceOverviewViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeOrchView : IDashboardOrchestratorView
    {
        public IReadOnlyList<TaskDiagnostics> Diags { get; set; } = Array.Empty<TaskDiagnostics>();
        public IReadOnlyCollection<string> Running { get; set; } = Array.Empty<string>();
        public IReadOnlyList<TaskDiagnostics> GetDiagnostics() => Diags;
        public IReadOnlyCollection<string> RunningTaskIds => Running;
    }

    private static TaskDiagnostics Diag(string id, long vc, DateTimeOffset start, DateTimeOffset hb)
        => new(id, start, hb, hb, vc, 0, 0, 42);

    [Fact]
    public void Sample_NoTask_KpisZero()
    {
        var orch = new FakeOrchView();
        var vm = new WorkspaceOverviewViewModel(orch, () => Now);
        vm.SetTask("missing");

        vm.Sample();

        Assert.Equal("—", vm.UptimeDisplay);
        Assert.Equal(0, vm.TotalMessages);
        Assert.Empty(vm.SparklineRates);
    }

    [Fact]
    public void Sample_PopulatesKpisFromDiagnostics()
    {
        var orch = new FakeOrchView
        {
            Diags = new[] { Diag("t1", 1000, Now.AddMinutes(-10), Now.AddSeconds(-1)) },
            Running = new[] { "t1" }
        };
        var vm = new WorkspaceOverviewViewModel(orch, () => Now);
        vm.SetTask("t1");

        vm.Sample();

        Assert.Equal(1000, vm.TotalMessages);
        Assert.Equal(42, vm.SubscribedTags);
        Assert.True(vm.IsRunning);
    }

    [Fact]
    public void Sample_TwiceGrowingValueCount_AppendsRatePoint()
    {
        var t = Now;
        DateTimeOffset Clock() => t;
        var orch = new FakeOrchView
        {
            Diags = new[] { Diag("t1", 100, Now.AddMinutes(-10), Now) },
            Running = new[] { "t1" }
        };
        var vm = new WorkspaceOverviewViewModel(orch, Clock);
        vm.SetTask("t1");

        vm.Sample();   // baseline, no rate yet
        Assert.Empty(vm.SparklineRates);

        t = Now.AddSeconds(1);
        orch.Diags = new[] { Diag("t1", 180, Now.AddMinutes(-10), t) };
        vm.Sample();   // rate = 80

        Assert.Single(vm.SparklineRates);
        Assert.Equal(80.0, vm.SparklineRates[0], precision: 1);
    }

    [Fact]
    public void SparklineRates_CappedAt60()
    {
        var t = Now;
        DateTimeOffset Clock() => t;
        var orch = new FakeOrchView { Running = new[] { "t1" } };
        var vm = new WorkspaceOverviewViewModel(orch, Clock);
        vm.SetTask("t1");

        long vc = 0;
        for (int i = 0; i < 80; i++)
        {
            orch.Diags = new[] { Diag("t1", vc, Now.AddMinutes(-10), t) };
            vm.Sample();
            vc += 10;
            t = t.AddSeconds(1);
        }

        Assert.Equal(60, vm.SparklineRates.Count);
    }

    [Fact]
    public void SetTask_Switching_ResetsBuffer()
    {
        var t = Now;
        DateTimeOffset Clock() => t;
        var orch = new FakeOrchView { Running = new[] { "t1", "t2" } };
        var vm = new WorkspaceOverviewViewModel(orch, Clock);

        vm.SetTask("t1");
        orch.Diags = new[] { Diag("t1", 100, Now, t) };
        vm.Sample();
        t = Now.AddSeconds(1);
        orch.Diags = new[] { Diag("t1", 200, Now, t) };
        vm.Sample();
        Assert.Single(vm.SparklineRates);

        vm.SetTask("t2");   // 切任务 → 清空
        Assert.Empty(vm.SparklineRates);
    }
}
```

- [ ] **Step 2: Red**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: FAILED — WorkspaceOverviewViewModel 不存在。

- [ ] **Step 3: 实现 WorkspaceOverviewViewModel**

`wpf/src/Dc.App/ViewModels/Workspace/WorkspaceOverviewViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dc.App.ViewModels.Dashboard;

namespace Dc.App.ViewModels.Workspace;

public sealed partial class WorkspaceOverviewViewModel : ObservableObject
{
    private const int MaxPoints = 60;

    private readonly IDashboardOrchestratorView _orch;
    private readonly Func<DateTimeOffset> _clock;

    private string? _taskId;
    private long? _lastValueCount;
    private DateTimeOffset _lastSampleAt;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private long _totalMessages;
    [ObservableProperty] private long _errorCount;
    [ObservableProperty] private int _restartCount;
    [ObservableProperty] private int _subscribedTags;
    [ObservableProperty] private string _uptimeDisplay = "—";
    [ObservableProperty] private string _lastHeartbeatDisplay = "—";

    public ObservableCollection<double> SparklineRates { get; } = new();

    public WorkspaceOverviewViewModel(IDashboardOrchestratorView orchestratorView, Func<DateTimeOffset> clock)
    {
        _orch = orchestratorView;
        _clock = clock;
    }

    public void SetTask(string? taskId)
    {
        _taskId = taskId;
        _lastValueCount = null;
        SparklineRates.Clear();
    }

    public void Sample()
    {
        if (_taskId is null) return;
        var now = _clock();
        var diag = _orch.GetDiagnostics().FirstOrDefault(d => d.TaskId == _taskId);
        if (diag is null)
        {
            IsRunning = false;
            TotalMessages = 0;
            ErrorCount = 0;
            RestartCount = 0;
            SubscribedTags = 0;
            UptimeDisplay = "—";
            LastHeartbeatDisplay = "—";
            return;
        }

        IsRunning = _orch.RunningTaskIds.Contains(_taskId);
        TotalMessages = diag.ValueCount;
        ErrorCount = diag.PublishErrorCount;
        RestartCount = diag.RestartCount;
        SubscribedTags = diag.SubscribedTagCount;
        UptimeDisplay = FormatUptime(now - diag.StartedAt);
        LastHeartbeatDisplay = diag.LastHeartbeatAt is { } hb
            ? $"{(now - hb).TotalSeconds:F0}s 前"
            : "—";

        if (_lastValueCount is { } prev)
        {
            var elapsed = (now - _lastSampleAt).TotalSeconds;
            if (elapsed > 0.001)
            {
                var rate = (diag.ValueCount - prev) / elapsed;
                if (rate < 0) rate = 0;
                SparklineRates.Add(rate);
                while (SparklineRates.Count > MaxPoints) SparklineRates.RemoveAt(0);
            }
        }
        _lastValueCount = diag.ValueCount;
        _lastSampleAt = now;
    }

    private static string FormatUptime(TimeSpan s)
    {
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d {s.Hours}h";
        if (s.TotalHours >= 1) return $"{s.Hours}h {s.Minutes}m";
        return $"{s.Minutes}m {s.Seconds}s";
    }
}
```

- [ ] **Step 4: Green**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/Workspace/WorkspaceOverviewViewModel.cs \
        wpf/tests/Dc.App.Tests/ViewModels/Workspace/WorkspaceOverviewViewModelTests.cs
git commit -m ":sparkles: S3a.2: WorkspaceOverviewViewModel KPI + 客户端 sparkline（5 unit tests）"
```

---

## Task 3: 把 detail tab 协调进 TaskWorkspaceViewModel

**Files:**
- Modify: `wpf/src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs`

> master 选中变化 → 概览 VM SetTask + Tag VM 切 task 范围 + 当前 tab 默认回概览。引入 tab 切换 + DispatcherTimer 驱动概览采样。

- [ ] **Step 1: 扩展 TaskWorkspaceViewModel**

在 `TaskWorkspaceViewModel` 加：
- 构造再注入 `WorkspaceOverviewViewModel overview` 和 `Func<string, TagsViewModel> tagsFactory`（按 task 拿一个配置好的 TagsViewModel；S3a 用现有单例 TagsViewModel + 设置过滤即可，工厂签名留扩展）
- `[ObservableProperty] string _selectedTab = "overview";`
- `[ObservableProperty] object? _currentTabContent;`
- `public WorkspaceOverviewViewModel Overview { get; }`
- `public TagsViewModel TagsPanel { get; }`
- `OnSelectedTaskChanged`：调用 `Overview.SetTask(taskId)`、`TagsPanel` 切到该 task（设置其 task 范围属性 + reload）、`SelectedTab = "overview"`、刷新 CurrentTabContent
- `OnSelectedTabChanged`：根据 key 把 CurrentTabContent 设为 Overview 或 TagsPanel
- `Start(Dispatcher)` / `Stop()`：1s DispatcherTimer → `Overview.Sample()`（仅当 SelectedTab=="overview"）

具体改动（追加到现有 class）：

```csharp
    public WorkspaceOverviewViewModel Overview { get; }
    public TagsViewModel TagsPanel { get; }

    [ObservableProperty] private string _selectedTab = "overview";
    [ObservableProperty] private object? _currentTabContent;

    private System.Windows.Threading.DispatcherTimer? _timer;
```

构造函数签名改为（在现有参数后追加）：

```csharp
    public TaskWorkspaceViewModel(
        IWorkspaceTaskSource source,
        IDashboardOrchestratorView orchestratorView,
        Func<DateTimeOffset> clock,
        TimeSpan heartbeatTimeout,
        WorkspaceOverviewViewModel overview,
        TagsViewModel tagsPanel)
    {
        _source = source;
        _orch = orchestratorView;
        _clock = clock;
        _heartbeatTimeout = heartbeatTimeout;
        Overview = overview;
        TagsPanel = tagsPanel;
        TagsPanel.IsEmbedded = true;

        FilteredTasks = CollectionViewSource.GetDefaultView(AllTasks);
        FilteredTasks.Filter = FilterRow;
        CurrentTabContent = Overview;
    }
```

加分部方法 + tab 协调：

```csharp
    partial void OnSelectedTaskChanged(TaskMasterRow? value)
    {
        Overview.SetTask(value?.TaskId);
        if (value is not null)
        {
            TagsPanel.TaskScope = value.TaskId;   // 见下方 Task 4：TagsViewModel 加 TaskScope
            _ = TagsPanel.LoadAsync();
        }
        SelectedTab = "overview";
        UpdateTabContent();
        Overview.Sample();
    }

    partial void OnSelectedTabChanged(string value) => UpdateTabContent();

    private void UpdateTabContent()
    {
        CurrentTabContent = SelectedTab switch
        {
            "tags" => TagsPanel,
            _ => Overview
        };
    }

    public void Start(System.Windows.Threading.Dispatcher dispatcher)
    {
        if (_timer is not null) return;
        _timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) =>
        {
            if (SelectedTab == "overview") Overview.Sample();
        };
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
```

> 测试已有的 6 个 ctor 调用要更新 — 在 `TaskWorkspaceViewModelTests.Build()` 里补 `WorkspaceOverviewViewModel` + 一个 fake/真 `TagsViewModel`。`TagsViewModel` 构造较重（DbFactory + dialogs + orchestrator + excel + filepicker）。**为避免测试被 TagsViewModel 拖累**，把 TagsPanel 类型从具体 `TagsViewModel` 改为新接口 `IEmbeddableTagPanel`（含 `bool IsEmbedded`、`string? TaskScope`、`Task LoadAsync()`），`TagsViewModel` 实现它。测试用 Fake 实现。

实现接口 `wpf/src/Dc.App/ViewModels/Workspace/IEmbeddableTagPanel.cs`:

```csharp
namespace Dc.App.ViewModels.Workspace;

public interface IEmbeddableTagPanel
{
    bool IsEmbedded { get; set; }
    string? TaskScope { get; set; }
    Task LoadAsync();
}
```

把 `TaskWorkspaceViewModel` 里 `TagsViewModel TagsPanel` 改成 `IEmbeddableTagPanel TagsPanel`，构造参数同步。

- [ ] **Step 2: 更新测试 Build() + 加 tab 测试**

更新 `TaskWorkspaceViewModelTests`：加 Fake：

```csharp
    private sealed class FakeTagPanel : IEmbeddableTagPanel
    {
        public bool IsEmbedded { get; set; }
        public string? TaskScope { get; set; }
        public int LoadCount;
        public Task LoadAsync() { LoadCount++; return Task.CompletedTask; }
    }

    private sealed class FakeOverviewSource : IDashboardOrchestratorView { /* 同 FakeOrchView */ }
```

`Build()` 改为构造 `WorkspaceOverviewViewModel`（用同一个 FakeOrchView）+ `FakeTagPanel`，传进 `TaskWorkspaceViewModel`。新增测试：

```csharp
    [Fact]
    public async Task SelectingTask_SetsTagScopeAndDefaultsToOverviewTab()
    {
        var (src, _, tagPanel, vm) = BuildWithTags();
        src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();

        vm.SelectedTask = vm.AllTasks[0];

        Assert.Equal("t1", tagPanel.TaskScope);
        Assert.True(tagPanel.LoadCount >= 1);
        Assert.Equal("overview", vm.SelectedTab);
        Assert.Same(vm.Overview, vm.CurrentTabContent);
    }

    [Fact]
    public async Task SwitchingToTagsTab_SetsCurrentContentToTagsPanel()
    {
        var (src, _, tagPanel, vm) = BuildWithTags();
        src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];

        vm.SelectedTab = "tags";

        Assert.Same(tagPanel, vm.CurrentTabContent);
    }
```

(把 `Build()` 重命名/包一层 `BuildWithTags()` 返回 4-tuple，含 tagPanel。)

- [ ] **Step 3: 跑测试**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: Build succeeded.

- [ ] **Step 4: TagsViewModel 实现 IEmbeddableTagPanel**

打开 `wpf/src/Dc.App/ViewModels/TagsViewModel.cs`。确认它已有 `IsEmbedded`（现有）。加 `TaskScope` 属性 + 让 class 声明 `: ObservableObject, IEmbeddableTagPanel`（加 `using Dc.App.ViewModels.Workspace;`）。`TaskScope` setter 内：记录当前 task id，供 `LoadAsync` 过滤（现有 LoadAsync 若按 group/task 过滤，把 TaskScope 纳入 where）。若 `LoadAsync` 当前签名不同（如带参数），加一个无参 `LoadAsync()` 满足接口，内部用 TaskScope。

> 这一步要读 TagsViewModel 现有实现再改。若 TagsViewModel 没有现成的 task 级过滤，最小实现：TaskScope 设值后在查询里 `Where(t => t.TaskId == TaskScope)`。报告实际改动。

- [ ] **Step 5: 构建 + commit**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -3
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/Workspace/ wpf/src/Dc.App/ViewModels/TagsViewModel.cs \
        wpf/tests/Dc.App.Tests/ViewModels/Workspace/
git commit -m ":sparkles: S3a.3: 工作台 tab 协调（概览/Tag）+ IEmbeddableTagPanel 解耦"
```

---

## Task 4: TaskWorkspaceView.xaml master-detail 布局

**Files:**
- Create: `wpf/src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml`
- Create: `wpf/src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml.cs`
- Create: `wpf/src/Dc.App/Views/Workspace/SparklineConverter.cs`

> 照 c2-workspace.html：左 master（汇总胶囊 + 搜索 + 状态筛选 + 列表 + 新建/导入），右 detail（标题 + tabs + ContentControl）。概览 tab sparkline 用 Polyline。视觉跟随主题（不强制暗色，与 Dashboard 不同 — 工作台是日常操作面）。

- [ ] **Step 1: Sparkline converter（List<double> → PointCollection）**

`wpf/src/Dc.App/Views/Workspace/SparklineConverter.cs`:

```csharp
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Dc.App.Views.Workspace;

/// 把速率序列归一化成 200x40 视窗内的折线点。
public sealed class SparklineConverter : IValueConverter
{
    private const double W = 200, H = 40;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pts = new PointCollection();
        if (value is not IEnumerable en) return pts;
        var rates = en.Cast<double>().ToList();
        if (rates.Count < 2) return pts;

        double max = Math.Max(1.0, rates.Max());
        for (int i = 0; i < rates.Count; i++)
        {
            double x = W * i / (rates.Count - 1);
            double y = H - (rates[i] / max * H);
            pts.Add(new Point(x, y));
        }
        return pts;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 2: TaskWorkspaceView.xaml**

`wpf/src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml`（关键结构，照 mockup；用 wpfui Card/样式，主题跟随）：

```xml
<UserControl x:Class="Dc.App.Views.Workspace.TaskWorkspaceView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:wsv="clr-namespace:Dc.App.Views.Workspace"
             xmlns:ws="clr-namespace:Dc.App.ViewModels.Workspace">
    <UserControl.Resources>
        <wsv:SparklineConverter x:Key="Spark" />
    </UserControl.Resources>
    <Grid Margin="16,12">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="260" />
            <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>

        <!-- master -->
        <DockPanel Grid.Column="0" Margin="0,0,12,0">
            <!-- 汇总胶囊 -->
            <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
                <Border Background="#1F22C55E" CornerRadius="999" Padding="8,2" Margin="0,0,4,0">
                    <TextBlock FontSize="11">
                        <Run Text="运行 " /><Run Text="{Binding RunningCount}" />
                    </TextBlock>
                </Border>
                <Border Background="#1FEF4444" CornerRadius="999" Padding="8,2" Margin="0,0,4,0">
                    <TextBlock FontSize="11">
                        <Run Text="停 " /><Run Text="{Binding StoppedCount}" />
                    </TextBlock>
                </Border>
                <Border Background="#1FD97706" CornerRadius="999" Padding="8,2">
                    <TextBlock FontSize="11">
                        <Run Text="告警 " /><Run Text="{Binding AlertCount}" />
                    </TextBlock>
                </Border>
            </StackPanel>

            <!-- 搜索 + 状态筛选 -->
            <ui:TextBox DockPanel.Dock="Top" PlaceholderText="搜索任务…"
                        Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}"
                        Margin="0,0,0,6" />
            <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
                <RadioButton Content="全部" IsChecked="True" GroupName="sf" Margin="0,0,8,0"
                             Checked="OnFilterAll" />
                <RadioButton Content="运行中" GroupName="sf" Margin="0,0,8,0" Checked="OnFilterRunning" />
                <RadioButton Content="已停止" GroupName="sf" Checked="OnFilterStopped" />
            </StackPanel>

            <!-- 新建/导入（底部） -->
            <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,8,0,0">
                <ui:Button Content="+ 新建" Margin="0,0,6,0" Click="OnNewTask" />
                <ui:Button Content="导入" Click="OnImport" />
            </StackPanel>

            <!-- 列表 -->
            <ListBox ItemsSource="{Binding FilteredTasks}"
                     SelectedItem="{Binding SelectedTask, Mode=TwoWay}"
                     BorderThickness="0" Background="Transparent">
                <ListBox.ItemTemplate>
                    <DataTemplate DataType="{x:Type ws:TaskMasterRow}">
                        <StackPanel Margin="2,4">
                            <StackPanel Orientation="Horizontal">
                                <Ellipse Width="8" Height="8" Margin="0,0,8,0" VerticalAlignment="Center">
                                    <Ellipse.Style>
                                        <Style TargetType="Ellipse">
                                            <Setter Property="Fill" Value="#22C55E" />
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding IsRunning}" Value="False">
                                                    <Setter Property="Fill" Value="#EF4444" />
                                                </DataTrigger>
                                                <DataTrigger Binding="{Binding HasAlert}" Value="True">
                                                    <Setter Property="Fill" Value="#D97706" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </Ellipse.Style>
                                </Ellipse>
                                <TextBlock Text="{Binding Name}" FontSize="13" />
                            </StackPanel>
                            <TextBlock FontSize="10" Opacity="0.5" Margin="16,2,0,0">
                                <Run Text="{Binding Protocol}" /><Run Text=" · " /><Run Text="{Binding TagCount}" /><Run Text=" tag" />
                            </TextBlock>
                        </StackPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </DockPanel>

        <!-- detail -->
        <Border Grid.Column="1" BorderThickness="1" CornerRadius="8"
                BorderBrush="{DynamicResource ControlElevationBorderBrush}">
            <DockPanel>
                <!-- 标题 + 启停 -->
                <Grid DockPanel.Dock="Top" Margin="16,12">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Text="{Binding SelectedTask.Name, FallbackValue='选择一个任务'}"
                               FontSize="18" FontWeight="SemiBold" VerticalAlignment="Center" />
                    <StackPanel Grid.Column="1" Orientation="Horizontal">
                        <ui:Button Content="启动" Margin="0,0,6,0" Click="OnStart" />
                        <ui:Button Content="停止" Margin="0,0,6,0" Click="OnStop" />
                        <ui:Button Content="重启" Click="OnRestart" />
                    </StackPanel>
                </Grid>

                <!-- tabs -->
                <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="16,0,16,8">
                    <RadioButton Content="概览" IsChecked="True" GroupName="tab" Margin="0,0,12,0" Checked="OnTabOverview" />
                    <RadioButton Content="Tag" GroupName="tab" Margin="0,0,12,0" Checked="OnTabTags" />
                    <TextBlock Text="分组 · 实时 · 诊断 · 配置 (S3b/c)" Opacity="0.35" FontSize="11" VerticalAlignment="Center" />
                </StackPanel>

                <!-- tab content -->
                <ContentControl Content="{Binding CurrentTabContent}" Margin="16,0,16,12">
                    <ContentControl.Resources>
                        <DataTemplate DataType="{x:Type ws:WorkspaceOverviewViewModel}">
                            <wsv:OverviewTabPanel />
                        </DataTemplate>
                        <!-- Tag tab：TagsViewModel 已有 DataTemplate 映射在 App.xaml -->
                    </ContentControl.Resources>
                </ContentControl>
            </DockPanel>
        </Border>
    </Grid>
</UserControl>
```

> 注意：`TagsViewModel` 的 DataTemplate 已在 App.xaml 注册（→ TagsView），ContentControl 会自动套用。Overview 需要一个 `OverviewTabPanel` UserControl（下一步）。

- [ ] **Step 3: OverviewTabPanel.xaml（概览内容 + sparkline）**

`wpf/src/Dc.App/Views/Workspace/OverviewTabPanel.xaml`（KPI 卡 + Polyline sparkline）：

```xml
<UserControl x:Class="Dc.App.Views.Workspace.OverviewTabPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:wsv="clr-namespace:Dc.App.Views.Workspace">
    <UserControl.Resources>
        <wsv:SparklineConverter x:Key="Spark" />
    </UserControl.Resources>
    <StackPanel>
        <UniformGrid Columns="3" Rows="2" Margin="0,0,0,12">
            <ui:Card Margin="0,0,6,6"><StackPanel>
                <TextBlock Text="{Binding TotalMessages}" FontSize="20" FontWeight="SemiBold" />
                <TextBlock Text="累计消息" FontSize="11" Opacity="0.6" /></StackPanel></ui:Card>
            <ui:Card Margin="3,0,3,6"><StackPanel>
                <TextBlock Text="{Binding SubscribedTags}" FontSize="20" FontWeight="SemiBold" />
                <TextBlock Text="订阅 Tag" FontSize="11" Opacity="0.6" /></StackPanel></ui:Card>
            <ui:Card Margin="6,0,0,6"><StackPanel>
                <TextBlock Text="{Binding ErrorCount}" FontSize="20" FontWeight="SemiBold" />
                <TextBlock Text="发送错误" FontSize="11" Opacity="0.6" /></StackPanel></ui:Card>
            <ui:Card Margin="0,6,6,0"><StackPanel>
                <TextBlock Text="{Binding RestartCount}" FontSize="20" FontWeight="SemiBold" />
                <TextBlock Text="重启次数" FontSize="11" Opacity="0.6" /></StackPanel></ui:Card>
            <ui:Card Margin="3,6,3,0"><StackPanel>
                <TextBlock Text="{Binding UptimeDisplay}" FontSize="20" FontWeight="SemiBold" />
                <TextBlock Text="运行时长" FontSize="11" Opacity="0.6" /></StackPanel></ui:Card>
            <ui:Card Margin="6,6,0,0"><StackPanel>
                <TextBlock Text="{Binding LastHeartbeatDisplay}" FontSize="20" FontWeight="SemiBold" />
                <TextBlock Text="最后心跳" FontSize="11" Opacity="0.6" /></StackPanel></ui:Card>
        </UniformGrid>

        <ui:Card>
            <StackPanel>
                <TextBlock Text="消息速率 (最近 60s)" FontSize="12" Opacity="0.7" Margin="0,0,0,8" />
                <Polyline Stroke="#0067C0" StrokeThickness="1.5" Height="40"
                          Stretch="None"
                          Points="{Binding SparklineRates, Converter={StaticResource Spark}}" />
            </StackPanel>
        </ui:Card>
    </StackPanel>
</UserControl>
```

加 `OverviewTabPanel.xaml.cs`（标准 InitializeComponent）。

- [ ] **Step 4: TaskWorkspaceView.xaml.cs 代码后台（按钮事件桥接 VM）**

`wpf/src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml.cs`：

```csharp
using System.Windows.Controls;
using Dc.App.ViewModels.Workspace;

namespace Dc.App.Views.Workspace;

public partial class TaskWorkspaceView : UserControl
{
    public TaskWorkspaceView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is TaskWorkspaceViewModel vm)
            {
                vm.Start(Dispatcher);
                _ = vm.LoadAsync();
            }
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is TaskWorkspaceViewModel vm) vm.Stop();
        };
    }

    private TaskWorkspaceViewModel? Vm => DataContext as TaskWorkspaceViewModel;

    private void OnFilterAll(object s, System.Windows.RoutedEventArgs e)     { if (Vm is { } v) v.StatusFilter = WorkspaceStatusFilter.All; }
    private void OnFilterRunning(object s, System.Windows.RoutedEventArgs e) { if (Vm is { } v) v.StatusFilter = WorkspaceStatusFilter.Running; }
    private void OnFilterStopped(object s, System.Windows.RoutedEventArgs e) { if (Vm is { } v) v.StatusFilter = WorkspaceStatusFilter.Stopped; }
    private void OnTabOverview(object s, System.Windows.RoutedEventArgs e)   { if (Vm is { } v) v.SelectedTab = "overview"; }
    private void OnTabTags(object s, System.Windows.RoutedEventArgs e)       { if (Vm is { } v) v.SelectedTab = "tags"; }

    private async void OnStart(object s, System.Windows.RoutedEventArgs e)   { if (Vm is { } v) await v.StartSelectedAsync(); }
    private async void OnStop(object s, System.Windows.RoutedEventArgs e)    { if (Vm is { } v) await v.StopSelectedAsync(); }
    private async void OnRestart(object s, System.Windows.RoutedEventArgs e) { if (Vm is { } v) await v.RestartSelectedAsync(); }
    private async void OnNewTask(object s, System.Windows.RoutedEventArgs e) { if (Vm is { } v) await v.NewTaskAsync(); }
    private async void OnImport(object s, System.Windows.RoutedEventArgs e)  { if (Vm is { } v) await v.ImportAsync(); }
}
```

> 需要在 `TaskWorkspaceViewModel` 加 `StartSelectedAsync` / `StopSelectedAsync` / `RestartSelectedAsync` / `NewTaskAsync` / `ImportAsync` 方法。Start/Stop 用注入的 `TaskOrchestrator`（构造再加一个参数）+ `IWorkspaceTaskSource` 拿 task 详情构 `TaskStartRequest`。New/Import 复用 `ITaskEditorDialog` / `ITagExcelService`。**这些命令的逻辑可从旧 TasksViewModel 搬**——读旧 TasksViewModel 的 StartCommand/StopCommand/NewCommand 实现，迁移过来。完成后 LoadAsync 刷新。报告搬运细节。

- [ ] **Step 5: 构建**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -8
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Views/Workspace/ wpf/src/Dc.App/ViewModels/Workspace/
git commit -m ":sparkles: S3a.4: TaskWorkspaceView master-detail 布局 + 概览 sparkline + Tag tab"
```

---

## Task 5: 路由切换 + DI + 删旧 TasksView

**Files:**
- Modify: `wpf/src/Dc.App/Composition/ServiceRegistration.cs`
- Modify: `wpf/src/Dc.App/App.xaml`
- Modify: `wpf/src/Dc.App/Views/Shell/ShellWindow.xaml.cs`
- Delete: `wpf/src/Dc.App/Views/TasksView.xaml` + `.cs`
- Delete: `wpf/src/Dc.App/ViewModels/TasksViewModel.cs`
- Delete: `wpf/src/Dc.App/ViewModels/TaskRowViewModel.cs`（若仅 TasksViewModel 用）

- [ ] **Step 1: DI 注册 workspace 相关**

在 `ServiceRegistration.cs` 加（替换原 `services.AddSingleton<TasksViewModel>();`）：

```csharp
        // 采集任务工作台（S3a）
        services.AddSingleton<Dc.App.ViewModels.Workspace.IWorkspaceTaskSource,
                              Dc.App.ViewModels.Workspace.DbWorkspaceTaskSource>();
        services.AddSingleton<Dc.App.ViewModels.Workspace.WorkspaceOverviewViewModel>(sp =>
            new Dc.App.ViewModels.Workspace.WorkspaceOverviewViewModel(
                sp.GetRequiredService<Dc.App.ViewModels.Dashboard.IDashboardOrchestratorView>(),
                () => DateTimeOffset.UtcNow));
        services.AddSingleton<Dc.App.ViewModels.Workspace.TaskWorkspaceViewModel>(sp =>
            new Dc.App.ViewModels.Workspace.TaskWorkspaceViewModel(
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.IWorkspaceTaskSource>(),
                sp.GetRequiredService<Dc.App.ViewModels.Dashboard.IDashboardOrchestratorView>(),
                () => DateTimeOffset.UtcNow,
                sp.GetRequiredService<OrchestratorOptions>().HeartbeatTimeout,
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.WorkspaceOverviewViewModel>(),
                sp.GetRequiredService<TagsViewModel>(),
                sp.GetRequiredService<TaskOrchestrator>(),
                sp.GetRequiredService<Dc.App.Services.ITaskEditorDialog>(),
                sp.GetRequiredService<Dc.Infrastructure.Excel.ITagExcelService>()));
```

（构造参数以 Task 4 最终签名为准；TagsViewModel 仍单例注册，作为 IEmbeddableTagPanel 注入 — 确认 TagsViewModel 已注册。）

新建 `wpf/src/Dc.App/ViewModels/Workspace/DbWorkspaceTaskSource.cs`：

```csharp
using Dc.Domain.Entities;
using Dc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dc.App.ViewModels.Workspace;

public sealed class DbWorkspaceTaskSource : IWorkspaceTaskSource
{
    private readonly IDbContextFactory<DcDbContext> _dbFactory;
    public DbWorkspaceTaskSource(IDbContextFactory<DcDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<CollectorTask>> LoadTasksAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Tasks.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();
    }
}
```

删除原 `services.AddSingleton<TasksViewModel>();` 行。`GroupsViewModel`/`TagsViewModel` 注册保留（TagsViewModel 仍被 workspace 用；GroupsViewModel 留给 S3b 分组 tab）。

- [ ] **Step 2: 路由表指向 TaskWorkspaceViewModel**

`ServiceRegistration.cs` 的 NavigationService 路由表里，把 "workspace" 那行的 VM 类型从 `typeof(TasksViewModel)` 改成 `typeof(Dc.App.ViewModels.Workspace.TaskWorkspaceViewModel)`。

- [ ] **Step 3: App.xaml DataTemplate**

`App.xaml`：删除 `TasksViewModel → TasksView` 的 DataTemplate（旧），加：

```xml
<DataTemplate DataType="{x:Type wsvm:TaskWorkspaceViewModel}">
    <wsview:TaskWorkspaceView />
</DataTemplate>
```

并在 Application 标签加 xmlns：
```
xmlns:wsvm="clr-namespace:Dc.App.ViewModels.Workspace"
xmlns:wsview="clr-namespace:Dc.App.Views.Workspace"
```

保留 `TagsViewModel → TagsView` 的 DataTemplate（工作台 Tag tab 要用）。GroupsViewModel/LiveData/Diagnostics 的 DataTemplate 也保留。

- [ ] **Step 4: 删旧 TasksView/TasksViewModel/TaskRowViewModel**

```bash
cd /home/adamyu/workspace/dc/wpf
git rm src/Dc.App/Views/TasksView.xaml src/Dc.App/Views/TasksView.xaml.cs
git rm src/Dc.App/ViewModels/TasksViewModel.cs
# TaskRowViewModel 若被别处引用则保留；先 grep
grep -rn "TaskRowViewModel" src/Dc.App --include=*.cs --include=*.xaml | grep -v "/obj/" | grep -v "/bin/"
```

若 `TaskRowViewModel` 仅 TasksViewModel 用，`git rm src/Dc.App/ViewModels/TaskRowViewModel.cs`。否则保留。

旧 TasksViewModel 里嵌入了 GroupsPanel/TagsPanel 联动逻辑 — 删除后确认 GroupsViewModel/TagsViewModel 自身不依赖 TasksViewModel（应该不依赖，是反向）。grep 验证。

- [ ] **Step 5: 构建（处理 stale 引用）**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -10
```

若报 stale 引用（TasksViewModel/TasksView），grep 清理：
```bash
grep -rn "TasksViewModel\|TasksView\b" src/Dc.App --include=*.cs --include=*.xaml | grep -v "/obj/" | grep -v "/bin/"
```

Expected 最终：Build succeeded.

- [ ] **Step 6: Commit**

```bash
cd /home/adamyu/workspace/dc
git add -A wpf/src/Dc.App/
git commit -m ":fire: S3a.5: 路由切到 TaskWorkspace，删旧 TasksView/TasksViewModel"
```

---

## Task 6: 全量回归 + push

- [ ] **Step 1: 全套测试 + build**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.Infrastructure.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo 2>&1 | tail -4
dotnet test tests/Dc.Integration.Tests   -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo 2>&1 | tail -4
dotnet build src/Dc.App tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -3
```

Expected: Infra 48 / Integration 10 / Dc.App build 0 错误。

- [ ] **Step 2: Push**

```bash
cd /home/adamyu/workspace/dc
git push origin wpf-opc-collector
```

- [ ] **Step 3: PR #5 评论**

```bash
CREDS=$(grep "git.adamyu.top" ~/.git-credentials | sed 's|https://||;s|@.*||')
cat > /tmp/s3a_comment.json <<'EOF'
{"body":"### S3a · 采集任务工作台（master + 概览 + Tag）完成\n\n- TaskWorkspaceViewModel master 协调器：搜索 / 状态筛选 / 汇总胶囊（复用 HealthEvaluator 算告警数）\n- WorkspaceOverviewViewModel：KPI 数字 + 客户端采样 sparkline（不动 orchestrator）\n- master-detail 布局 + tabs（概览 + Tag），剩 4 tab 占位 S3b/c\n- Tag tab 复用 TagsViewModel（IEmbeddableTagPanel 解耦）\n- 路由 workspace 切到 TaskWorkspaceViewModel，删旧 TasksView/TasksViewModel\n\nWindows walkthrough：选任务→看概览 KPI+sparkline；切 Tag tab→增删 Tag 热更新；搜索/状态筛选 master 列表；新建/导入任务"}
EOF
curl -sk -u "$CREDS" -H "Content-Type: application/json" \
  -X POST "https://git.adamyu.top:20443/api/v1/repos/adamyu/dc/issues/5/comments" \
  -d @/tmp/s3a_comment.json -o /tmp/c.json -w "HTTP %{http_code}\n"
jq -r '.html_url // .message' /tmp/c.json
rm -f /tmp/s3a_comment.json /tmp/c.json
```

---

## 验收

- Infra 48 + Integration 10 全绿
- Dc.App.Tests +约 13（S3a.1 6 + S3a.2 5 + S3a.3 2），累计 ~38，build 0 错误
- shell "采集任务" 进入是 master-detail 工作台
- 旧 TasksView 删除，无 stale 引用
- PR #5 评论已加 S3a 段
- Windows 真机验证留用户
