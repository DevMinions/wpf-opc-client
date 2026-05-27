# Dc UI 重新设计 — Win11 Fluent / Mica · 采集任务 master-detail

**状态**：设计稿，待评审
**作者**：Claude Opus 4.7（与 @adamyu 协同 brainstorming）
**日期**：2026-05-19
**关联分支**：`wpf-opc-collector`
**PR**：#5（WIP）

---

## 1. 目标与范围

### 1.1 为什么要重做

当前 WPF UI 存在四个被确认的痛点：

- **视觉老**：默认 WPF 控件直接出场，像 2000 年代的 Win32 软件
- **布局差**：侧边栏 + 表格 × 8，每个 view 都是孤立表格，信息架构混乱
- **交互差**：增删改都开对话框，多步骤完成一件事
- **主题缺失**：没有暗色 / 个性化，与 VSCode / Notion / Arco 这种现代软件不一致

此外还有一个**信息架构问题**：Tasks / Groups / Tag 在侧栏平铺，但三者是 `任务 → 分组 → Tag` 的父子关系，分开摆等于强迫用户在三个 view 之间反复跳，工作流割裂。

### 1.2 设计语言

- **视觉风格**：Win11 Fluent / Mica（与系统视觉一致，原生现代感）
- **控件库**：`WPF-UI`（lepoco/wpfui，MIT，社区活跃，原生 Fluent + Mica）
- **配色**：Win11 系统强调色（accent #0067C0）+ 语义色（good / warn / bad）
- **字体**：Segoe UI Variable（fallback PingFang SC / Microsoft YaHei）
- **暗色**：作为可选主题（亮 / 暗 / 跟随系统三档），不强制
- **风格选定**：C 暗色（仪表盘） + C2 master-detail（采集任务）

### 1.3 范围（本 spec）

- **新 Shell**：`FluentWindow` + `NavigationView`（7 项导航）+ StatusBar
- **新 Dashboard**（C 风格，仪表盘）：健康度大数字、当前告警、6 环形指标、任务速率
- **新「采集任务」工作台**（C2 风格，master-detail）：任务列表 + 选中任务的 tabs（概览 / 分组 / Tag / 实时数据 / 诊断 / 配置）
- **ThemeService**：亮 / 暗 / 跟随系统切换
- **NotifyIcon 切换**：`H.NotifyIcon.Wpf` → `WPF-UI.Tray`，保持系统托盘 + 单实例

### 1.4 不在本 spec（后续 spec / 后续 phase）

- 「浏览节点」「全局监控」（实时数据 / 诊断）「设置」「日志」四个 view 的视觉迁移
- Phase 9 运维加强（错误明细 / Telemetry / 告警规则 / 趋势图 KPI）— 暂搁
- 国际化 i18n
- Windows 端端到端验证（属于另一条 Phase 7 打包路线）

### 1.5 不目标

- **不重做后端**：所有 ViewModel 后面挂的 service / TaskOrchestrator / EF Core / 消息层完全不动
- **不破坏现有数据**：sqlite.db 表结构、`dc_` 前缀、entity 字段全部保留
- **不一次性迁完**：试金石 → 逐 View 迁，过程中允许新旧 View 共存（视觉混搭一段时间可接受）

---

## 2. 信息架构

### 2.1 侧栏结构

旧（8 项平铺）：
```
任务 / 分组 / Tag / 实时数据 / 诊断 / 浏览节点 / 设置 / 日志
```

新（7 项，分组）：
```
仪表盘
─────────
采集任务      ← 任务-分组-Tag 合并为单一入口，进入后是 master-detail 工作台
浏览节点      ← OPC 服务器节点浏览，与任务无关，保留独立
─────────
全局监控
  实时数据    ← 跨任务大屏，同时盯多个任务
  诊断        ← 系统级运行状态
─────────
系统
  设置
  日志
─────────
关于（footer）
```

### 2.2 「采集任务」工作台（master-detail）

进入后版式：

```
┌─ 面包屑：采集任务 › 1号炉温度 › Tag ──────────────────────┐
├──────────┬──────────────────────────────────────────────┤
│ 任务列表  │ ┌─ 选中任务标题 + 启停按钮 ────────────────┐  │
│ □ task1  │ ├─ tabs：概览 / 分组 / Tag / 实时 / 诊断 / 配置 │  │
│ ■ task2  │ ├─ tab 内容（当前 tab=Tag）              │  │
│ □ task3  │ │   摘要 4 卡 + 工具栏 + 数据表           │  │
│ + 新建    │ │                                       │  │
│ ⥯ 导入   │ │                                       │  │
└──────────┴────────────────────────────────────────────┘
```

**Tab 内容**（详细设计见各自子 spec，本 spec 只定义 tab 列表与 placeholder）：

| Tab | 内容（North Star） | 数据源 |
|---|---|---|
| **概览** | 任务级 KPI（启动时间 / 累计 msg / 错误 / 重启）+ 心跳趋势 sparkline + 协议参数摘要 | `TaskDiagnostics` + `CollectorTask` |
| **分组** | 该任务下的分组列表（CRUD），点分组进入 Tag tab 并预筛 | `Group` (where TaskId) |
| **Tag** | 该任务下的所有 Tag（CRUD）+ 分组筛选 + 类型筛选 + 搜索 + 浏览节点 + Excel | `Tag` (where TaskId) + 实时质量码 |
| **实时数据** | 该任务的实时值流（保留现 LiveDataView 单任务子集行为） | `TaskOrchestrator.TagValueReceived` |
| **诊断** | 该任务的诊断行（速率/秒、错误数、重启次数、心跳） | `TaskDiagnostics` |
| **配置** | 任务级编辑（Server URI / 协议 / 采样间隔 / 死区 / TCP 地址） | `CollectorTask` |

**全局监控**（侧栏 → 全局监控分组下）：

| 视图 | 内容 |
|---|---|
| 实时数据 | 所有任务的值流汇总（保留现 `LiveDataView` 跨任务版） |
| 诊断 | 所有任务的诊断行（保留现 `DiagnosticsView`） |

工作台 tab 内的「实时/诊断」是当前任务子集；全局监控是跨任务总览。两层兼顾。

---

## 3. 架构变更

### 3.1 目录布局

```
wpf/src/Dc.App/
├── App.xaml                       # 改：加载 wpfui 主题字典 + ThemeService
├── App.xaml.cs                    # 改：注册 ThemeService、新 Shell、Page 路由
├── Views/
│   ├── Shell/                       ← 新
│   │   ├── ShellWindow.xaml         (FluentWindow + NavigationView)
│   │   └── ShellWindow.xaml.cs
│   ├── Dashboard/                   ← 新
│   │   ├── DashboardView.xaml
│   │   └── DashboardView.xaml.cs
│   ├── Workspace/                   ← 新（采集任务工作台）
│   │   ├── TaskWorkspaceView.xaml   (master-detail 容器)
│   │   ├── Tabs/
│   │   │   ├── OverviewTab.xaml
│   │   │   ├── GroupsTab.xaml       (复用现 GroupsView 内容)
│   │   │   ├── TagsTab.xaml         (复用现 TagsView 内容)
│   │   │   ├── LiveDataTab.xaml     (复用现 LiveDataView，按 taskId 过滤)
│   │   │   ├── DiagnosticsTab.xaml  (复用现 DiagnosticsView，按 taskId 过滤)
│   │   │   └── ConfigTab.xaml       (复用现 TaskEditor 字段，inline)
│   ├── GlobalMonitor/               ← 移动现有 LiveDataView / DiagnosticsView
│   ├── Browse/                      ← 保留现有 BrowseView，仅升级样式
│   ├── Settings/                    ← 保留现有 ConfigView，仅升级样式
│   └── Logs/                        ← 保留现有 LogsView，仅升级样式
├── ViewModels/
│   ├── Shell/
│   │   └── ShellViewModel.cs
│   ├── Dashboard/
│   │   └── DashboardViewModel.cs
│   ├── Workspace/
│   │   ├── TaskWorkspaceViewModel.cs       (master + selected task)
│   │   ├── TaskRowViewModel.cs             (任务列表行，已存在)
│   │   └── Tabs/...
│   ├── Services/
│   │   ├── ThemeService.cs                 ← 新
│   │   └── INavigationService.cs           ← 新
│   └── ... (旧 VM 大部分保留)
└── Assets/Theme/                    ← 新
    ├── Tokens.xaml                  (颜色/间距/字号 design tokens)
    └── DataGrid.xaml                (wpfui DataGrid 派生样式，复用)
```

### 3.2 删除项

- `MainWindow.xaml` / `MainWindowViewModel.cs`：被 `ShellWindow` / `ShellViewModel` 替换
- `H.NotifyIcon.Wpf` 包：被 `WPF-UI.Tray` 替换
- 现 8 项侧栏导航逻辑：被新 7 项分组导航替换

### 3.3 ShellViewModel API

```csharp
public partial class ShellViewModel : ObservableObject
{
    [ObservableProperty] private string _currentTitle = "仪表盘";
    [ObservableProperty] private object? _currentContent;     // ContentPresenter binding
    [ObservableProperty] private string _selectedRoute = "dashboard";

    public IReadOnlyList<NavItem> NavItems { get; }
    public ObservableCollection<NavItem> FooterItems { get; }

    public ICommand NavigateCommand { get; }                  // 接收 route tag (string)
    public ICommand ToggleThemeCommand { get; }               // tray menu / settings 都能触发
}

public record NavItem(string Route, string Title, string Icon, string? Header = null);
```

路由表：

```csharp
Route → ViewModel + View
"dashboard"    → DashboardViewModel        → DashboardView
"workspace"    → TaskWorkspaceViewModel    → TaskWorkspaceView
"browse"       → BrowseViewModel           → BrowseView      (保留)
"livedata"     → LiveDataViewModel         → LiveDataView    (保留，标记 mode=Global)
"diagnostics"  → DiagnosticsViewModel      → DiagnosticsView (保留，标记 mode=Global)
"settings"     → ConfigViewModel           → ConfigView      (保留)
"logs"         → LogsViewModel             → LogsView        (保留)
"about"        → AboutViewModel            → AboutWindow (footer，模态)
```

### 3.4 ThemeService

```csharp
public enum AppTheme { Light, Dark, System }

public interface IThemeService
{
    AppTheme Current { get; }
    event Action<AppTheme>? ThemeChanged;
    void Apply(AppTheme theme);                  // 持久化到 appsettings.json: Theme
}
```

实现：
- `WPF-UI` 自带 `ApplicationThemeManager.Apply(...)`，直接复用
- `System` 模式：监听 `Microsoft.Win32.SystemEvents.UserPreferenceChanged` + `SystemParameters.HighContrast`
- 启动时从 `appsettings.json` 读 `Theme` 字段，缺省 `System`
- 切换后写回 `appsettings.json`，避免下次启动重置

UI 入口：
- 系统托盘菜单 → 主题（亮/暗/跟随系统）
- 设置 → 外观 → 主题（同上，三选一 RadioButton）

---

## 4. 依赖变更

### 4.1 `Directory.Packages.props` 增量

```xml
<!-- 新增 -->
<PackageVersion Include="WPF-UI" Version="3.0.5" />
<PackageVersion Include="WPF-UI.Tray" Version="3.0.5" />

<!-- 移除（被 WPF-UI.Tray 替代） -->
<!-- <PackageVersion Include="H.NotifyIcon.Wpf" Version="2.2.0" /> -->
```

`Dc.App.csproj` 增加 `PackageReference Include="WPF-UI"` + `WPF-UI.Tray`，移除 `H.NotifyIcon.Wpf`。

### 4.2 App.xaml 资源字典

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ui:ThemesDictionary Theme="Light" />     <!-- 初始值，运行时由 ThemeService 覆盖 -->
      <ui:ControlsDictionary />
      <ResourceDictionary Source="Assets/Theme/Tokens.xaml" />
      <ResourceDictionary Source="Assets/Theme/DataGrid.xaml" />
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

### 4.3 ServiceRegistration.cs 增量

新增注册：

```csharp
services.AddSingleton<IThemeService, ThemeService>();
services.AddSingleton<INavigationService, NavigationService>();

services.AddSingleton<ShellWindow>();
services.AddSingleton<ShellViewModel>();
services.AddSingleton<DashboardViewModel>();
services.AddSingleton<TaskWorkspaceViewModel>();
// 旧 VM 保留 transient/singleton 注册不变
```

`Application_Startup` 启动入口从 `MainWindow` 改 `ShellWindow`。

---

## 5. 实施阶段

试金石策略已锁定 — 一次只做一刀，验证后再下一刀。

| Stage | 内容 | 产出 | 验收 |
|---|---|---|---|
| **S1 · Shell + Theme** | FluentWindow 替换 MainWindow、新 7 项 NavigationView、ThemeService 三档切换、托盘换 wpfui | 启动后是 Mica 窗口，所有旧 View 仍能通过 sidebar 打开（视觉混搭可接受） | 1) 启动看到 Mica；2) 切换主题三档生效并持久化；3) 旧 8 个 View 全部可达 |
| **S2 · Dashboard (C 风格)** | DashboardView + ViewModel，健康度大数字、当前告警卡片、6 环形指标、任务速率列表、公告条 | 启动默认进 Dashboard 看到 C 风格 mockup 同等内容 | 1) 全部 KPI 数据真实 binding；2) 告警 click 跳「采集任务」选中对应 task；3) 30s 自动刷新（DispatcherTimer） |
| **S3 · 采集任务工作台** | TaskWorkspaceView master-detail，6 tabs（概览/分组/Tag/实时/诊断/配置）。其中分组/Tag/实时/诊断 4 个 tab **复用现 ViewModel 的查询逻辑**（按 `taskId` 注入构造参数过滤），但 UserControl/XAML 重写以适配 tab 容器尺寸与 wpfui 视觉令牌（不嵌入旧顶层 UserControl 整体） | 工作台内有功能完整的 Group / Tag / 实时 / 诊断 子视图，旧侧栏顶层入口对应迁出 | 1) 选 task 切换 tab 上下文跟随；2) Tag CRUD/Excel/浏览节点全链路无回归（含热加热卸）；3) 面包屑实时显示 |
| **S4 · 全局监控分离** | LiveDataView / DiagnosticsView 加 `Mode = Task(taskId) \| Global` 二态，全局模式挂「全局监控」分组 | 跨任务大屏 + 单任务子集两种用法都成立 | 1) 全局模式行为 == 现在；2) 任务子集模式 binding 正确 |
| **S5 · 其余 view 视觉收敛** | Browse / Settings / Logs 套 wpfui Style + DataGrid 派生样式 + 卡片化布局 | 全 App 视觉一致 | 设计 walkthrough：截图比对，没有"突兀的旧 view" |

S1 + S2 是「试金石」，跑完即可邀请用户体验 + 给反馈；S3 是工作台落地（重点）；S4 + S5 是收尾。

每个 Stage 单独走 PR / 测试 / 验收。S3 完成后再决定 Phase 9 运维加强 的优先级。

---

## 6. 风险与开放问题

### 6.1 已知风险

| 风险 | 影响 | 缓解 |
|---|---|---|
| Mica 仅 Win11 22H2+ 原生支持；Win10 / Win11 早期版本会 fallback 到纯色背景 | 视觉效果降级 | wpfui 自动 fallback，无需特别处理；在 README 标注最佳体验环境 |
| wpfui 3.x 与现有 `Microsoft.Xaml.Behaviors.Wpf` 共存兼容性 | 启动崩 / 样式冲突 | S1 第一步先在空白工程验证导航 + 主题，确认无冲突再批量改 |
| 旧 view 直接挂新 shell 视觉混搭期可能让用户困惑「为什么 Dashboard 漂亮但 Tag 还是旧的」 | UX 短期不一致 | StatusBar 提示「视觉迁移进行中 · v1.1 完成」；S5 之前不发 release |
| `TaskWorkspaceViewModel` 复用现 VM 但加 `taskId` 过滤，VM 改造点多 | S3 实施工时被低估 | 评估 S3 时先列每个 VM 改动点清单（在写 implementation plan 时做） |
| `WPF-UI.Tray` 替换 `H.NotifyIcon` 后单实例 mutex 行为变化 | 二次启动失效 | 单实例 Mutex 由 `App.xaml.cs` 持有，与 tray 无关，验证即可 |

### 6.2 开放问题（评审时确认）

1. **Spec 落盘位置**：选了 `wpf/docs/specs/`，跟现有 `wpf/docs/wire-format.md` 一致。是否 OK？还是要根目录 `docs/superpowers/specs/`？
2. **关于按钮位置**：mockup 放在 NavigationView footer。是否改成 Settings 子项 + 状态栏 i 图标？
3. **「全局监控」命名**：考虑过「监控总览」/「全局视图」/「实时大屏」，目前用「全局监控」。有没有更合适的？
4. **Dashboard 公告条**：mockup 里写了「22:00 计划维护」的公告条。这个功能要不要做？还是只是 mockup 装饰？如做需要新数据源（系统配置表 + 时间窗口）。
5. **采集任务工作台 "概览" tab**：本 spec 给了占位 KPI 列表，但具体心跳 sparkline 数据从哪来？`TaskDiagnostics` 当前没存历史，需要在 orchestrator 加 ring buffer。这一项算 S2 范围还是 S3 范围？

---

## 7. 测试策略

- **ViewModel 测试**（xUnit）：每个新 VM 加单元测试，覆盖 navigation/theme switch/tab 切换/master 选中
- **ThemeService 测试**：三态切换、持久化、System 模式监听 `UserPreferenceChanged`
- **集成测试**：保留现 58 个测试不变 — 重构 UI 不动后端，应当 0 回归
- **手工测试 walkthrough**（每 Stage 验收）：
  - 启动 Shell → 切换主题 → 看主题持久化
  - Dashboard → 点告警 → 跳转工作台对应任务
  - 工作台 → 切换 task → tab 上下文跟随
  - 全部旧 view 仍能从导航打开且功能未回归
- **截图回归**（可选，S5 时考虑）：用 `WpfUiTests` 或 `WpfStorm` 抓 Shell + Dashboard + Workspace 三张图入库

---

## 8. 决策摘要（一句话各项）

- **视觉**：Win11 Fluent / Mica（wpfui 控件库）
- **主题**：三档（亮 / 暗 / 跟随系统），持久化到 appsettings.json
- **信息架构**：仪表盘优先 + 采集任务 master-detail + 全局监控保留
- **采集合并**：Tasks/Groups/Tags 合并为「采集任务」工作台，子标签页式
- **范围**：试金石 = Shell + Dashboard；后续阶段 = 工作台 + 全局监控分离 + 视觉收敛
- **不动后端**：所有 service / orchestrator / EF Core / 消息层完全不变
- **暂搁 Phase 9**：运维面板增强（错误明细 / Telemetry / 趋势图）等 UI 重构完成后再决定

---

## 9. 关联资源

- mockup 站点：`/tmp/dc-mockups/`（本机 HTTP 127.0.0.1:8765）
  - `c-status-board.html` — Dashboard 风格定稿
  - `c2-workspace.html` — 采集任务工作台定稿
- WPF UI 控件库：https://github.com/lepoco/wpfui
- 关联记忆：
  - `feedback_rewrite_principles` — 不死板移植，按产品级重写
  - `project_da_sdk_decision` — vendor SDK 决策
