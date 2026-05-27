# S6 — 迁移到 HandyControl（中后台/Antd 风）

**决策**：用户选定 A 中后台/Antd 风 + 用 HandyControl 落地（authentic）。这是把现有 wpfui 整套替换。

**最大约束**：我（Claude）只能在 Linux 上 `dotnet build`，**跑不了 GUI**。运行时 XAML/资源错误抓不到。因此 **Phase 0 曳光弹必须先在用户 Windows 上验证 HandyControl 真能跑通**，再逐页迁。绝不一次性盲迁 13 页。

---

## HandyControl 集成事实（已查 context7）
- NuGet 包：`HandyControl`（取最新 3.5.x，支持 net8.0-windows）
- xmlns：`xmlns:hc="https://handyorg.github.io/handycontrol"`
- App.xaml 合并：
  - `pack://application:,,,/HandyControl;component/Themes/Skin{Default|Dark}.xaml`（皮肤=配色）
  - `pack://application:,,,/HandyControl;component/Themes/Theme.xaml`（控件模板）
- 运行时切皮肤 = 替换 MergedDictionaries 里的 Skin 字典（Default↔Dark）
- 导航：`hc:SideMenu` + `hc:SideMenuItem`（SelectionChanged + Command），天然深色侧栏
- 画刷（DynamicResource，跟皮肤）：`PrimaryBrush`、`RegionBrush`（主背景）、`SecondaryRegionBrush`、`PrimaryTextBrush`、`SecondaryTextBrush`、`ThirdlyTextBrush`、`BorderBrush`、`SuccessBrush`、`WarningBrush`、`DangerBrush`、`InfoBrush`

## 与 wpfui 的关系
- 目标终态：移除 wpfui，全 HandyControl。
- 过渡期：两者**并存**——已迁的页用 hc，未迁的页仍用 wpfui（`ui:` 控件 + wpfui 画刷），避免一次性全崩。
- 关键风险：两库都注册隐式样式（Button/TextBox 无 key 的 Style）→ 合并顺序决定谁赢。App.xaml 里 **HandyControl 放最后**，已迁页的 hc 控件用显式 hc 类型不受影响；旧页多用 `ui:Button` 显式类型，也不受隐式样式影响。
- `Tokens.xaml`（现基于 wpfui 画刷的 DcCard/DcPill/...）：过渡期保留（旧页的 `{StaticResource DcCard}` 还要用，StaticResource 缺失会崩）。迁移完成后，要么删 Tokens、要么把它重指到 HandyControl 画刷。

## ⚠️ 教训（务必遵守）
- `CornerRadius/Thickness/Margin/Padding` 是结构体：Setter/属性里**只能写字面量**（"8"），**禁止** `{StaticResource <Double>}` —— 编译过、运行崩（S5b 已踩）。
- 资源字典 Source 用完整 pack URI（HandyControl 官方就是 pack URI）。
- 每改一处只能 build 验证；真机渲染靠用户。

---

## Phase 0 — 曳光弹（先验证，必须用户 Windows 确认后才继续）

目标：用最小改动验证 HandyControl 在用户机上：①包能还原+编译 ②皮肤运行时加载不崩 ③`hc:SideMenu` 深色侧栏导航能点能切内容 ④亮/暗皮肤切换 ⑤是 Antd 味。

范围：外壳 + 仪表盘 迁到 HandyControl；其余 12 页暂留 wpfui（混搭可接受）。

### Task 0.1 加 HandyControl 包
- `Directory.Packages.props` 加 `<PackageVersion Include="HandyControl" Version="3.5.1" />`
- `Dc.App.csproj` 加 `<PackageReference Include="HandyControl" />`
- 暂不动 wpfui（并存）
- build 验证 + restore 能成功（若 Linux feed 无此包→报 BLOCKED，让用户 Windows 上 restore）

### Task 0.2 App.xaml 合并 HandyControl 皮肤
- MergedDictionaries 末尾加：
  ```xml
  <ResourceDictionary Source="pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml" />
  <ResourceDictionary Source="pack://application:,,,/HandyControl;component/Themes/Theme.xaml" />
  ```
  （放在 wpfui 之后；保留 wpfui + Tokens.xaml 不动）

### Task 0.3 ThemeService 支持 HandyControl 皮肤切换
- 新增 `HandyControlThemeApplier : IThemeApplier`（或扩展现有 applier）：Apply(Light/Dark) → 替换 App.MergedDictionaries 里的 Skin 字典为 SkinDefault/SkinDark。DetectSystemTheme 复用注册表逻辑。
- DI 把 IThemeApplier 指向新 applier（替换 WpfUiThemeApplier）。
- 注意：替换字典要找到旧 Skin 字典并 Remove + Add 新的（按 Source 含 "Skin" 匹配）。

### Task 0.4 Shell 改 HandyControl
- ShellWindow：`ui:FluentWindow` → 普通 `Window`（或 `hc:Window`），背景 `{DynamicResource RegionBrush}`。
- 导航：`ui:NavigationView` → `hc:SideMenu`（深色侧栏，Width 220），`hc:SideMenuItem` 按路由生成（Header + 可选 Icon）。SelectionChanged → 现有 NavigateCommand（沿用 PreviewMouseLeftButtonUp 思路或 SideMenu 的 SelectionChanged 事件桥接）。
- 内容区 `ContentControl Content="{Binding CurrentContent}"` 不变。
- 托盘保留 `H.NotifyIcon`（hc 无托盘）。
- 状态栏用 hc 画刷。
- 标题栏：hc:Window 自带 chrome，或保留简单标题条。

### Task 0.5 仪表盘改 HandyControl
- DashboardView：wpfui 画刷 → hc 画刷（PrimaryTextBrush/RegionBrush/SuccessBrush…），`ui:Card`→ Border + hc 样式，pill→hc Tag 风。converter 的 TryFindResource 改查 hc 画刷键。
- 卡片扁平（圆角 6-8、细边、subtle shadow），表格加密 —— 对齐 antd.html 稿。

### Task 0.6 build + push，用户 Windows 验证
- build 全绿 → push
- 用户 `.\build.ps1 -Target run` → 确认：窗口出来、深色侧栏、点导航切页、设置里切亮/暗皮肤、仪表盘是 Antd 味、其余页仍能开（混搭）。
- **用户确认 OK 后**，才进 Phase 1。

---

## Phase 1+ — 逐页迁（Phase 0 验证通过后再细化）
浏览/实时/诊断/设置/日志 → 工作台 6 tab（含 Groups/Tags）→ 编辑对话框 → 最后移除 wpfui + 清理 Tokens。每页 build + 用户截图确认。Phase 0 通过后再展开细列。

## 回退
若 Phase 0 在用户机上跑不通且短时定位不了：保留当前 wpfui 版本（已工作），HandyControl 改动在分支上可丢弃。wpfui 版是安全网。
