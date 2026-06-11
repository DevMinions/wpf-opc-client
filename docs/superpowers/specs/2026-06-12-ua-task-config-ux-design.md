# UA 任务配置体验修正 设计

> 2026-06-12。direction 2「打磨」第二块。修家里真 Prosys UA server 活体验证暴露的 2 个真 bug（memory `dc-ua-live-validation-findings` 第 1、4 项）。

## 目标

修正 UA 任务配置的两处体验问题：(1) 地址字段 DA 中心、占位符与映射矛盾导致 UA 用户填错；(2) 启动失败只进日志、界面无反馈。

## 背景：已探明现状

- **字段映射矛盾**：`TaskEditorWindow.xaml` 的「服务器」(Server) 字段占位符是 `DA: ProgID / UA: opc.tcp://host:port`（叫 UA 用户把 URL 填这），但 `DbTaskLauncher.ToStartRequest` 映射 `ServerUri = task.Node`（UA 的 opc.tcp URL 实际取自「节点」Node）。用户照占位符填到 Server → Node 仍是默认/主机名 → "Invalid URI"。这套 Server(ProgID)+Node(Host) 是 DA 中心模型，套到 UA 别扭。
- **启动失败无 UI**：`TaskWorkspaceViewModel.StartSelectedAsync`（src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs:232）调 `_orchestrator.StartAsync(req)`（:241）在连接失败（证书/URI/安全）时抛异常；`TaskWorkspaceView.xaml.cs` 的 `OnStart` try/catch 只 `Log.Error`，界面无任何提示。活体见过 `BadSecurityChecksFailed`/`Invalid URI` 只在日志。
- 无现成通知服务（grep 无 Snackbar/InfoBar/NotificationService）。WPF-UI **3.0.5**（含 `SnackbarPresenter`/`ISnackbarService`）。Shell 在 `src/Dc.App/Views/Shell/ShellWindow.xaml`。
- `TaskEditorViewModel`（ObservableValidator）：`[ObservableProperty]` 字段 Server/Node/Clsid/Protocol/Interval/Deviation/TcpAddress/UseSecurity；计算属性 `IsDaProtocol`/`IsClassicOpcProtocol`/`IsUaProtocol` 控制字段可见性；Server/Node 均带 `[Required]`；`ToEntity()` 用 `Id=OriginalId??""`。

## 已定决策

| 维度 | 决策 |
|---|---|
| UA 地址字段 | **UA 单一「服务器地址」字段**（绑 Node，占位符 opc.tcp://...）；DA/AE 保留 ProgID+Host 双字段 |
| UA 下 Server | **镜像 = Node**（满足 [Required]、与既有任务一致）；`DbTaskLauncher` 仍 ServerUri=Node 不变 |
| UA URL 格式校验 | **不加强校验**（YAGNI）——错地址由启动失败通知兜底，二者协同 |
| 启动失败反馈 | **非模态通知条**（WPF-UI Snackbar） |
| 通知抽象 | `INotificationService` 注入 VM（WPF 不进 VM 逻辑、可 Windows 单测） |

## 组件设计

### ① UA 单一「服务器地址」字段

- **`TaskEditorWindow.xaml`** 按协议切字段：
  - **UA**（`Visibility` 绑 `IsUaProtocol`）：单行「服务器地址」，TextBox 绑 `Node`，`Placeholder.Text="opc.tcp://host:port/path"`，`AutomationId="TaskNode"`（沿用，活体脚本不破）。
  - **DA/AE**（`Visibility` 绑 `IsClassicOpcProtocol`）：保留「服务器」(Server=ProgID，`AutomationId="TaskServer"`) + 「节点」(Node=Host) 双字段；「服务器」占位符改纯 DA 语义 `ProgID（如 Matrikon.OPC.Simulation.1）`，去掉误导的 UA 提示。
  - 注：两套字段各自绑 Server/Node，互斥可见。UA 套用的就是 Node 字段（单字段），DA 套用 Server+Node。
- **`TaskEditorViewModel`**：UA 下 `Server` 镜像 `Node`——在 `OnNodeChanged(string)` partial 方法 + `OnProtocolChanged` 里 `if (IsUaProtocol) Server = Node;`。这样 UA 隐藏的 Server 字段始终非空、`[Required]` 满足、`ToEntity()` 带出 Server=Node=URL（与既有任务一致，`DbTaskLauncher` 用 Node 不受影响）。

### ② 启动失败非模态通知

- **新 `INotificationService`**（`src/Dc.App/Services/INotificationService.cs`）：
  ```csharp
  public interface INotificationService { void ShowError(string title, string message); }
  ```
- **WPF 实现 `SnackbarNotificationService`**（`src/Dc.App/Services/SnackbarNotificationService.cs`）：包 WPF-UI `ISnackbarService`，`ShowError` 调 `Show(title, message, ControlAppearance.Danger, <icon>, TimeSpan)`（具体 API 名按 WPF-UI 3.0.5 在计划阶段核实）。
- **`ShellWindow.xaml`** 加 `<ui:SnackbarPresenter x:Name="..."/>`（叠在内容上层）；**`ShellWindow.xaml.cs`/`ServiceRegistration`** 注册 `ISnackbarService`/`INotificationService` 并 `SetSnackbarPresenter`。
- **`TaskWorkspaceViewModel`**：构造注入 `INotificationService?`（可选，默认 null-object `NullNotification` 不弹）；`StartSelectedAsync` 把 `StartAsync` 包 try/catch：
  ```csharp
  try { await _orchestrator.StartAsync(req); }
  catch (Exception ex) { _notify.ShowError("任务启动失败", ex.Message); }
  await LoadAsync();
  ```
  `OnStart` 后置的 try/catch + Log.Error 保留作兜底（双层不冲突：VM 先 catch+notify，正常不再上抛）。

**分层**：异常 message 是字符串，OPC SDK 类型不泄漏；`INotificationService` 抽象隔离 WPF。

## 错误处理

- 启动失败：notify + LoadAsync 刷新状态（任务未进运行集，行保持「已停止」）。
- 通知服务未注入（测试/Cli 不适用）：null-object 不弹，不崩。
- UA↔DA 协议切换：Server 镜像只在 UA 生效；切回 DA 时 Server 仍是上次镜像值，DA 字段可见可改（可接受，切协议罕见）。

## 测试

**Windows `Dc.App.Tests`（office/家里 dc-remote 跑）：**
1. `TaskEditorViewModel`：UA 下设 `Node` → `Server` 自动 = Node；`ToEntity().Server == Node`。
2. `TaskEditorViewModel`：协议 UA→`IsUaProtocol==true`/`IsClassicOpcProtocol==false`，Da→反之（字段可见性开关）。
3. `TaskWorkspaceViewModel.StartSelectedAsync`：orchestrator 抛异常 → `INotificationService.ShowError` 被调（fake notify 跟踪）、任务不在运行集、LoadAsync 刷新。

**家里真 Prosys UA server 活体（dc-remote，用新帮手）：**
- `app-launch` 起 Prosys；新建 UA 任务，UA 编辑器只一个「服务器地址」字段，填 opc.tcp URL → 启动连上（state=running）。
- 故意填错地址（如漏端口）启动 → `shot-full` 截到非模态通知条「任务启动失败：...」。
- DA/AE 编辑器仍显示 服务器(ProgID)+节点(Host) 双字段，占位符无 UA 误导。

## 范围外（YAGNI）

- opc.tcp URL 格式强校验——错地址由启动通知兜底。
- 通用通知中心/历史——仅 start-failure 用 ShowError，够用。
- 成功/信息类通知——本特性只做错误反馈。
- DA/AE 字段重构——只去掉 Server 占位符的 UA 误导文案，不动 DA 双字段模型。
