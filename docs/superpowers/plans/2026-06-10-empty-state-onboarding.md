# 空状态 + 引导体系 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 给 Dc.App 加可复用 `EmptyState` 控件，接入采集任务/浏览节点/实时数据/诊断 4 页，用上下文 CTA 串起「浏览连接→新建任务→看数据」引导链，降低工程师一次性配置的上手门槛。

**架构：** 新增 1 个模板化控件（`EmptyState : Control` + Tokens.xaml 默认模板）+ 1 个值转换器（`CountToVisibilityConverter`）。各页在内容区叠加 `EmptyState`，按集合 `Count==0` / `Connected` 控制可见。LiveData/Diagnostics 独立页注入导航委托提供「去采集任务」CTA，内嵌 tab 实例不显示 CTA。浏览节点失败态用显式 `IsConnectError` 标志驱动红色内联条。纯展示层，不动采集/连接业务逻辑。

**技术栈：** WPF（net8.0-windows）、WPF-UI 3.0.5（`ui:SymbolIcon`/`SymbolRegular`）、CommunityToolkit.Mvvm（`[RelayCommand]`/`[ObservableProperty]`）、xUnit + Moq。构建/测试/截图一律走 dc-remote（home 工作区）。

**规格依据：** `docs/superpowers/specs/2026-06-10-empty-state-onboarding-design.md`

**dc-remote 常用命令（每次构建/验证用）：**
- 同步+构建：`~/dc-remote.sh home sync && ~/dc-remote.sh home build`
- 跑测试（带 TRX 解析）：`~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter <名>'`
- 截图验证：`~/dc-remote.sh home run` → `~/dc-remote.sh home ui click <页名>` → `~/dc-remote.sh home shot` → Read `/tmp/dc-home-screen.png`

---

## 文件结构

**新增：**
- `src/Dc.App/Views/Converters/CountToVisibilityConverter.cs` — `int Count`→`Visibility`（`0`→Visible，支持 `ConverterParameter=Invert` 反向）。职责：单一绑定转换。
- `src/Dc.App/Controls/EmptyState.cs` — 模板化控件，依赖属性 Icon/Title/Hint/ActionText/ActionCommand。职责：展示空状态 + 触发一个命令，零业务逻辑。
- 测试：`tests/Dc.App.Tests/Views/Converters/CountToVisibilityConverterTests.cs`、`tests/Dc.App.Tests/ViewModels/NavigateCtaTests.cs`

**修改：**
- `src/Dc.App/Theme/Tokens.xaml` — `EmptyState` 默认 `ControlTemplate` + 样式。
- `src/Dc.App/App.xaml` — 注册 `CountToVisibilityConverter`（key `CountToVis`）。
- `src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs` — `NewTaskAsync` 加 `[RelayCommand]`。
- `src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml` — 无任务时叠加 EmptyState（CTA 绑 `NewTaskCommand`）。
- `src/Dc.App/ViewModels/BrowseViewModel.cs` — 加 `IsConnectError` 标志。
- `src/Dc.App/Views/BrowseView.xaml` — 未连接 EmptyState + 失败红色内联条。
- `src/Dc.App/ViewModels/LiveDataViewModel.cs` / `DiagnosticsViewModel.cs` — 加导航委托 + `ShowNavigateCta` + `NavigateToWorkspaceCommand` + `NavigateCtaText`。
- `src/Dc.App/Views/LiveDataView.xaml` / `DiagnosticsView.xaml` — Rows 空时叠加 EmptyState。
- `src/Dc.App/Composition/ServiceRegistration.cs` — 独立 LiveData/Diag 实例传导航委托 + `showNavigateCta:true`。

---

## 任务 1：CountToVisibilityConverter（值转换器 + 单测）

**文件：**
- 创建：`src/Dc.App/Views/Converters/CountToVisibilityConverter.cs`
- 测试：`tests/Dc.App.Tests/Views/Converters/CountToVisibilityConverterTests.cs`

- [ ] **步骤 1：编写失败的测试**

创建 `tests/Dc.App.Tests/Views/Converters/CountToVisibilityConverterTests.cs`：

```csharp
using System.Globalization;
using System.Windows;
using Dc.App.Views.Converters;

namespace Dc.App.Tests.Views.Converters;

public class CountToVisibilityConverterTests
{
    private readonly CountToVisibilityConverter _c = new();

    [Fact]
    public void Zero_To_Visible()
        => Assert.Equal(Visibility.Visible, _c.Convert(0, typeof(Visibility), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Positive_To_Collapsed()
        => Assert.Equal(Visibility.Collapsed, _c.Convert(3, typeof(Visibility), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Invert_Zero_To_Collapsed()
        => Assert.Equal(Visibility.Collapsed, _c.Convert(0, typeof(Visibility), "Invert", CultureInfo.InvariantCulture));

    [Fact]
    public void Invert_Positive_To_Visible()
        => Assert.Equal(Visibility.Visible, _c.Convert(5, typeof(Visibility), "Invert", CultureInfo.InvariantCulture));

    [Fact]
    public void Null_Or_NonInt_Treated_As_Empty_Visible()
        => Assert.Equal(Visibility.Visible, _c.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
}
```

- [ ] **步骤 2：运行测试验证失败**

运行：`~/dc-remote.sh home sync && ~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter CountToVisibilityConverter'`
预期：编译失败（`CountToVisibilityConverter` 未定义）→ `TRX_MISSING`。

- [ ] **步骤 3：编写最少实现代码**

创建 `src/Dc.App/Views/Converters/CountToVisibilityConverter.cs`：

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dc.App.Views.Converters;

/// <summary>
/// 集合 Count → Visibility：0（空）→ Visible，非 0 → Collapsed。
/// ConverterParameter="Invert" 反向。null/非 int 按「空」兜底，不抛。
/// 绑 ObservableCollection.Count，集合增删会触发刷新。
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is not int n || n == 0;
        var invert = parameter as string == "Invert";
        var visibleWhenEmpty = !invert;
        return isEmpty == visibleWhenEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **步骤 4：运行测试验证通过**

运行：`~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter CountToVisibilityConverter'`
预期：`total=5 passed=5 failed=0`

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/Views/Converters/CountToVisibilityConverter.cs tests/Dc.App.Tests/Views/Converters/CountToVisibilityConverterTests.cs
git commit -m "✨ feat(ui): CountToVisibilityConverter 空集合→可见转换器"
```

---

## 任务 2：EmptyState 控件 + Tokens 模板

**文件：**
- 创建：`src/Dc.App/Controls/EmptyState.cs`
- 修改：`src/Dc.App/Theme/Tokens.xaml`（追加默认样式）
- 修改：`src/Dc.App/App.xaml`（注册 CountToVis 转换器）

> 说明：模板化控件无单测（WPF 视觉层），用「构建通过 + 后续页面接入后截图」验证。

- [ ] **步骤 1：创建控件类**

创建 `src/Dc.App/Controls/EmptyState.cs`：

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Dc.App.Controls;

/// <summary>
/// 可复用空状态：图标 + 标题 + 说明 + 可选主操作按钮。零业务逻辑。
/// 默认模板见 Theme/Tokens.xaml。ActionText 为空时按钮不显示。
/// </summary>
public sealed class EmptyState : Control
{
    static EmptyState()
        => DefaultStyleKeyProperty.OverrideMetadata(
            typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(EmptyState),
        new PropertyMetadata(SymbolRegular.Info24));
    public SymbolRegular Icon { get => (SymbolRegular)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyState), new PropertyMetadata(null));
    public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null));
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
}
```

- [ ] **步骤 2：在 Tokens.xaml 追加默认模板**

在 `src/Dc.App/Theme/Tokens.xaml` 根 `ResourceDictionary` 内（确保顶部有 `xmlns:controls="clr-namespace:Dc.App.Controls"` 与 `xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"`，无则加）追加：

```xml
<Style TargetType="{x:Type controls:EmptyState}">
    <Setter Property="IsTabStop" Value="False" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="{x:Type controls:EmptyState}">
                <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center" MaxWidth="380">
                    <ui:SymbolIcon Symbol="{TemplateBinding Icon}" FontSize="44"
                                   HorizontalAlignment="Center"
                                   Foreground="{DynamicResource TextFillColorTertiaryBrush}" />
                    <TextBlock Text="{TemplateBinding Title}" Margin="0,14,0,0"
                               HorizontalAlignment="Center" FontSize="16" FontWeight="SemiBold"
                               Foreground="{DynamicResource TextFillColorPrimaryBrush}" />
                    <TextBlock Text="{TemplateBinding Hint}" Margin="0,6,0,0"
                               HorizontalAlignment="Center" TextAlignment="Center" TextWrapping="Wrap"
                               Foreground="{DynamicResource TextFillColorTertiaryBrush}" />
                    <Button Content="{TemplateBinding ActionText}"
                            Command="{TemplateBinding ActionCommand}"
                            Margin="0,18,0,0" HorizontalAlignment="Center"
                            MinWidth="120">
                        <Button.Style>
                            <Style TargetType="Button" BasedOn="{StaticResource DcBtnPrimary}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding ActionText, RelativeSource={RelativeSource AncestorType={x:Type controls:EmptyState}}}" Value="{x:Null}">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                    </DataTrigger>
                                    <DataTrigger Binding="{Binding ActionText, RelativeSource={RelativeSource AncestorType={x:Type controls:EmptyState}}}" Value="">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Button.Style>
                    </Button>
                </StackPanel>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

> 注：`DcBtnPrimary`（TargetType Button）已在 Tokens.xaml 定义，`BasedOn` 复用。

- [ ] **步骤 3：在 App.xaml 注册转换器**

`src/Dc.App/App.xaml` 顶部 `xmlns` 区加（若无）：`xmlns:conv="clr-namespace:Dc.App.Views.Converters"`；在 `<BooleanToVisibilityConverter x:Key="BoolToVis" />`（约第 20 行）下一行加：

```xml
<conv:CountToVisibilityConverter x:Key="CountToVis" />
```

- [ ] **步骤 4：构建验证**

运行：`~/dc-remote.sh home sync && ~/dc-remote.sh home build`
预期：`0 个警告 0 个错误`（控件 + 模板 + 转换器注册编译通过）。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/Controls/EmptyState.cs src/Dc.App/Theme/Tokens.xaml src/Dc.App/App.xaml
git commit -m "✨ feat(ui): EmptyState 可复用空状态控件 + Tokens 默认模板"
```

---

## 任务 3：采集任务页接入 + NewTaskCommand

**文件：**
- 修改：`src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs:254`（`NewTaskAsync` 加 `[RelayCommand]`）
- 修改：`src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml`
- 测试：`tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs`（加 NewTaskCommand 测试）

- [ ] **步骤 1：编写失败的测试**

在 `tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs` 的类内追加（沿用文件内既有 `Task1`、`FakeTaskSource`、`BuildFull` 约定；新建一个返回任务的 editor）：

```csharp
private sealed class SavingEditor : Dc.App.Services.ITaskEditorDialog
{
    public CollectorTask? Edit(CollectorTask? existing) => Task1("new-1");
}

[Fact]
public async Task NewTaskCommand_Exists_And_Saves_Via_Editor()
{
    var src = new FakeTaskSource();
    var orch = new FakeOrchView();
    var overview = new WorkspaceOverviewViewModel(orch, () => Now);
    var config = new WorkspaceConfigViewModel(new FakeEditor());
    var vm = new TaskWorkspaceViewModel(
        src, orch, () => Now, TimeSpan.FromSeconds(120),
        overview, new FakeTagPanel(),
        orchestrator: null,
        editor: new SavingEditor(),
        groupsPanel: new FakeGroupPanel(),
        livePanel: new FakeLivePanel(),
        diagPanel: new FakeDiagPanel(),
        config: config);

    Assert.NotNull(vm.NewTaskCommand);
    await vm.NewTaskCommand.ExecuteAsync(null);

    Assert.Single(src.Saved);
}
```

- [ ] **步骤 2：运行测试验证失败**

运行：`~/dc-remote.sh home sync && ~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter NewTaskCommand_Exists_And_Saves_Via_Editor'`
预期：编译失败（`NewTaskCommand` 不存在）。

- [ ] **步骤 3：给 NewTaskAsync 加 [RelayCommand]**

修改 `src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs`，在 `public async Task NewTaskAsync()` 上方加特性（确保文件已 `using CommunityToolkit.Mvvm.Input;`，既有 `[RelayCommand]` 用法说明已 using）：

```csharp
    [RelayCommand]
    public async Task NewTaskAsync()
    {
        if (_editor is null) return;
        var edited = _editor.Edit(null);
        if (edited is null) return;

        edited.Id = Dc.Infrastructure.Persistence.UlidGenerator.NewId();
        await _source.SaveNewTaskAsync(edited);
        await LoadAsync();
    }
```

- [ ] **步骤 4：运行测试验证通过**

运行：`~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter NewTaskCommand_Exists_And_Saves_Via_Editor'`
预期：`passed=1`

- [ ] **步骤 5：接入空状态 XAML**

在 `src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml` 根 `<Grid Margin="22,18">` 内末尾（最后一个子元素后、`</Grid>` 前）追加一个跨列 EmptyState 覆盖层，无任务时显示：

```xml
        <!-- 空状态：无任何采集任务时覆盖整页，主 CTA 居中 -->
        <controls:EmptyState Grid.Column="0" Grid.ColumnSpan="3"
                             Icon="TaskListSquareLtr24"
                             Title="还没有采集任务"
                             Hint="新建任务，连接 OPC 服务器开始采集数据"
                             ActionText="+ 新建任务"
                             ActionCommand="{Binding NewTaskCommand}"
                             Visibility="{Binding AllTasks.Count, Converter={StaticResource CountToVis}}" />
```

并给原有 master `DockPanel`（`Grid.Column="0"`）与右侧详情面板（`Grid.Column="2"` 容器）各加：

```xml
            Visibility="{Binding AllTasks.Count, Converter={StaticResource CountToVis}, ConverterParameter=Invert}"
```

> 确保根 `UserControl` 顶部已有 `xmlns:controls="clr-namespace:Dc.App.Controls"`（文件已存在该 xmlns）。

- [ ] **步骤 6：构建 + 截图验证空状态**

```bash
~/dc-remote.sh home sync && ~/dc-remote.sh home build
~/dc-remote.sh home run
~/dc-remote.sh home ui click 采集任务
~/dc-remote.sh home shot   # Read /tmp/dc-home-screen.png
```
预期：无任务时显示居中「还没有采集任务 + [+ 新建任务]」，不再是空列表 + 左下角按钮。

- [ ] **步骤 7：Commit**

```bash
git add src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs
git commit -m "✨ feat(ui): 采集任务空状态 + NewTaskCommand（CTA 主操作居中）"
```

---

## 任务 4：浏览节点接入 + 失败内联条

**文件：**
- 修改：`src/Dc.App/ViewModels/BrowseViewModel.cs`（加 `IsConnectError`）
- 修改：`src/Dc.App/Views/BrowseView.xaml`

- [ ] **步骤 1：VM 加 IsConnectError 标志**

在 `src/Dc.App/ViewModels/BrowseViewModel.cs` 字段区（`_statusMessage` 附近）加：

```csharp
    [ObservableProperty] private bool _isConnectError;
```

在 `ConnectAsync` 内设置（开始置 false、catch 置 true）。改 `ConnectAsync`（约 47-80 行）相应位置：

```csharp
        IsConnectError = false;
        StatusMessage = "正在连接…";
        try
        {
            await _browser.ConnectAsync(options);
            Connected = true;
            // ...既有成功逻辑（StatusMessage = "已连接 ..."）...
        }
        catch (Exception ex)
        {
            Connected = false;
            IsConnectError = true;
            StatusMessage = $"连接失败: {ex.Message}";
        }
```

- [ ] **步骤 2：接入 XAML（空状态 + 失败条）**

在 `src/Dc.App/Views/BrowseView.xaml` 顶部加 `xmlns:controls="clr-namespace:Dc.App.Controls"`（若无）。在结果表格容器（节点列表 Border/Grid）所在单元格叠加 EmptyState，未连接且非加载时显示：

```xml
        <controls:EmptyState
            Icon="Search24"
            Title="未连接到 OPC 服务器"
            Hint="填入端点地址，点连接浏览地址空间并复制 NodeId"
            ActionText="连接"
            ActionCommand="{Binding ConnectCommand}">
            <controls:EmptyState.Visibility>
                <MultiBinding Converter="{StaticResource ...}">
                    <!-- 见下：用 BoolToVis 处理 Connected 与 IsLoading 的简单组合 -->
                </MultiBinding>
            </controls:EmptyState.Visibility>
        </controls:EmptyState>
```

> 简化实现：避免新增 MultiBinding 转换器，给 BrowseViewModel 加一个只读派生属性 `public bool ShowConnectPrompt => !Connected && !IsLoading;`，并在 `OnConnectedChanged`/`OnIsLoadingChanged`（CommunityToolkit 生成的局部方法）里 `OnPropertyChanged(nameof(ShowConnectPrompt));`。XAML 用：

```xml
        <controls:EmptyState Grid.Row="..." Grid.Column="..."
            Icon="Search24"
            Title="未连接到 OPC 服务器"
            Hint="填入端点地址，点连接浏览地址空间并复制 NodeId"
            ActionText="连接"
            ActionCommand="{Binding ConnectCommand}"
            Visibility="{Binding ShowConnectPrompt, Converter={StaticResource BoolToVis}}" />
```

对应在 `BrowseViewModel.cs` 加：

```csharp
    public bool ShowConnectPrompt => !Connected && !IsLoading;

    partial void OnConnectedChanged(bool value) => OnPropertyChanged(nameof(ShowConnectPrompt));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowConnectPrompt));
```

失败内联条（放工具栏下方）：

```xml
        <Border Style="{StaticResource DcAlertBad}"
                Visibility="{Binding IsConnectError, Converter={StaticResource BoolToVis}}">
            <TextBlock Text="{Binding StatusMessage}" />
        </Border>
```

> `DcAlertBad` 样式已存在于 Tokens.xaml。

- [ ] **步骤 3：构建 + 截图验证**

```bash
~/dc-remote.sh home sync && ~/dc-remote.sh home build
~/dc-remote.sh home run
~/dc-remote.sh home ui click 浏览节点
~/dc-remote.sh home shot   # Read：未连接时应显示「未连接 + [连接]」空状态
~/dc-remote.sh home ui set <服务器地址输入框 AutomationId 或保持默认> opc.tcp://127.0.0.1:1
~/dc-remote.sh home ui click 连接
~/dc-remote.sh home shot   # Read：失败应显示红色内联条「连接失败: ...」
```
预期：未连接空状态 + 失败红色内联条都出现。

- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/ViewModels/BrowseViewModel.cs src/Dc.App/Views/BrowseView.xaml
git commit -m "✨ feat(ui): 浏览节点未连接空状态 + 连接失败红色内联条"
```

---

## 任务 5：LiveData/Diagnostics VM 导航 CTA（含测试 + 装配）

**文件：**
- 修改：`src/Dc.App/ViewModels/LiveDataViewModel.cs`、`src/Dc.App/ViewModels/DiagnosticsViewModel.cs`
- 修改：`src/Dc.App/Composition/ServiceRegistration.cs:189,191`
- 测试：`tests/Dc.App.Tests/ViewModels/NavigateCtaTests.cs`

- [ ] **步骤 1：编写失败的测试**

创建 `tests/Dc.App.Tests/ViewModels/NavigateCtaTests.cs`：

```csharp
using System.Windows.Threading;
using Dc.App.ViewModels;
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;

namespace Dc.App.Tests.ViewModels;

public class NavigateCtaTests
{
    private static TaskOrchestrator Orch()
        => new(Array.Empty<IOpcSubscriberFactory>(), new FakePublisherFactory(), new OrchestratorOptions(), null);

    private sealed class FakePublisherFactory : IPublisherFactory
    {
        public IPublisher Create(string tcpAddress) => throw new NotSupportedException();
    }

    [Fact]
    public void LiveData_Standalone_ShowsCta_And_Navigates()
    {
        string? navigated = null;
        var vm = new LiveDataViewModel(Orch(), Dispatcher.CurrentDispatcher,
            navigate: key => navigated = key, showNavigateCta: true);

        Assert.True(vm.ShowNavigateCta);
        Assert.Equal("去采集任务", vm.NavigateCtaText);
        vm.NavigateToWorkspaceCommand.Execute(null);
        Assert.Equal("workspace", navigated);
    }

    [Fact]
    public void LiveData_Embedded_NoCta()
    {
        var vm = new LiveDataViewModel(Orch(), Dispatcher.CurrentDispatcher);
        Assert.False(vm.ShowNavigateCta);
        Assert.Null(vm.NavigateCtaText);
    }

    [Fact]
    public void Diagnostics_Standalone_ShowsCta_And_Navigates()
    {
        string? navigated = null;
        var vm = new DiagnosticsViewModel(Orch(),
            navigate: key => navigated = key, showNavigateCta: true);

        Assert.True(vm.ShowNavigateCta);
        vm.NavigateToWorkspaceCommand.Execute(null);
        Assert.Equal("workspace", navigated);
    }
}
```

> 注：`TaskOrchestrator` 构造签名以现有 `ServiceRegistration.cs:68` 为准（`IEnumerable<IOpcSubscriberFactory>, IPublisherFactory, OrchestratorOptions, ILogger?`）。若 `IPublisherFactory` 接口方法名不同，按实际签名实现 `FakePublisherFactory`（仅用于构造，不会被调用）。

- [ ] **步骤 2：运行测试验证失败**

运行：`~/dc-remote.sh home sync && ~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter NavigateCtaTests'`
预期：编译失败（构造参数/成员不存在）。

- [ ] **步骤 3：改 LiveDataViewModel**

`src/Dc.App/ViewModels/LiveDataViewModel.cs`：构造函数加可选参数与成员。

```csharp
    private readonly Action<string>? _navigate;

    [ObservableProperty] private bool _showNavigateCta;

    public string? NavigateCtaText => ShowNavigateCta ? "去采集任务" : null;

    public LiveDataViewModel(TaskOrchestrator orchestrator, Dispatcher dispatcher,
        Action<string>? navigate = null, bool showNavigateCta = false)
    {
        _orchestrator = orchestrator;
        _dispatcher = dispatcher;
        _navigate = navigate;
        ShowNavigateCta = showNavigateCta;
        // ...既有 ctor 体不变...
    }

    [RelayCommand]
    private void NavigateToWorkspace() => _navigate?.Invoke("workspace");
```

> 确保 `using CommunityToolkit.Mvvm.Input;`。`ShowNavigateCta` 用 `[ObservableProperty]` 便于触发 `NavigateCtaText`；加 `partial void OnShowNavigateCtaChanged(bool _) => OnPropertyChanged(nameof(NavigateCtaText));`（本期 ShowNavigateCta 构造后不变，可省，但加上更稳）。

- [ ] **步骤 4：改 DiagnosticsViewModel**

`src/Dc.App/ViewModels/DiagnosticsViewModel.cs`：同构改造（无 dispatcher 参数）。

```csharp
    private readonly Action<string>? _navigate;

    [ObservableProperty] private bool _showNavigateCta;

    public string? NavigateCtaText => ShowNavigateCta ? "去采集任务" : null;

    public DiagnosticsViewModel(TaskOrchestrator orchestrator,
        Action<string>? navigate = null, bool showNavigateCta = false)
    {
        _orchestrator = orchestrator;
        _navigate = navigate;
        ShowNavigateCta = showNavigateCta;
        // ...既有 ctor 体不变...
    }

    [RelayCommand]
    private void NavigateToWorkspace() => _navigate?.Invoke("workspace");
```

- [ ] **步骤 5：运行测试验证通过**

运行：`~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter NavigateCtaTests'`
预期：`passed=3`

- [ ] **步骤 6：装配独立实例传导航委托**

`src/Dc.App/Composition/ServiceRegistration.cs` 把独立注册（约 189、191 行）

```csharp
        services.AddSingleton<LiveDataViewModel>();
        // ...
        services.AddSingleton<DiagnosticsViewModel>();
```

改为：

```csharp
        services.AddSingleton<LiveDataViewModel>(sp => new LiveDataViewModel(
            sp.GetRequiredService<TaskOrchestrator>(),
            System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher,
            navigate: key => sp.GetRequiredService<Dc.App.ViewModels.Shell.ShellViewModel>().NavigateCommand.Execute(key),
            showNavigateCta: true));
        // ...
        services.AddSingleton<DiagnosticsViewModel>(sp => new DiagnosticsViewModel(
            sp.GetRequiredService<TaskOrchestrator>(),
            navigate: key => sp.GetRequiredService<Dc.App.ViewModels.Shell.ShellViewModel>().NavigateCommand.Execute(key),
            showNavigateCta: true));
```

> 内嵌实例（`IEmbeddableLivePanel`/`IEmbeddableDiagPanel`，约 181-184 行）保持不变（不传 navigate/showNavigateCta → 默认 false）。导航委托内惰性解析 ShellViewModel，避免构造期循环。

- [ ] **步骤 7：构建验证 + 全量 App 测试**

```bash
~/dc-remote.sh home sync && ~/dc-remote.sh home build
~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj'
```
预期：`0 错误`；App 测试全绿（原 71 + 新增 ≈ 79）。

- [ ] **步骤 8：Commit**

```bash
git add src/Dc.App/ViewModels/LiveDataViewModel.cs src/Dc.App/ViewModels/DiagnosticsViewModel.cs src/Dc.App/Composition/ServiceRegistration.cs tests/Dc.App.Tests/ViewModels/NavigateCtaTests.cs
git commit -m "✨ feat(ui): 实时数据/诊断 导航 CTA（独立页去采集任务，内嵌不显示）"
```

---

## 任务 6：LiveData/Diagnostics 视图接入空状态

**文件：**
- 修改：`src/Dc.App/Views/LiveDataView.xaml`、`src/Dc.App/Views/DiagnosticsView.xaml`

- [ ] **步骤 1：LiveDataView 叠加空状态**

`src/Dc.App/Views/LiveDataView.xaml` 顶部加 `xmlns:controls="clr-namespace:Dc.App.Controls"`。在 `Grid.Row="2"`（DataGrid 所在 Border）同单元格、其后追加：

```xml
        <controls:EmptyState Grid.Row="2"
            Icon="DataHistogram24"
            Title="暂无实时数据"
            Hint="运行采集任务后，这里实时显示订阅值"
            ActionText="{Binding NavigateCtaText}"
            ActionCommand="{Binding NavigateToWorkspaceCommand}"
            Visibility="{Binding Rows.Count, Converter={StaticResource CountToVis}}" />
```

并给 `Grid.Row="2"` 的 DataGrid Border 加 `Visibility="{Binding Rows.Count, Converter={StaticResource CountToVis}, ConverterParameter=Invert}"`。

- [ ] **步骤 2：DiagnosticsView 叠加空状态**

`src/Dc.App/Views/DiagnosticsView.xaml` 顶部加 `xmlns:controls="clr-namespace:Dc.App.Controls"`。在 DataGrid 所在行/单元格后追加（统计卡区域保留始终可见，仅表格区放空状态）：

```xml
        <controls:EmptyState Grid.Row="<表格所在行>"
            Icon="Pulse24"
            Title="暂无运行中的任务"
            Hint="启动采集任务后这里显示运行诊断"
            ActionText="{Binding NavigateCtaText}"
            ActionCommand="{Binding NavigateToWorkspaceCommand}"
            Visibility="{Binding Rows.Count, Converter={StaticResource CountToVis}}" />
```

并给该 DataGrid 容器加 `Visibility="{Binding Rows.Count, Converter={StaticResource CountToVis}, ConverterParameter=Invert}"`。

> `Grid.Row` 编号以各 View 实际 RowDefinition 为准（LiveData 表格在 Row 2；Diagnostics 按文件实际行号填）。

- [ ] **步骤 3：构建 + 截图验证（独立页 vs 内嵌 tab）**

```bash
~/dc-remote.sh home sync && ~/dc-remote.sh home build
~/dc-remote.sh home run
~/dc-remote.sh home ui click 实时数据 ; ~/dc-remote.sh home shot   # 独立页：空状态含「去采集任务」
~/dc-remote.sh home ui click 诊断    ; ~/dc-remote.sh home shot   # 独立页：空状态含「去采集任务」
```
预期：两独立页空状态显示且带「去采集任务」CTA。

- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/Views/LiveDataView.xaml src/Dc.App/Views/DiagnosticsView.xaml
git commit -m "✨ feat(ui): 实时数据/诊断 视图空状态接入"
```

---

## 任务 7：整体验证 + 引导链路走查

- [ ] **步骤 1：全量回归测试**

运行：`~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj'`
预期：全绿（无回归）。

- [ ] **步骤 2：四页空状态截图复核**

```bash
~/dc-remote.sh home stop ; ~/dc-remote.sh home run
for p in 采集任务 浏览节点 实时数据 诊断; do ~/dc-remote.sh home ui click "$p"; ~/dc-remote.sh home shot; cp /tmp/dc-home-screen.png /tmp/verify-$p.png; done
```
逐张 Read，确认：每页空状态文案/图标/CTA 正确，亮主题显示正常。

- [ ] **步骤 3：引导链路走查（CTA 串联）**

依次点击验证 CTA 行为：采集任务→[+新建任务] 弹出新建对话框；浏览节点→[连接]；实时数据/诊断→[去采集任务] 跳回采集任务页。

```bash
~/dc-remote.sh home ui click 实时数据 ; ~/dc-remote.sh home ui click 去采集任务 ; ~/dc-remote.sh home shot
```
预期：截图回到采集任务页（验证导航委托生效）。

- [ ] **步骤 4：暗色主题抽查**

```bash
~/dc-remote.sh home ui click 设置 ; ~/dc-remote.sh home ui click 暗色 ; ~/dc-remote.sh home ui click 采集任务 ; ~/dc-remote.sh home shot
```
预期：空状态在暗色主题下配色正常（图标/文字/按钮可读）。完后切回「跟随系统」。

- [ ] **步骤 5：最终 Commit（如有走查中的微调）**

```bash
git add -A
git commit -m "✅ test(ui): 空状态引导体系四页截图复核 + 链路走查通过"
```

---

## 自检结论

- **规格覆盖**：EmptyState 控件（任务2）、转换器（任务1）、采集任务+NewTaskCommand（任务3）、浏览未连接+失败条（任务4）、LiveData/Diag 导航 CTA+装配（任务5）、两视图接入（任务6）、错误态/双用上下文（任务4/5）、测试（任务1/3/5）、视觉验证（任务3/4/6/7）——规格各节均有对应任务。
- **类型一致**：`NewTaskCommand`、`NavigateToWorkspaceCommand`、`ShowNavigateCta`、`NavigateCtaText`、`IsConnectError`、`ShowConnectPrompt`、`CountToVis`/`CountToVisibilityConverter`、`EmptyState` 全程命名一致。
- **待实现期确认**（非阻塞，标注于步骤内）：`TaskOrchestrator`/`IPublisherFactory` 精确签名、DiagnosticsView 表格 Grid.Row 实际行号、BrowseView 结果表格所在单元格——均要求实现者以实际文件为准填入，不影响方案。
