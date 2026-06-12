# UA 任务配置体验修正 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 修 UA 任务地址字段映射矛盾（UA 单一「服务器地址」字段）+ 启动失败非模态通知反馈。

**架构：** ① 编辑器按协议切字段（UA 单字段绑 Node、DA/AE 保留 Server+Node），VM 下 UA 时 Server 镜像 Node。② `INotificationService`（WPF-UI Snackbar 实现）注入 `TaskWorkspaceViewModel`，`StartSelectedAsync` try/catch 调 `ShowError`。

**技术栈：** WPF（net8.0-windows）、CommunityToolkit.Mvvm（[ObservableProperty] 源生成）、WPF-UI 3.0.5（SnackbarService）、xUnit。

---

## 关键事实（已核实，实现时遵守）

- **本机 Linux 不能编译/跑 Dc.App / Dc.App.Tests**（net8.0-windows）。任务实现+提交照做，**不在本机编译/跑测**；编译与单测攒到任务 6 经 dc-remote 在家里 Windows 跑（`dc-remote test <csproj>` 已自带 `-p:Platform=x64 -p:CustomTestTarget=net8.0-windows`）。TDD 测试先写（Windows 阶段才真跑）。
- **编辑器 Grid 行高固定**（`TaskEditorWindow.xaml:55-62`：Row0/1/2=36px、Row3/4=Auto、Row5=36px）。UA 隐藏 Server 行需把 **Row1 改 `Height="Auto"`**（collapse 内容后 0 高，否则留 36px 空隙）。
- `controls:Placeholder.Text`（`Dc.App.Controls.Placeholder`，`xmlns:controls="clr-namespace:Dc.App.Controls"`）是 **RegisterAttached DependencyProperty → 可绑定**。
- 转换器 `BoolToVis`（`BooleanToVisibilityConverter`）在 `TaskEditorWindow.xaml:13` 本地声明；`IsUaProtocol`/`IsClassicOpcProtocol` 计算属性已在 `TaskEditorViewModel`。
- **`TaskOrchestrator` 是 sealed class**（非接口），VM 字段 `TaskOrchestrator? _orchestrator`。构造 `TaskOrchestrator(IEnumerable<IOpcSubscriberFactory> factories, Func<DateTimeOffset>? clock = null, OrchestratorOptions? options = null, ILogger? logger = null)`。**`StartAsync` 在协议无注册 factory 时抛 `InvalidOperationException`**（`TaskOrchestrator.cs:104`）→ 用**空 factory 列表**构造真 orchestrator 即可让 StartAsync 抛，无需 fake subscriber。
- **WPF-UI 3.0.5 Snackbar API**（context7 核实）：`Wpf.Ui.ISnackbarService { void SetSnackbarPresenter(SnackbarPresenter); void Show(string title, string message, ControlAppearance appearance, IconElement? icon, TimeSpan timeout); TimeSpan DefaultTimeOut {get;set;} }`；具体类 `Wpf.Ui.SnackbarService`；`Wpf.Ui.Controls.ControlAppearance.Danger`；XAML 元素 `<ui:SnackbarPresenter>`（`xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"`）。icon 可传 `null`。
- `ShellWindow.xaml` 是 `ui:FluentWindow` + 根 `<Grid>`（3 行：Auto/* /28）。
- `TaskWorkspaceViewModel` 既有可选注入模式（`ITaskEditorDialog? editor=null`、`IConfirmDialog? confirm=null` + 内部 null-object）——新 `INotificationService?` 照此加在构造末尾。
- 测试 `TaskWorkspaceViewModelTests` 有 `BuildVm(FakeTaskSource src, ITaskEditorDialog? editor=null, IConfirmDialog? confirm=null)`（自建 orch=null）和 `BuildFull()`；`Task1(id, server="炉温", type=2)` helper（type=2=UA）。

## 文件结构

**编辑器（① UA 字段）**
- 修改 `src/Dc.App/ViewModels/TaskEditorViewModel.cs` — NodeLabel/NodePlaceholder 计算属性 + UA Server 镜像。
- 修改 `src/Dc.App/Views/TaskEditorWindow.xaml` — 按协议切字段。

**通知（② 启动失败反馈）**
- 创建 `src/Dc.App/Services/INotificationService.cs`
- 创建 `src/Dc.App/Services/SnackbarNotificationService.cs`
- 修改 `src/Dc.App/Views/Shell/ShellWindow.xaml` — 加 SnackbarPresenter
- 修改 `src/Dc.App/Composition/ServiceRegistration.cs` — 注册 + SetSnackbarPresenter + 传入 VM
- 修改 `src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs` — 注入 + StartSelectedAsync try/catch

**测试**
- 修改 `tests/Dc.App.Tests/ViewModels/TaskEditorViewModelTests.cs`
- 修改 `tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs`

---

### 任务 1：TaskEditorViewModel — NodeLabel/NodePlaceholder + UA Server 镜像

**文件：**
- 修改：`src/Dc.App/ViewModels/TaskEditorViewModel.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/TaskEditorViewModelTests.cs`

- [ ] **步骤 1：写失败测试**（Windows 阶段才跑；先写）

在 `TaskEditorViewModelTests.cs` 加：

```csharp
    [Fact]
    public void Ua_SettingNode_MirrorsToServer()
    {
        var vm = new TaskEditorViewModel();          // 默认 Ua
        vm.Node = "opc.tcp://host:4840/x";
        Assert.Equal("opc.tcp://host:4840/x", vm.Server);
        Assert.Equal("opc.tcp://host:4840/x", vm.ToEntity().Server);
    }

    [Fact]
    public void Da_SettingNode_DoesNotMirrorToServer()
    {
        var vm = new TaskEditorViewModel { Protocol = OpcProtocol.Da };
        vm.Server = "Matrikon.OPC.Simulation.1";
        vm.Node = "192.168.1.10";
        Assert.Equal("Matrikon.OPC.Simulation.1", vm.Server);   // DA 不镜像
    }

    [Fact]
    public void NodeLabel_And_Placeholder_TrackProtocol()
    {
        var vm = new TaskEditorViewModel();          // Ua
        Assert.Equal("服务器地址:", vm.NodeLabel);
        Assert.Contains("opc.tcp", vm.NodePlaceholder);
        vm.Protocol = OpcProtocol.Da;
        Assert.Equal("节点:", vm.NodeLabel);
        Assert.DoesNotContain("opc.tcp", vm.NodePlaceholder);
    }
```

- [ ] **步骤 2：跑测试确认失败**（Windows）—— VM 无 NodeLabel/NodePlaceholder、Ua 不镜像。本机跳过实跑。

- [ ] **步骤 3：实现**

`TaskEditorViewModel.cs`：
1. 加计算属性（放 `IsUaProtocol` 旁）：

```csharp
    public string NodeLabel => IsUaProtocol ? "服务器地址:" : "节点:";
    public string NodePlaceholder => IsUaProtocol ? "opc.tcp://host:port/path" : "主机名或 IP";
```

2. 加 `OnNodeChanged` partial（CommunityToolkit 源生成会在 Node 变化时调）——UA 镜像 Server：

```csharp
    partial void OnNodeChanged(string value)
    {
        if (IsUaProtocol) Server = value;
    }
```

3. `OnProtocolChanged`（已存在，现通知 IsDaProtocol/IsClassicOpcProtocol/IsUaProtocol）补通知 + 切到 UA 时立即镜像：

```csharp
    partial void OnProtocolChanged(OpcProtocol value)
    {
        OnPropertyChanged(nameof(IsDaProtocol));
        OnPropertyChanged(nameof(IsClassicOpcProtocol));
        OnPropertyChanged(nameof(IsUaProtocol));
        OnPropertyChanged(nameof(NodeLabel));
        OnPropertyChanged(nameof(NodePlaceholder));
        if (IsUaProtocol) Server = Node;
    }
```

（保留该方法现有的三行 OnPropertyChanged，只新增 NodeLabel/NodePlaceholder 两行 + 镜像。）

- [ ] **步骤 4：跑测试确认通过**（Windows 阶段，任务 6）。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/ViewModels/TaskEditorViewModel.cs tests/Dc.App.Tests/ViewModels/TaskEditorViewModelTests.cs
git commit -m "✨ feat(task): 编辑器 UA 下 Server 镜像 Node + NodeLabel/Placeholder 按协议切"
```

---

### 任务 2：TaskEditorWindow.xaml — 按协议切字段

**文件：**
- 修改：`src/Dc.App/Views/TaskEditorWindow.xaml:56`（Row1 行高）、`:73-81`（服务器/节点字段）

> 无单测（纯 XAML），验证 = Windows 编译 + 任务 6 活体。

- [ ] **步骤 1：Row1 行高改 Auto**

`TaskEditorWindow.xaml` 第 56 行（第二个 RowDefinition，对应 Row1 服务器行）：
```xml
<RowDefinition Height="Auto" />
```
（原 `Height="36"` → `Height="Auto"`，UA 隐藏 Server 后该行 collapse 到 0 不留空隙。）

- [ ] **步骤 2：服务器字段仅 DA/AE 可见 + 占位符改纯 DA 语义**

替换第 73-76 行的 服务器 TextBlock + TextBox：
```xml
                        <TextBlock Grid.Row="1" Grid.Column="0" Text="服务器:"
                                   Visibility="{Binding IsClassicOpcProtocol, Converter={StaticResource BoolToVis}}"
                                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                        <TextBox Grid.Row="1" Grid.Column="1" AutomationProperties.AutomationId="TaskServer" Text="{Binding Server, UpdateSourceTrigger=PropertyChanged}"
                                 Visibility="{Binding IsClassicOpcProtocol, Converter={StaticResource BoolToVis}}"
                                 controls:Placeholder.Text="ProgID（如 Matrikon.OPC.Simulation.1）" />
```

- [ ] **步骤 3：节点字段标签/占位符按协议切**

替换第 78-81 行的 节点 TextBlock + TextBox（label/placeholder 改绑 VM 计算属性，UA 时显示「服务器地址 / opc.tcp://...」）：
```xml
                        <TextBlock Grid.Row="2" Grid.Column="0" Text="{Binding NodeLabel}"
                                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                        <TextBox Grid.Row="2" Grid.Column="1" AutomationProperties.AutomationId="TaskNode" Text="{Binding Node, UpdateSourceTrigger=PropertyChanged}"
                                 controls:Placeholder.Text="{Binding NodePlaceholder}" />
```
（AutomationId `TaskServer`/`TaskNode` 沿用——活体脚本按这俩点击/填值，别改。）

- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/Views/TaskEditorWindow.xaml
git commit -m "✨ feat(ui): 编辑器 UA 单字段(服务器地址→Node)、DA/AE 保留双字段"
```

---

### 任务 3：INotificationService + SnackbarNotificationService

**文件：**
- 创建：`src/Dc.App/Services/INotificationService.cs`
- 创建：`src/Dc.App/Services/SnackbarNotificationService.cs`

> 无单测（薄 WPF 包装），验证 = 编译 + 活体。仿 `ITaskEditorDialog`/`IConfirmDialog` 模式。

- [ ] **步骤 1：建接口**

`src/Dc.App/Services/INotificationService.cs`：
```csharp
namespace Dc.App.Services;

public interface INotificationService
{
    /// <summary>Show a non-modal error notification (toast).</summary>
    void ShowError(string title, string message);
}
```

- [ ] **步骤 2：建 WPF-UI Snackbar 实现**

`src/Dc.App/Services/SnackbarNotificationService.cs`：
```csharp
using System;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Dc.App.Services;

// 包 WPF-UI ISnackbarService;非模态 toast。SetSnackbarPresenter 在 ShellWindow 加载后调（见任务 4）。
public sealed class SnackbarNotificationService : INotificationService
{
    private readonly ISnackbarService _snackbar;

    public SnackbarNotificationService(ISnackbarService snackbar) => _snackbar = snackbar;

    public void ShowError(string title, string message) =>
        _snackbar.Show(title, message, ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
}
```

- [ ] **步骤 3：Commit**

```bash
git add src/Dc.App/Services/INotificationService.cs src/Dc.App/Services/SnackbarNotificationService.cs
git commit -m "✨ feat(ui): INotificationService + WPF-UI Snackbar 实现"
```

---

### 任务 4：ShellWindow SnackbarPresenter + DI 接线

**文件：**
- 修改：`src/Dc.App/Views/Shell/ShellWindow.xaml`（加 SnackbarPresenter）
- 修改：`src/Dc.App/Composition/ServiceRegistration.cs`（注册 + SetSnackbarPresenter + 传入 VM）

> 无单测，验证 = 编译 + 活体。实现者先读这两文件现状。

- [ ] **步骤 1：ShellWindow 加 SnackbarPresenter**

`ShellWindow.xaml` 根 `<Grid>` 内、最后一个子元素之后（让它浮在最上层）加（跨全部行）：
```xml
        <ui:SnackbarPresenter x:Name="RootSnackbarPresenter" Grid.RowSpan="3"
                              VerticalAlignment="Bottom" Margin="0,0,0,40" />
```
（核实根 Grid 的 RowDefinitions 数量与 `Grid.RowSpan` 一致——读到是 3 行则 RowSpan="3"；若结构不同按实际调。）

- [ ] **步骤 2：DI 注册 + SetSnackbarPresenter + 传入 VM**

`ServiceRegistration.cs`（先读现状确认注册风格与 ShellWindow 构造时机）：
1. 注册（仿 ITaskEditorDialog 那行）：
```csharp
services.AddSingleton<Wpf.Ui.ISnackbarService, Wpf.Ui.SnackbarService>();
services.AddSingleton<Dc.App.Services.INotificationService, Dc.App.Services.SnackbarNotificationService>();
```
2. `TaskWorkspaceViewModel` 工厂构造处（任务 8 同款 `sp => new TaskWorkspaceViewModel(...)`）传入末尾新形参：
```csharp
notify: sp.GetRequiredService<Dc.App.Services.INotificationService>()
```
3. **SetSnackbarPresenter**：ShellWindow 创建后、显示前，把 `RootSnackbarPresenter` 绑到 service。核实 ShellWindow 怎么被构造/显示（`App.xaml.cs` 或 ServiceRegistration 解析 ShellWindow 的地方）：拿到 ShellWindow 实例后调
```csharp
sp.GetRequiredService<Wpf.Ui.ISnackbarService>().SetSnackbarPresenter(shell.RootSnackbarPresenter);
```
（`RootSnackbarPresenter` 是任务 4 步骤 1 给的 `x:Name`，code-behind 可访问。若 ShellWindow 在 DI 外 new，则在其构造/Loaded 里取 service 调 SetSnackbarPresenter。实现者按实际接线点落实，保证 Show 前 presenter 已设——否则 SnackbarService.Show 抛 InvalidOperationException。）

- [ ] **步骤 3：Commit**

```bash
git add src/Dc.App/Views/Shell/ShellWindow.xaml src/Dc.App/Composition/ServiceRegistration.cs
git commit -m "✨ feat(ui): ShellWindow SnackbarPresenter + Snackbar/Notification DI 接线"
```

---

### 任务 5：TaskWorkspaceViewModel — StartSelectedAsync 失败通知

**文件：**
- 修改：`src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs`

- [ ] **步骤 1：写失败测试**（Windows 阶段跑；先写）

`TaskWorkspaceViewModelTests.cs`：
1. 加 fake：
```csharp
    private sealed class FakeNotification : Dc.App.Services.INotificationService
    {
        public List<(string Title, string Message)> Errors { get; } = new();
        public void ShowError(string title, string message) => Errors.Add((title, message));
    }
```
2. 扩 `BuildVm` 加可选 `orchestrator`/`notify` 形参（**读现有 BuildVm 签名照加，不破坏现有用例**；现签名约 `BuildVm(FakeTaskSource src, ITaskEditorDialog? editor=null, IConfirmDialog? confirm=null)`）：加 `TaskOrchestrator? orchestrator = null, Dc.App.Services.INotificationService? notify = null`，并在内部 `new TaskWorkspaceViewModel(...)` 把 `orchestrator: orchestrator` / `notify: notify` 传进去（替换原写死的 `orchestrator: null`）。
3. 用例（空 factory 的真 orchestrator → StartAsync 抛「协议未注册」→ VM catch → ShowError）：
```csharp
    [Fact]
    public async Task StartSelected_WhenOrchestratorThrows_ShowsErrorNotification()
    {
        var src = new FakeTaskSource { Tasks = { Task1("a") } };       // type=2 Ua
        var notify = new FakeNotification();
        await using var orch = new TaskOrchestrator(Array.Empty<Dc.Opc.Abstractions.IOpcSubscriberFactory>());
        var vm = BuildVm(src, orchestrator: orch, notify: notify);
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks.First();
        await vm.StartSelectedAsync();
        Assert.Single(notify.Errors);
        Assert.Equal("任务启动失败", notify.Errors[0].Title);
        Assert.False(vm.AllTasks.First().IsRunning);
    }
```
（`using Dc.Infrastructure.Orchestration;` 已在测试文件 —— 现有 FakeOrchView 同命名空间引用过 TaskDiagnostics；缺则加。）

- [ ] **步骤 2：跑测试确认失败**（Windows）—— VM 构造无 notify 形参 / StartSelectedAsync 未捕获通知。本机跳过实跑。

- [ ] **步骤 3：实现**

`TaskWorkspaceViewModel.cs`：
1. 加字段 + 构造末尾形参 + null-object（仿 `_confirm`/`DenyConfirm`）：
```csharp
    private readonly Dc.App.Services.INotificationService _notify;
```
构造形参末尾加 `Dc.App.Services.INotificationService? notify = null`，体内 `_notify = notify ?? new NullNotification();`，并加私有类：
```csharp
    private sealed class NullNotification : Dc.App.Services.INotificationService
    {
        public void ShowError(string title, string message) { }
    }
```
2. `StartSelectedAsync` 包 try/catch（替换现有 `await _orchestrator.StartAsync(req); await LoadAsync();`）：
```csharp
        try
        {
            await _orchestrator.StartAsync(req);
        }
        catch (Exception ex)
        {
            _notify.ShowError("任务启动失败", ex.Message);
        }
        await LoadAsync();
```

- [ ] **步骤 4：跑测试确认通过**（Windows 阶段，任务 6）。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs
git commit -m "✨ feat(task): 启动失败弹非模态通知(注入 INotificationService)"
```

---

### 任务 6：全量验证 + 家里活体复验

**文件：** 无（验证）

- [ ] **步骤 1：Windows 编译**（dc-remote 家里）

`dc-remote.sh auto build src/Dc.App/Dc.App.csproj`，预期 0 错误。

- [ ] **步骤 2：Windows 单测**（dc-remote）

`dc-remote.sh auto test tests/Dc.App.Tests/Dc.App.Tests.csproj`，预期全绿（含任务 1 的 3 + 任务 5 的 1 新用例 + 既有无回归）。

- [ ] **步骤 3：家里真 Prosys UA server 活体复验**（dc-remote 新帮手）

1. `app-launch 'D:\Program Files\ProsysOPC\Prosys OPC UA Simulation Server\UaSimulationServer.exe'` 起模拟器。
2. `run` 起 App，新建 UA 任务：编辑器**只一个「服务器地址」字段**（无「服务器」行），填 `opc.tcp://DESKTOP-KONUSAK:53530/OPCUA/SimulationServer`、取消「使用安全连接」→ 保存 → 启动 → `/metrics` state=running。
3. 编辑该任务，故意把地址改错（如删端口 `opc.tcp://DESKTOP-KONUSAK/OPCUA/SimulationServer`）→ 启动 → `shot-full` 截到非模态通知条「任务启动失败：...」。
4. 新建/编辑一个 DA 或 AE 任务：编辑器仍显示「服务器」(ProgID)+「节点」(Host) 双字段，服务器占位符无 UA 误导文案。
5. `psh` 直查 DB 或 `/metrics` 佐证。

- [ ] **步骤 4：更新 memory**

`dc-ua-live-validation-findings` 把第 1（URL 字段）、4（启动失败无 UI）项标「已修复（feat/ua-task-config-ux）」。

---

## 自检结果

**规格覆盖度**：① UA 单字段→任务 1（VM 镜像+label/placeholder）+任务 2（XAML）；② 启动失败通知→任务 3（接口+Snackbar）+任务 4（Shell+DI）+任务 5（VM try/catch）。测试→任务 1（VM 镜像/可见性）、任务 5（失败→ShowError）、任务 6（编译+单测+活体）。全覆盖。

**占位符扫描**：无 TODO/待定；所有步骤含完整代码。几处「核实」（ShellWindow 根 Grid 行数/RowSpan、ServiceRegistration 注册风格与 SetSnackbarPresenter 接线点、BuildVm 现签名）是必要的现状确认点，已指明确切位置与回退。

**类型一致性**：`INotificationService.ShowError(title,message)`、`SnackbarNotificationService`、`NullNotification`/`FakeNotification`、`NodeLabel`/`NodePlaceholder`、`IsUaProtocol`/`IsClassicOpcProtocol`、`TaskOrchestrator(IEnumerable<IOpcSubscriberFactory>)` 跨任务一致。WPF-UI API（`ISnackbarService.Show(...)`/`SetSnackbarPresenter`/`ControlAppearance.Danger`/`SnackbarPresenter`）按 context7 核实。

**已知边界**：全特性在 net8.0-windows，Linux 无单测——VM 逻辑（镜像、失败通知）靠 Windows 单测 + 活体；XAML/Snackbar 靠编译 + 活体。AutomationId TaskServer/TaskNode 沿用保活体脚本不破。
