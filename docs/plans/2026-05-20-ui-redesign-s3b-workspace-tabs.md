# UI 重设计 S3b — 工作台剩余 4 tab（分组 / 实时 / 诊断 / 配置） 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).

**Goal:** 把 S3a 工作台 detail 区的「分组 · 实时 · 诊断 · 配置 (S3b/c)」占位提示换成 4 个真实 tab，复用现有 GroupsViewModel / LiveDataViewModel / DiagnosticsViewModel + 新 WorkspaceConfigViewModel（只读 + 编辑按钮开对话框）。分组 tab 选中分组自动跳 Tag tab 预筛。

**Architecture:**
延续 S3a 的 `IEmbeddableTagPanel` 模式，为 3 个复用 VM 定义最小接口（`IEmbeddableGroupPanel` / `IEmbeddableLivePanel` / `IEmbeddableDiagPanel`），让 TaskWorkspaceViewModel 可单测且不被重 ctor 拖累。配置 tab 是新 `WorkspaceConfigViewModel`（只读展示 + Edit 命令调 `ITaskEditorDialog.Edit`）。LiveData/Diagnostics 自带刷新（事件/内部 timer），无需工作台 timer 额外驱动。

**Tech Stack:** .NET 8 + WPF + Wpf.Ui + CommunityToolkit.Mvvm + EF Core + xUnit + Moq

**Spec:** `wpf/docs/specs/2026-05-19-ui-redesign-fluent-design.md` (Stage S3, §2.2)
**前置:** S3a 完成（commit 96c970d）

---

## 已锁定决策

| 项 | 决策 |
|---|---|
| 配置 tab | 只读展示任务参数 + 「编辑」按钮开现有 `ITaskEditorDialog` 对话框 |
| 分组 tab 联动 | 选中分组 → 自动跳 Tag tab + 设 `TagsPanel.GroupFilter` 预筛 |
| 实时/诊断刷新 | 复用各 VM 自带刷新（LiveData 事件驱动 / Diagnostics 内部 timer），工作台不额外驱动 |
| 复用方式 | 为 3 个 VM 定义最小 embeddable 接口（同 S3a 的 IEmbeddableTagPanel） |
| Diagnostics 任务过滤 | DiagnosticsViewModel 加 `TaskScope`（string?），非 null 时 Rows 只留该任务 |
| 视觉 | tab 内复用现有 View（经 App.xaml DataTemplate）；视觉混搭可接受，S5 收敛 |

---

## 前置说明

dotnet 在 `/home/adamyu/.dotnet/dotnet`，PATH 先 export。Linux 上 Dc.App.Tests 跑不了 net8.0-windows runtime，build 验证为准。

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

测试基线：infra 48 + integration 10 + Dc.App.Tests ~38（S1 14 + S2 11 + S3a 13）。S3b 完成 +约 8。

确认的既有 API：
- `GroupsViewModel`（`Dc.App.ViewModels`）：`[ObservableProperty] bool IsEmbedded`、`CollectorTask? TaskFilter`、`Group? SelectedGroup`、`Task LoadAsync()`、`ObservableCollection<CollectorTask> AvailableTasks`；`LoadAsync` 里 `if (TaskFilter is not null) q = q.Where(g => g.TaskId == TaskFilter.Id)`
- `LiveDataViewModel`（`Dc.App.ViewModels`）：`[ObservableProperty] string? TaskFilter`、`RowsView`（按 TaskId 过滤）、`AvailableTaskIds`
- `DiagnosticsViewModel`（`Dc.App.ViewModels`）：`ObservableCollection<DiagnosticsRowViewModel> Rows`、`void Refresh()`、内部 `AutoRefresh`/`RefreshIntervalSec` 自刷；**无任务过滤，本计划要加 TaskScope**
- `TagsViewModel`：`Group? GroupFilter`（S3a 已确认）、实现 `IEmbeddableTagPanel`
- `ITaskEditorDialog.Edit(CollectorTask? existing) → CollectorTask?`
- `TaskWorkspaceViewModel`（S3a）ctor 顺序（**读文件确认**）：`(IWorkspaceTaskSource, IDashboardOrchestratorView, Func<DateTimeOffset>, TimeSpan, WorkspaceOverviewViewModel, IEmbeddableTagPanel, TaskOrchestrator, ITaskEditorDialog)`。S3b 会再追加 4 个面板参数。
- `CollectorTask`：`Id`/`Server`/`Node`/`Clsid?`/`Type`(byte 1=DA 2=UA 3=AE)/`Interval`/`Deviation`/`TcpAddress`/`CreatedAt`
- `Group` entity：`Id`/`Name`/`TaskId`（确认字段名见 `Dc.Domain/Entities/Group.cs`）

---

## Task 1: DiagnosticsViewModel 加 TaskScope 过滤 (TDD)

**Files:**
- Modify: `wpf/src/Dc.App/ViewModels/DiagnosticsViewModel.cs`
- Create: `wpf/tests/Dc.App.Tests/ViewModels/DiagnosticsViewModelScopeTests.cs`

> 现 DiagnosticsViewModel.Refresh() 把所有运行任务的诊断塞进 Rows。加 `TaskScope`（string?），非 null 时只保留该 task 的行。

- [ ] **Step 1: 先读现状**

```bash
cat /home/adamyu/workspace/dc/wpf/src/Dc.App/ViewModels/DiagnosticsViewModel.cs
```

理解 Refresh() 如何填充 Rows（用 orchestrator.GetDiagnostics()），_rowIndex 字典，移除消失任务的逻辑。

- [ ] **Step 2: 写测试（Red）**

`wpf/tests/Dc.App.Tests/ViewModels/DiagnosticsViewModelScopeTests.cs`：

```csharp
using Dc.App.ViewModels;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.Tests.ViewModels;

public class DiagnosticsViewModelScopeTests
{
    // DiagnosticsViewModel ctor 接 TaskOrchestrator（具体类）。本测试只验证 TaskScope 过滤逻辑。
    // 若 DiagnosticsViewModel.Refresh 直接依赖 TaskOrchestrator 具体类型而难以构造，
    // 改为：抽取一个纯函数 FilterRows 或用 TaskScope 影响 Refresh 的产出。
    // 实现者：若 orchestrator 难 mock（sealed 类 + 复杂 ctor），把过滤做成可单测的纯逻辑：
    //   internal static bool MatchesScope(string? scope, string taskId) => scope is null || scope == taskId;
    // 并对该静态方法写测试。

    [Theory]
    [InlineData(null, "t1", true)]
    [InlineData("t1", "t1", true)]
    [InlineData("t1", "t2", false)]
    public void MatchesScope_FiltersByTaskId(string? scope, string taskId, bool expected)
    {
        Assert.Equal(expected, DiagnosticsViewModel.MatchesScope(scope, taskId));
    }
}
```

- [ ] **Step 3: Red**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: FAILED — `MatchesScope` / `TaskScope` 不存在。

- [ ] **Step 4: 实现**

在 `DiagnosticsViewModel` 加：
- `[ObservableProperty] private string? _taskScope;`（用 MVVM toolkit）
- `internal static bool MatchesScope(string? scope, string taskId) => scope is null || string.Equals(scope, taskId, StringComparison.Ordinal);`
- 在 `Refresh()` 填充 Rows 的循环里，跳过不匹配 scope 的诊断项：拿到每个 `TaskDiagnostics d` 后 `if (!MatchesScope(TaskScope, d.TaskId)) continue;`
- `partial void OnTaskScopeChanged(string? value) => Refresh();`（切 scope 立即刷新）

读实际 Refresh 实现，把 continue 放在正确位置（在 `_rowIndex` upsert 之前），并确保移除逻辑也尊重 scope（被过滤掉的 task 不应残留在 Rows）。最简：Refresh 开头按 scope 过滤 diagnostics 列表，后续逻辑不变。

- [ ] **Step 5: Green**

```bash
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/DiagnosticsViewModel.cs \
        wpf/tests/Dc.App.Tests/ViewModels/DiagnosticsViewModelScopeTests.cs
git commit -m ":sparkles: S3b.1: DiagnosticsViewModel 加 TaskScope 任务过滤（3 unit tests）"
```

---

## Task 2: WorkspaceConfigViewModel (TDD)

**Files:**
- Create: `wpf/src/Dc.App/ViewModels/Workspace/WorkspaceConfigViewModel.cs`
- Create: `wpf/tests/Dc.App.Tests/ViewModels/Workspace/WorkspaceConfigViewModelTests.cs`

> 只读展示选中任务参数 + 「编辑」命令。编辑通过注入的 `ITaskEditorDialog.Edit(existing)`；编辑成功（返回非 null）触发一个回调让工作台刷新。

- [ ] **Step 1: 写测试（Red）**

`wpf/tests/Dc.App.Tests/ViewModels/Workspace/WorkspaceConfigViewModelTests.cs`：

```csharp
using Dc.App.Services;
using Dc.App.ViewModels.Workspace;
using Dc.Domain.Entities;

namespace Dc.App.Tests.ViewModels.Workspace;

public class WorkspaceConfigViewModelTests
{
    private sealed class FakeEditor : ITaskEditorDialog
    {
        public CollectorTask? ReturnValue;
        public CollectorTask? LastArg;
        public int Calls;
        public CollectorTask? Edit(CollectorTask? existing)
        {
            Calls++; LastArg = existing; return ReturnValue;
        }
    }

    private static CollectorTask Task1(string id = "t1")
        => new() { Id = id, Server = "炉温", Node = "opc.tcp://x", Type = 2,
                   TcpAddress = "10.0.0.1:9000", Interval = 1000, Deviation = 1 };

    [Fact]
    public void SetTask_PopulatesReadonlyFields()
    {
        var vm = new WorkspaceConfigViewModel(new FakeEditor());
        vm.SetTask(Task1());

        Assert.Equal("炉温", vm.Server);
        Assert.Equal("opc.tcp://x", vm.Node);
        Assert.Equal("UA", vm.ProtocolLabel);
        Assert.Equal("10.0.0.1:9000", vm.TcpAddress);
        Assert.Equal(1000, vm.Interval);
        Assert.Equal(1, vm.Deviation);
        Assert.True(vm.HasTask);
    }

    [Fact]
    public void SetTask_Null_ClearsHasTask()
    {
        var vm = new WorkspaceConfigViewModel(new FakeEditor());
        vm.SetTask(Task1());
        vm.SetTask(null);
        Assert.False(vm.HasTask);
    }

    [Fact]
    public void EditCommand_CallsDialogWithCurrentTask()
    {
        var editor = new FakeEditor { ReturnValue = null };
        var vm = new WorkspaceConfigViewModel(editor);
        var task = Task1();
        vm.SetTask(task);

        vm.EditCommand.Execute(null);

        Assert.Equal(1, editor.Calls);
        Assert.Same(task, editor.LastArg);
    }

    [Fact]
    public void EditCommand_OnSuccess_RaisesEdited()
    {
        var editor = new FakeEditor { ReturnValue = Task1("t1") };
        var vm = new WorkspaceConfigViewModel(editor);
        vm.SetTask(Task1("t1"));

        string? editedId = null;
        vm.Edited += id => editedId = id;

        vm.EditCommand.Execute(null);

        Assert.Equal("t1", editedId);
    }

    [Fact]
    public void EditCommand_NoTask_DoesNothing()
    {
        var editor = new FakeEditor();
        var vm = new WorkspaceConfigViewModel(editor);
        vm.EditCommand.Execute(null);
        Assert.Equal(0, editor.Calls);
    }
}
```

- [ ] **Step 2: Red**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: FAILED — WorkspaceConfigViewModel 不存在。

- [ ] **Step 3: 实现**

`wpf/src/Dc.App/ViewModels/Workspace/WorkspaceConfigViewModel.cs`：

```csharp
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Services;
using Dc.Domain.Entities;

namespace Dc.App.ViewModels.Workspace;

public sealed partial class WorkspaceConfigViewModel : ObservableObject
{
    private readonly ITaskEditorDialog _editor;
    private CollectorTask? _task;

    [ObservableProperty] private bool _hasTask;
    [ObservableProperty] private string _server = string.Empty;
    [ObservableProperty] private string _node = string.Empty;
    [ObservableProperty] private string _protocolLabel = string.Empty;
    [ObservableProperty] private string _tcpAddress = string.Empty;
    [ObservableProperty] private int _interval;
    [ObservableProperty] private int _deviation;

    public ICommand EditCommand { get; }

    /// 编辑成功后触发，参数 = 任务 Id，供工作台刷新。
    public event Action<string>? Edited;

    public WorkspaceConfigViewModel(ITaskEditorDialog editor)
    {
        _editor = editor;
        EditCommand = new RelayCommand(Edit);
    }

    public void SetTask(CollectorTask? task)
    {
        _task = task;
        HasTask = task is not null;
        Server = task?.Server ?? string.Empty;
        Node = task?.Node ?? string.Empty;
        ProtocolLabel = task is null ? string.Empty : Label(task.Type);
        TcpAddress = task?.TcpAddress ?? string.Empty;
        Interval = task?.Interval ?? 0;
        Deviation = task?.Deviation ?? 0;
    }

    private void Edit()
    {
        if (_task is null) return;
        var result = _editor.Edit(_task);
        if (result is not null) Edited?.Invoke(_task.Id);
    }

    private static string Label(byte type) => type switch
    {
        1 => "DA", 2 => "UA", 3 => "AE", _ => "?"
    };
}
```

- [ ] **Step 4: Green**

```bash
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/Workspace/WorkspaceConfigViewModel.cs \
        wpf/tests/Dc.App.Tests/ViewModels/Workspace/WorkspaceConfigViewModelTests.cs
git commit -m ":sparkles: S3b.2: WorkspaceConfigViewModel 只读展示 + 编辑命令（5 unit tests）"
```

---

## Task 3: embeddable 接口 + TaskWorkspaceViewModel 4 tab 协调 (TDD)

**Files:**
- Create: `wpf/src/Dc.App/ViewModels/Workspace/IEmbeddableGroupPanel.cs`
- Create: `wpf/src/Dc.App/ViewModels/Workspace/IEmbeddableLivePanel.cs`
- Create: `wpf/src/Dc.App/ViewModels/Workspace/IEmbeddableDiagPanel.cs`
- Modify: `wpf/src/Dc.App/ViewModels/GroupsViewModel.cs`（实现接口）
- Modify: `wpf/src/Dc.App/ViewModels/LiveDataViewModel.cs`（实现接口）
- Modify: `wpf/src/Dc.App/ViewModels/DiagnosticsViewModel.cs`（实现接口）
- Modify: `wpf/src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs`
- Modify: `wpf/tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs`

- [ ] **Step 1: 三个最小接口**

`IEmbeddableGroupPanel.cs`:
```csharp
using System.ComponentModel;
using Dc.Domain.Entities;

namespace Dc.App.ViewModels.Workspace;

public interface IEmbeddableGroupPanel : INotifyPropertyChanged
{
    bool IsEmbedded { get; set; }
    CollectorTask? TaskFilter { get; set; }
    Group? SelectedGroup { get; }
    Task LoadAsync();
}
```

`IEmbeddableLivePanel.cs`:
```csharp
namespace Dc.App.ViewModels.Workspace;

public interface IEmbeddableLivePanel
{
    string? TaskFilter { get; set; }
}
```

`IEmbeddableDiagPanel.cs`:
```csharp
namespace Dc.App.ViewModels.Workspace;

public interface IEmbeddableDiagPanel
{
    string? TaskScope { get; set; }
}
```

- [ ] **Step 2: 让 3 个 VM 实现接口**

- `GroupsViewModel`：类声明加 `, IEmbeddableGroupPanel`（已 ObservableObject）。`IsEmbedded`/`TaskFilter`/`SelectedGroup`/`LoadAsync` 都已存在 → 接口自动满足。加 `using Dc.App.ViewModels.Workspace;`。
- `LiveDataViewModel`：类声明加 `, IEmbeddableLivePanel`。`TaskFilter`（string?）已存在 → 满足。加 using。
- `DiagnosticsViewModel`：类声明加 `, IEmbeddableDiagPanel`。`TaskScope`（Task 1 已加）→ 满足。加 using。

- [ ] **Step 3: 扩展 TaskWorkspaceViewModel**

读现有文件，ctor 追加 4 个参数：`IEmbeddableGroupPanel groupsPanel, IEmbeddableLivePanel livePanel, IEmbeddableDiagPanel diagPanel, WorkspaceConfigViewModel config`。存为属性：

```csharp
    public IEmbeddableGroupPanel GroupsPanel { get; }
    public IEmbeddableLivePanel LivePanel { get; }
    public IEmbeddableDiagPanel DiagPanel { get; }
    public WorkspaceConfigViewModel Config { get; }
```

ctor 内：
- `GroupsPanel.IsEmbedded = true;`
- 订阅分组选中联动：
  ```csharp
  GroupsPanel.PropertyChanged += (_, e) =>
  {
      if (e.PropertyName == nameof(IEmbeddableGroupPanel.SelectedGroup)
          && GroupsPanel.SelectedGroup is not null)
      {
          TagsPanel.GroupFilter = GroupsPanel.SelectedGroup;  // 见下方：IEmbeddableTagPanel 需暴露 GroupFilter
          SelectedTab = "tags";
      }
  };
  ```
- 订阅 Config 编辑完成 → 刷新：`Config.Edited += async _ => await LoadAsync();`

> **IEmbeddableTagPanel 需要加 `Group? GroupFilter { get; set; }`**：当前接口只有 IsEmbedded/TaskScope/LoadAsync。加 GroupFilter（TagsViewModel 已有该属性）。同步更新 S3a 的 FakeTagPanel 实现。

更新 `OnSelectedTaskChanged`：在现有逻辑后追加：
```csharp
    // 拿选中任务的完整 CollectorTask（从 LoadAsync 缓存的字典）
    var task = value is null ? null : _tasksById.GetValueOrDefault(value.TaskId);
    GroupsPanel.TaskFilter = task;
    _ = GroupsPanel.LoadAsync();
    LivePanel.TaskFilter = value?.TaskId;
    DiagPanel.TaskScope = value?.TaskId;
    Config.SetTask(task);
```

> 需要 `_tasksById` 缓存：在 `LoadAsync` 里构建 `Dictionary<string, CollectorTask>`。当前 LoadAsync 用 `_source.LoadTasksAsync()` 拿 `IReadOnlyList<CollectorTask>` — 加一行 `_tasksById = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);`，字段 `private Dictionary<string, CollectorTask> _tasksById = new();`。

更新 `UpdateTabContent` 加 4 个分支：
```csharp
    CurrentTabContent = SelectedTab switch
    {
        "tags"        => TagsPanel,
        "groups"      => GroupsPanel,
        "livedata"    => LivePanel,
        "diagnostics" => DiagPanel,
        "config"      => Config,
        _             => Overview
    };
```

- [ ] **Step 4: 更新测试**

`TaskWorkspaceViewModelTests` 的 `BuildWithTags()` 要补 4 个新参数。加 fakes：

```csharp
    private sealed class FakeGroupPanel : ObservableObject, IEmbeddableGroupPanel
    {
        public bool IsEmbedded { get; set; }
        private Dc.Domain.Entities.CollectorTask? _taskFilter;
        public Dc.Domain.Entities.CollectorTask? TaskFilter { get => _taskFilter; set => SetProperty(ref _taskFilter, value); }
        private Dc.Domain.Entities.Group? _selectedGroup;
        public Dc.Domain.Entities.Group? SelectedGroup { get => _selectedGroup; private set => SetProperty(ref _selectedGroup, value); }
        public int LoadCount;
        public Task LoadAsync() { LoadCount++; return Task.CompletedTask; }
        public void SimulateSelect(Dc.Domain.Entities.Group g) => SelectedGroup = g;
    }
    private sealed class FakeLivePanel : IEmbeddableLivePanel { public string? TaskFilter { get; set; } }
    private sealed class FakeDiagPanel : IEmbeddableDiagPanel { public string? TaskScope { get; set; } }
```

`FakeTagPanel` 加 `public Dc.Domain.Entities.Group? GroupFilter { get; set; }`（实现新增的接口成员）。

`BuildWithTags()` 构造 `WorkspaceConfigViewModel`（用 FakeEditor，同 Task 2 的 FakeEditor — 复制一份或提到共享）传入。返回多带 group/live/diag/config 引用以便断言。

新增测试：
```csharp
    [Fact]
    public async Task SelectingTask_ConfiguresAllPanelScopes()
    {
        var (deps, vm) = BuildFull();
        deps.Src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();

        vm.SelectedTask = vm.AllTasks[0];

        Assert.Equal("t1", deps.Live.TaskFilter);
        Assert.Equal("t1", deps.Diag.TaskScope);
        Assert.Equal("t1", deps.Group.TaskFilter?.Id);
        Assert.True(deps.Config.HasTask);
    }

    [Fact]
    public async Task SelectingGroupInGroupPanel_JumpsToTagsTabWithFilter()
    {
        var (deps, vm) = BuildFull();
        deps.Src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];

        var grp = new Dc.Domain.Entities.Group { Id = "g1", Name = "炉膛", TaskId = "t1" };
        deps.Group.SimulateSelect(grp);

        Assert.Equal("tags", vm.SelectedTab);
        Assert.Same(grp, deps.Tag.GroupFilter);
        Assert.Same(deps.Tag, vm.CurrentTabContent);
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("livedata")]
    [InlineData("diagnostics")]
    [InlineData("config")]
    public async Task SwitchingTab_SetsCurrentContent(string tab)
    {
        var (deps, vm) = BuildFull();
        deps.Src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];

        vm.SelectedTab = tab;

        object expected = tab switch
        {
            "groups" => deps.Group,
            "livedata" => deps.Live,
            "diagnostics" => deps.Diag,
            "config" => deps.Config,
            _ => deps.Overview
        };
        Assert.Same(expected, vm.CurrentTabContent);
    }
```

`BuildFull()` 返回一个 deps 容器（含 Src/Orch/Tag/Group/Live/Diag/Config/Overview）+ vm。重构 `Build()`/`BuildWithTags()` 复用它，保持已有测试编译通过。

- [ ] **Step 5: 构建 + 测试**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: 两个 0 错误。

- [ ] **Step 6: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/ wpf/tests/Dc.App.Tests/ViewModels/Workspace/
git commit -m ":sparkles: S3b.3: 工作台 4 tab 协调 + 分组→Tag 联动 + 3 embeddable 接口"
```

---

## Task 4: TaskWorkspaceView.xaml 加 4 tab + 配置面板

**Files:**
- Modify: `wpf/src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml`
- Create: `wpf/src/Dc.App/Views/Workspace/ConfigTabPanel.xaml` + `.cs`
- Modify: `wpf/src/Dc.App/App.xaml`（加 WorkspaceConfigViewModel DataTemplate）

- [ ] **Step 1: tab 单选按钮加 4 个**

读 `TaskWorkspaceView.xaml`，找到 tab 区（S3a 的 RadioButton "概览"/"Tag" + 占位 TextBlock "分组 · 实时 · 诊断 · 配置 (S3b/c)"）。把占位 TextBlock 删掉，补 4 个 RadioButton：

```xml
<RadioButton Content="概览" IsChecked="True" GroupName="tab" Margin="0,0,12,0" Checked="OnTabOverview" />
<RadioButton Content="分组" GroupName="tab" Margin="0,0,12,0" Checked="OnTabGroups" />
<RadioButton Content="Tag" GroupName="tab" Margin="0,0,12,0" Checked="OnTabTags" />
<RadioButton Content="实时数据" GroupName="tab" Margin="0,0,12,0" Checked="OnTabLive" />
<RadioButton Content="诊断" GroupName="tab" Margin="0,0,12,0" Checked="OnTabDiagnostics" />
<RadioButton Content="配置" GroupName="tab" Checked="OnTabConfig" />
```

`TaskWorkspaceView.xaml.cs` 加事件桥接：
```csharp
private void OnTabGroups(object s, System.Windows.RoutedEventArgs e)      { if (Vm is { } v) v.SelectedTab = "groups"; }
private void OnTabLive(object s, System.Windows.RoutedEventArgs e)        { if (Vm is { } v) v.SelectedTab = "livedata"; }
private void OnTabDiagnostics(object s, System.Windows.RoutedEventArgs e) { if (Vm is { } v) v.SelectedTab = "diagnostics"; }
private void OnTabConfig(object s, System.Windows.RoutedEventArgs e)      { if (Vm is { } v) v.SelectedTab = "config"; }
```

> `CurrentTabContent` 的 ContentControl 会按 runtime 类型经 App.xaml DataTemplate 解析：GroupsViewModel→GroupsView、LiveDataViewModel→LiveDataView、DiagnosticsViewModel→DiagnosticsView（均已注册）。配置面板需要新 DataTemplate（Step 3）。Overview 仍用 TaskWorkspaceView 内联 DataTemplate（S3a）。

- [ ] **Step 2: ConfigTabPanel.xaml**

`wpf/src/Dc.App/Views/Workspace/ConfigTabPanel.xaml`（只读字段 + 编辑按钮）：

```xml
<UserControl x:Class="Dc.App.Views.Workspace.ConfigTabPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
    <StackPanel>
        <ui:Card Margin="0,0,0,12">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="120" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" /><RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" /><RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" /><RowDefinition Height="Auto" />
                </Grid.RowDefinitions>
                <TextBlock Grid.Row="0" Grid.Column="0" Text="服务器" Opacity="0.6" Margin="0,4" />
                <TextBlock Grid.Row="0" Grid.Column="1" Text="{Binding Server}" Margin="0,4" />
                <TextBlock Grid.Row="1" Grid.Column="0" Text="节点" Opacity="0.6" Margin="0,4" />
                <TextBlock Grid.Row="1" Grid.Column="1" Text="{Binding Node}" Margin="0,4" TextWrapping="Wrap" />
                <TextBlock Grid.Row="2" Grid.Column="0" Text="协议" Opacity="0.6" Margin="0,4" />
                <TextBlock Grid.Row="2" Grid.Column="1" Text="{Binding ProtocolLabel}" Margin="0,4" />
                <TextBlock Grid.Row="3" Grid.Column="0" Text="TCP 地址" Opacity="0.6" Margin="0,4" />
                <TextBlock Grid.Row="3" Grid.Column="1" Text="{Binding TcpAddress}" Margin="0,4" />
                <TextBlock Grid.Row="4" Grid.Column="0" Text="采样间隔 (ms)" Opacity="0.6" Margin="0,4" />
                <TextBlock Grid.Row="4" Grid.Column="1" Text="{Binding Interval}" Margin="0,4" />
                <TextBlock Grid.Row="5" Grid.Column="0" Text="死区 (%)" Opacity="0.6" Margin="0,4" />
                <TextBlock Grid.Row="5" Grid.Column="1" Text="{Binding Deviation}" Margin="0,4" />
            </Grid>
        </ui:Card>
        <ui:Button Content="编辑任务参数" Icon="{ui:SymbolIcon Edit24}"
                   Command="{Binding EditCommand}" HorizontalAlignment="Left" />
    </StackPanel>
</UserControl>
```

`ConfigTabPanel.xaml.cs`：标准 InitializeComponent。

- [ ] **Step 3: App.xaml 加 DataTemplate**

`App.xaml` 在 workspace 区域加：
```xml
<DataTemplate DataType="{x:Type wsvm:WorkspaceConfigViewModel}">
    <wsview:ConfigTabPanel />
</DataTemplate>
```
（`wsvm`/`wsview` xmlns 已在 S3a.5 加过）

- [ ] **Step 4: 构建**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -8
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Views/Workspace/ wpf/src/Dc.App/App.xaml
git commit -m ":sparkles: S3b.4: 工作台 4 tab UI（分组/实时/诊断/配置面板）"
```

---

## Task 5: DI 注册工作台面板依赖

**Files:**
- Modify: `wpf/src/Dc.App/Composition/ServiceRegistration.cs`

- [ ] **Step 1: 注册接口映射 + 扩 TaskWorkspaceViewModel 工厂**

读现有 ServiceRegistration 里 S3a 的 TaskWorkspaceViewModel 工厂注册。`GroupsViewModel`/`LiveDataViewModel`/`DiagnosticsViewModel` 已注册为单例（确认）。加接口映射（让工作台注入接口而非具体类）：

```csharp
        services.AddSingleton<Dc.App.ViewModels.Workspace.IEmbeddableGroupPanel>(
            sp => sp.GetRequiredService<GroupsViewModel>());
        services.AddSingleton<Dc.App.ViewModels.Workspace.IEmbeddableLivePanel>(
            sp => sp.GetRequiredService<LiveDataViewModel>());
        services.AddSingleton<Dc.App.ViewModels.Workspace.IEmbeddableDiagPanel>(
            sp => sp.GetRequiredService<DiagnosticsViewModel>());
        services.AddSingleton<Dc.App.ViewModels.Workspace.WorkspaceConfigViewModel>(
            sp => new Dc.App.ViewModels.Workspace.WorkspaceConfigViewModel(
                sp.GetRequiredService<Dc.App.Services.ITaskEditorDialog>()));
```

把 TaskWorkspaceViewModel 工厂的构造调用追加 4 个参数（顺序匹配 Task 3 ctor）：
```csharp
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.IEmbeddableGroupPanel>(),
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.IEmbeddableLivePanel>(),
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.IEmbeddableDiagPanel>(),
                sp.GetRequiredService<Dc.App.ViewModels.Workspace.WorkspaceConfigViewModel>()
```

> 注意单例共享：GroupsViewModel/LiveDataViewModel/DiagnosticsViewModel 同时被「全局监控」导航路由（livedata/diagnostics）和工作台 tab 用。共享单例意味着工作台设的 TaskFilter/TaskScope 会影响全局监控视图。**这是已知耦合**——S4 做全局监控分离时用独立实例解决。S3b 阶段：在 PR 评论里标注此限制。若想立刻规避，可给工作台用独立实例（`AddTransient` 或显式 new），但会偏离"复用"目标。S3b 先共享 + 标注。

- [ ] **Step 2: 构建**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -8
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Composition/ServiceRegistration.cs
git commit -m ":wrench: S3b.5: DI 注册工作台 4 面板依赖（接口映射到现有单例）"
```

---

## Task 6: 全量回归 + push

- [ ] **Step 1: 测试 + build**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.Infrastructure.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo 2>&1 | tail -4
dotnet test tests/Dc.Integration.Tests   -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo 2>&1 | tail -4
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -3
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -3
```

Expected: Infra 48 / Integration 10 / 两个 build 0 错误。

- [ ] **Step 2: Push**

```bash
cd /home/adamyu/workspace/dc
git push origin wpf-opc-collector
```

- [ ] **Step 3: PR #5 评论**

```bash
CREDS=$(grep "git.adamyu.top" ~/.git-credentials | sed 's|https://||;s|@.*||')
cat > /tmp/s3b_comment.json <<'EOF'
{"body":"### S3b · 工作台剩余 4 tab（分组/实时/诊断/配置）完成\n\n- DiagnosticsViewModel 加 TaskScope 任务过滤\n- WorkspaceConfigViewModel 只读展示 + 编辑按钮（开 ITaskEditorDialog）\n- 3 个 embeddable 接口（Group/Live/Diag）+ 工作台 6 tab 全通\n- 分组 tab 选中分组 → 自动跳 Tag tab 并按该分组预筛\n- 选任务时 4 面板 scope 同步（GroupsPanel.TaskFilter / Live.TaskFilter / Diag.TaskScope / Config）\n\n**已知耦合**：Groups/LiveData/Diagnostics 单例同时被工作台 tab 与「全局监控」导航复用，工作台设的过滤会影响全局视图 — S4 全局监控分离时用独立实例解决。\n\n验证：Infra 48 + Integration 10 全绿；Dc.App + Tests build 0 错误；Dc.App.Tests 累计约 46。\n\nWindows walkthrough：选任务→切 6 个 tab；分组 tab 点分组跳 Tag 预筛；配置 tab 点编辑开对话框改完刷新。"}
EOF
curl -sk -u "$CREDS" -H "Content-Type: application/json" \
  -X POST "https://git.adamyu.top:20443/api/v1/repos/adamyu/dc/issues/5/comments" \
  -d @/tmp/s3b_comment.json -o /tmp/c.json -w "HTTP %{http_code}\n"
jq -r '.html_url // .message' /tmp/c.json
rm -f /tmp/s3b_comment.json /tmp/c.json
```

---

## 验收

- Infra 48 + Integration 10 全绿
- Dc.App.Tests +约 8（S3b.1 3 + S3b.2 5 + S3b.3 测试），累计约 46
- 工作台 6 tab 全部可切换且 scope 正确
- 分组→Tag 联动工作
- 配置 tab 编辑闭环
- PR #5 评论已加 S3b 段 + 已知耦合标注
