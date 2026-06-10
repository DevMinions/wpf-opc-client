# 空状态 + 引导体系 设计规格

- 日期：2026-06-10
- 范围：Dc.App（WPF）。新增可复用 `EmptyState` 控件，接入 4 个页面，用上下文 CTA 串起配置引导链。
- 目标用户视角：工程师配置为主、偏一次性 —— 降低上手门槛、空界面给"下一步"指引。
- 对应审查项：🔴1（全线空状态零引导）、🔴2（新建任务主操作权重低）、🔴3（浏览连接反馈弱）、🟡8（实时/诊断空状态缺说明）。

## 1. 目标与非目标

**目标**
- 各页空数据时不再是裸空表格，而是"图标 + 标题 + 说明 + 主操作 CTA"的空状态。
- 4 个页面的 CTA 指向流程下一步，隐式串起 **① 浏览连接 → ② 新建任务 → ③ 看数据**。
- 浏览节点的连接状态（未连接/连接中/失败）变醒目。

**非目标（YAGNI，本期不做）**
- 不做集中式首次启动引导浮层（方案 B）。
- 不重构新建任务为向导（方案 C）。
- 不改任务采集/发布等业务逻辑。

## 2. 组件设计：`EmptyState` 控件

- 文件：`src/Dc.App/Controls/EmptyState.cs`，`public sealed class EmptyState : Control`。
- 默认 `ControlTemplate` 与配套样式放进 `src/Dc.App/Theme/Tokens.xaml`（沿用"样式集中"约定）。
- 依赖属性：

  | 属性 | 类型 | 默认 | 说明 |
  |---|---|---|---|
  | `Icon` | `Wpf.Ui.Controls.SymbolRegular` | `Info24` | 顶部图标，与侧栏同一套 FluentSystemIcons |
  | `Title` | `string` | `""` | 主标题 |
  | `Hint` | `string` | `""` | 一行说明 |
  | `ActionText` | `string?` | `null` | 主按钮文案；为 null/空时不显示按钮 |
  | `ActionCommand` | `ICommand?` | `null` | 按钮命令 |

- 模板视觉：垂直 + 水平居中的 StackPanel —— `ui:SymbolIcon`（约 48px，`TextFillColorTertiaryBrush` 弱化色）/ `Title`（`DcCardHeader`）/ `Hint`（`DcMuted`）/ 按钮（`DcBtnPrimary`，`Visibility` 绑 `ActionText` 是否为空）。
- 主题感知：全部用 Tokens/wpf-ui 的 DynamicResource 画刷，亮/暗自动适配。
- 职责单一：只展示空状态 + 触发一个命令，零业务逻辑，可独立测试与复用。

## 3. 接入点、文案与数据流

### 3.1 绑定辅助
- 新增 `src/Dc.App/Views/Converters/CountToVisibilityConverter.cs`：`int 0 → Visible，非 0 → Collapsed`（带 `Invert` 反向用法）。`ObservableCollection.Count` 变更会触发绑定刷新，故无需改 VM 集合结构。
- 复用现有 `BoolToVisibilityConverter`（App.xaml 已注册 `BoolToVis`）处理 Browse 的 `Connected`。

### 3.2 各页接入

| 页面 / View | 空状态显示条件 | 文案（Title / Hint） | CTA → 命令 |
|---|---|---|---|
| 采集任务 `TaskWorkspaceView` | `AllTasks.Count == 0` | 还没有采集任务 / 新建任务，连接 OPC 服务器开始采集 | **+ 新建任务** → 新增 `NewTaskCommand`（见下）|
| 浏览节点 `BrowseView` | `!Connected && !IsLoading` | 未连接到 OPC 服务器 / 填入端点地址，点连接浏览地址空间 | **连接** → 现有 `ConnectCommand` ✅ |
| 实时数据 `LiveDataView` | `Rows.Count == 0`（仅独立页，`ShowNavigateCta`） | 暂无实时数据 / 运行采集任务后这里实时显示订阅值 | **去采集任务** → `NavigateToWorkspaceCommand`（见 3.3）|
| 诊断 `DiagnosticsView` | `Rows.Count == 0`（仅独立页，`ShowNavigateCta`） | 暂无运行中的任务 / 启动采集任务后显示运行诊断 | **去采集任务** → `NavigateToWorkspaceCommand`（见 3.3）|

> 新建任务命令：现有 `TaskWorkspaceView` 的"+ 新建"按钮走 code-behind `Click="OnNewTask"`（调 `Vm.NewTaskAsync()`），**没有可绑命令**。给 `NewTaskAsync` 加 `[RelayCommand]` 生成 `NewTaskCommand`，EmptyState CTA 与顶部按钮统一绑它（顶部按钮可顺带从 Click 迁到命令）。

- 接入方式：每页在内容区（DataGrid/表格所在容器）之上叠加一个 `EmptyState`，用上表条件控制 `Visibility`；空状态显示时内容区隐藏。
- **采集任务**：空状态覆盖右侧"选择一个任务"+ tab 区，把 [+ 新建任务] 作为居中主 CTA（解决🔴2"按钮缩在左下角"）。

### 3.3 LiveData/Diagnostics 的双用上下文
- 这两个 VM 既作独立导航页、又作采集任务页内嵌 tab（`IEmbeddableLivePanel`/`IEmbeddableDiagPanel`）。
- 新增只读属性 `bool ShowNavigateCta`：独立页实例（导航路由）构造时置 `true`；内嵌实例置 `false`。
- 内嵌实例空状态只显示说明文案、不显示"去采集任务"CTA（已在任务上下文内，导航无意义）。
- **导航机制**：`INavigationService` 只有 `Resolve`，**无 `Navigate`**；实际切页是 `ShellViewModel.NavigateCommand.Execute(key)`。故给独立页 VM 注入一个导航委托 `Action<string> navigate`，`NavigateToWorkspaceCommand` 调 `navigate("workspace")`。ServiceRegistration 构造独立实例时传 `key => sp.GetRequiredService<ShellViewModel>().NavigateCommand.Execute(key)`（lambda 内惰性解析 ShellViewModel，避免构造期耦合/环）。

### 3.4 浏览节点连接反馈（🔴3）
- 未连接：显示 EmptyState（[连接] CTA，绑现有 `ConnectCommand`）。
- 连接中：`IsLoading == true` → 进度指示（`ui:ProgressRing` 或文字"连接中…"），隐藏 EmptyState。
- 失败：`StatusMessage` 已承载 `"连接失败: {ex.Message}"`（现状只在右上角小灰字）。给 `BrowseViewModel` 加显式 `[ObservableProperty] bool _isConnectError`（连接 catch 块置 true、开始连接/成功置 false），工具栏下方 `DcAlertBad` 内联条 `Visibility` 绑 `IsConnectError`、文本绑 `StatusMessage`。不靠字符串前缀嗅探。

## 4. 错误处理与边界
- `EmptyState` 不抛业务异常；`ActionCommand` 为 null 时按钮不显示也不报错。
- `CountToVisibilityConverter` 对 null/非 int 输入按"空"（Visible）兜底，不抛。
- 空状态与内容区互斥显示：任一时刻只一个可见，避免叠加遮挡。
- 不改变任何采集/连接业务行为，纯展示层。

## 5. 测试方案
- **转换器单测**（`Dc.App.Tests`）：`CountToVisibilityConverter` 对 0/正数/null/Invert 的输出。
- **VM 命令单测**：
  - `LiveDataViewModel`/`DiagnosticsViewModel` 独立实例 `ShowNavigateCta == true`、内嵌实例 `== false`；`NavigateToWorkspaceCommand` 调用注入的导航委托并传 `"workspace"`（用捕获 lambda 断言收到的 key）。
  - `TaskWorkspaceViewModel.NewTaskCommand` 存在且可执行（复用现有 editor mock，确认调到 `NewTaskAsync` 路径）。
  - Browse CTA 绑定既有 `ConnectCommand`（无需新测）。
- **视觉验证**（dc-remote）：对 4 页空状态 `shot` 截图，人工/对比确认；首次无任务时走一遍 ① 浏览连接 → ② 新建任务 → ③ 看数据 的 CTA 链路。
- 全量回归：`dc-remote home test`（Dc.App.Tests 当前 71 通过，新增测试后应仍全绿）。

## 6. 涉及文件
- 新增：`Controls/EmptyState.cs`、`Views/Converters/CountToVisibilityConverter.cs`
- 修改：
  - `Theme/Tokens.xaml`（EmptyState 模板 + 样式）、`App.xaml`（注册 `CountToVisibility` 转换器）
  - `Views/Workspace/TaskWorkspaceView.xaml`（空状态覆盖 + CTA 绑 NewTaskCommand）、`ViewModels/Workspace/TaskWorkspaceViewModel.cs`（`NewTaskAsync` 加 `[RelayCommand]`）
  - `Views/BrowseView.xaml`（空状态 + 失败内联条）
  - `Views/LiveDataView.xaml`、`ViewModels/LiveDataViewModel.cs`（`ShowNavigateCta` + `NavigateToWorkspaceCommand` + 注入导航委托）
  - `Views/DiagnosticsView.xaml`、`ViewModels/DiagnosticsViewModel.cs`（同上）
  - `Composition/ServiceRegistration.cs`（独立 LiveData/Diag 实例传 `ShowNavigateCta=true` + 导航委托；内嵌实例传 false）
- 新增测试：`Dc.App.Tests` 下转换器与 VM 测试

## 7. 验收标准
- 4 页在空数据下显示对应 EmptyState 与正确 CTA；CTA 触发既有命令/导航。
- 浏览节点未连接/连接中/失败三态清晰可辨，失败信息醒目（红色内联条）。
- 内嵌 tab 不显示"去采集任务"CTA。
- 亮/暗主题下空状态显示正常。
- 全量测试通过；4 页空状态截图复核通过。
