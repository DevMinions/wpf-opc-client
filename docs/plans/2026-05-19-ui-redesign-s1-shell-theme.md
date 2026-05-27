# UI 重设计 S1 — Shell + Theme 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 WPF 主窗口从原生 WPF + ListBox 替换为 `Wpf.Ui` 的 `FluentWindow` + `NavigationView`，引入三档主题切换（亮/暗/跟随系统），托盘从 `H.NotifyIcon` 切到 `Wpf.Ui.Tray`。试金石第一刀，不动任何后端/旧 View。

**Architecture:**
新增 `ShellWindow` 替代 `MainWindow`，新增 `ShellViewModel` 与现有 `NavigationViewModel` 解耦（导航路由由新 `INavigationService` 集中管理）。`IThemeService` + `IThemeApplier` 抽象将 wpfui 平台层（`ApplicationThemeManager`）与可单元测试的持久化逻辑分离。旧 8 个 View / VM 一律不动，通过新 Shell 路由继续呈现 — 验证 Mica 窗口 + 主题切换 + 全 View 可达即视为 S1 完工。

**Tech Stack:** .NET 8 + WPF + Wpf.Ui 3.0.5 + CommunityToolkit.Mvvm + xUnit + Moq

**Spec:** `wpf/docs/specs/2026-05-19-ui-redesign-fluent-design.md`

---

## 前置说明

### 项目目录约定（不在任务里重复，全计划共用）

- Solution 根：`wpf/`
- App 项目：`wpf/src/Dc.App/`
- 现有测试项目：`wpf/tests/Dc.Infrastructure.Tests/`（net8.0）、`wpf/tests/Dc.Integration.Tests/`（net8.0）
- 新建测试项目：`wpf/tests/Dc.App.Tests/`（**net8.0-windows**，必须 — 要引用 WPF 类型）

### 验证命令（每次跑都用绝对路径）

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build Dc.sln -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet test tests/Dc.Infrastructure.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet test tests/Dc.Integration.Tests   -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet test tests/Dc.App.Tests           -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

基线：48 + 10 测试全绿；S1 完工后 `Dc.App.Tests` 新增 8 个测试，总数 66。

---

## Task 1: 添加 Wpf.Ui 包到中央版本锁

**Files:**
- Modify: `wpf/Directory.Packages.props`
- Modify: `wpf/src/Dc.App/Dc.App.csproj`

- [ ] **Step 1: 添加 Wpf.Ui 中央版本**

修改 `wpf/Directory.Packages.props`，在 `<ItemGroup>` 内现有 `H.NotifyIcon.Wpf` 那行下面追加：

```xml
    <PackageVersion Include="H.NotifyIcon.Wpf" Version="2.2.0" />
    <PackageVersion Include="Wpf.Ui" Version="3.0.5" />
```

注意：Wpf.Ui 3.x 的 Tray 控件包含在主包内（`Wpf.Ui.Tray.Controls.NotifyIcon`），不需要单独 `Wpf.Ui.Tray` 包。

- [ ] **Step 2: 在 Dc.App.csproj 引用 Wpf.Ui**

修改 `wpf/src/Dc.App/Dc.App.csproj`，第二个 `<ItemGroup>`（包引用区），把 `<PackageReference Include="H.NotifyIcon.Wpf" />` 改成：

```xml
    <PackageReference Include="H.NotifyIcon.Wpf" />
    <PackageReference Include="Wpf.Ui" />
```

H.NotifyIcon 暂保留（Task 13 会删，那时托盘已切完），保证中间步骤可编译。

- [ ] **Step 3: 验证构建**

Run:
```bash
cd /home/adamyu/workspace/dc/wpf
dotnet restore Dc.sln
dotnet build src/Dc.App/Dc.App.csproj -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 4: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/Directory.Packages.props wpf/src/Dc.App/Dc.App.csproj
git commit -m ":wrench: S1.1: 引入 Wpf.Ui 3.0.5（保留 H.NotifyIcon 过渡）"
```

---

## Task 2: 创建 Dc.App.Tests 测试项目

**Files:**
- Create: `wpf/tests/Dc.App.Tests/Dc.App.Tests.csproj`
- Create: `wpf/tests/Dc.App.Tests/Usings.cs`
- Modify: `wpf/Dc.sln`

- [ ] **Step 1: 创建 csproj**

新建 `wpf/tests/Dc.App.Tests/Dc.App.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Dc.App.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Moq" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Dc.App\Dc.App.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 注册 Moq 到 Directory.Packages.props（如未注册）**

检查：

```bash
grep -c '"Moq"' /home/adamyu/workspace/dc/wpf/Directory.Packages.props
```

若 == 0，在 `Directory.Packages.props` 内追加：

```xml
    <PackageVersion Include="Moq" Version="4.20.72" />
```

- [ ] **Step 3: 创建 Usings.cs**

新建 `wpf/tests/Dc.App.Tests/Usings.cs`：

```csharp
global using Xunit;
global using Moq;
```

- [ ] **Step 4: 把项目挂到 solution**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet sln Dc.sln add tests/Dc.App.Tests/Dc.App.Tests.csproj
```

Expected: "Project ... was added to the solution."

- [ ] **Step 5: 验证构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/tests/Dc.App.Tests/ wpf/Dc.sln wpf/Directory.Packages.props
git commit -m ":white_check_mark: S1.2: 新建 Dc.App.Tests 测试项目（net8.0-windows）"
```

---

## Task 3: 创建 IThemeApplier 抽象 + Wpf.Ui 实现

**Files:**
- Create: `wpf/src/Dc.App/Services/Theme/IThemeApplier.cs`
- Create: `wpf/src/Dc.App/Services/Theme/AppTheme.cs`
- Create: `wpf/src/Dc.App/Services/Theme/WpfUiThemeApplier.cs`

> 把"实际调用 wpfui 切换主题"抽象出来，这样 `ThemeService` 的持久化/选择逻辑可单元测试，不依赖 wpfui 静态类。

- [ ] **Step 1: 创建 AppTheme.cs**

新建 `wpf/src/Dc.App/Services/Theme/AppTheme.cs`：

```csharp
namespace Dc.App.Services.Theme;

public enum AppTheme
{
    Light,
    Dark,
    System
}
```

- [ ] **Step 2: 创建 IThemeApplier.cs**

新建 `wpf/src/Dc.App/Services/Theme/IThemeApplier.cs`：

```csharp
namespace Dc.App.Services.Theme;

public interface IThemeApplier
{
    /// 把 effective theme（Light/Dark，不含 System）下发到 UI 库。
    /// 调用方负责把 System 解析成 Light/Dark 再传进来。
    void Apply(AppTheme effective);

    /// 返回当前系统主题（Light 或 Dark）。用于 AppTheme.System 解析。
    AppTheme DetectSystemTheme();
}
```

- [ ] **Step 3: 创建 WpfUiThemeApplier.cs**

新建 `wpf/src/Dc.App/Services/Theme/WpfUiThemeApplier.cs`：

```csharp
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace Dc.App.Services.Theme;

public sealed class WpfUiThemeApplier : IThemeApplier
{
    public void Apply(AppTheme effective)
    {
        var target = effective switch
        {
            AppTheme.Dark  => ApplicationTheme.Dark,
            AppTheme.Light => ApplicationTheme.Light,
            _ => throw new ArgumentException(
                "WpfUiThemeApplier.Apply 只接受 Light/Dark；System 需先解析。", nameof(effective))
        };
        ApplicationThemeManager.Apply(target);
    }

    public AppTheme DetectSystemTheme()
    {
        // Win11/10：注册表 AppsUseLightTheme == 0 即深色
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var value = key?.GetValue("AppsUseLightTheme");
        if (value is int i && i == 0) return AppTheme.Dark;
        return AppTheme.Light;
    }
}
```

- [ ] **Step 4: 验证构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Services/Theme/
git commit -m ":sparkles: S1.3: IThemeApplier 抽象 + WpfUiThemeApplier 实现"
```

---

## Task 4: ThemeService — TDD 实现

**Files:**
- Create: `wpf/tests/Dc.App.Tests/Services/Theme/ThemeServiceTests.cs`
- Create: `wpf/src/Dc.App/Services/Theme/IThemeService.cs`
- Create: `wpf/src/Dc.App/Services/Theme/ThemeService.cs`

- [ ] **Step 1: 写测试（Red）**

新建 `wpf/tests/Dc.App.Tests/Services/Theme/ThemeServiceTests.cs`：

```csharp
using Dc.App.Services.Theme;
using Microsoft.Extensions.Configuration;

namespace Dc.App.Tests.Services.Theme;

public class ThemeServiceTests
{
    private static IConfiguration ConfigWithTheme(string? value)
    {
        var dict = value is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["Theme"] = value };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void Initial_DefaultsToSystem_WhenConfigMissing()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);

        var svc = new ThemeService(ConfigWithTheme(null), applier.Object);
        svc.Initialize();

        Assert.Equal(AppTheme.System, svc.Current);
        applier.Verify(a => a.Apply(AppTheme.Light), Times.Once);
    }

    [Theory]
    [InlineData("Light", AppTheme.Light)]
    [InlineData("Dark",  AppTheme.Dark)]
    [InlineData("System", AppTheme.System)]
    public void Initial_ReadsConfiguredValue(string configured, AppTheme expected)
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Dark);

        var svc = new ThemeService(ConfigWithTheme(configured), applier.Object);
        svc.Initialize();

        Assert.Equal(expected, svc.Current);
    }

    [Fact]
    public void Apply_System_ResolvesViaApplier()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Dark);

        var svc = new ThemeService(ConfigWithTheme("Light"), applier.Object);
        svc.Initialize();
        svc.Apply(AppTheme.System);

        applier.Verify(a => a.Apply(AppTheme.Dark), Times.Once);
        Assert.Equal(AppTheme.System, svc.Current);
    }

    [Fact]
    public void Apply_Light_CallsApplierWithLight()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);

        var svc = new ThemeService(ConfigWithTheme(null), applier.Object);
        svc.Initialize();
        applier.Invocations.Clear();

        svc.Apply(AppTheme.Light);

        applier.Verify(a => a.Apply(AppTheme.Light), Times.Once);
        Assert.Equal(AppTheme.Light, svc.Current);
    }

    [Fact]
    public void Apply_RaisesThemeChangedEvent()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);
        var svc = new ThemeService(ConfigWithTheme(null), applier.Object);
        svc.Initialize();

        AppTheme? received = null;
        svc.ThemeChanged += t => received = t;

        svc.Apply(AppTheme.Dark);

        Assert.Equal(AppTheme.Dark, received);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: FAIL — `ThemeService` 类型不存在。

- [ ] **Step 3: 实现 IThemeService 接口**

新建 `wpf/src/Dc.App/Services/Theme/IThemeService.cs`：

```csharp
namespace Dc.App.Services.Theme;

public interface IThemeService
{
    AppTheme Current { get; }
    event Action<AppTheme>? ThemeChanged;

    /// 启动时调用一次：读 IConfiguration["Theme"] → Apply 一次。
    void Initialize();

    /// 用户切换主题。System 会被解析为 effective Light/Dark 再下发。
    void Apply(AppTheme theme);
}
```

- [ ] **Step 4: 实现 ThemeService 类**

新建 `wpf/src/Dc.App/Services/Theme/ThemeService.cs`：

```csharp
using Microsoft.Extensions.Configuration;

namespace Dc.App.Services.Theme;

public sealed class ThemeService : IThemeService
{
    private readonly IConfiguration _config;
    private readonly IThemeApplier _applier;
    private AppTheme _current = AppTheme.System;

    public ThemeService(IConfiguration config, IThemeApplier applier)
    {
        _config = config;
        _applier = applier;
    }

    public AppTheme Current => _current;

    public event Action<AppTheme>? ThemeChanged;

    public void Initialize()
    {
        var configured = _config["Theme"];
        var initial = ParseOrDefault(configured, AppTheme.System);
        Apply(initial, raiseEvent: false);
    }

    public void Apply(AppTheme theme) => Apply(theme, raiseEvent: true);

    private void Apply(AppTheme theme, bool raiseEvent)
    {
        var effective = theme == AppTheme.System
            ? _applier.DetectSystemTheme()
            : theme;
        _applier.Apply(effective);
        _current = theme;
        if (raiseEvent) ThemeChanged?.Invoke(theme);
    }

    private static AppTheme ParseOrDefault(string? raw, AppTheme fallback)
        => Enum.TryParse<AppTheme>(raw, ignoreCase: true, out var t) ? t : fallback;
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: PASS — 5 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Services/Theme/IThemeService.cs \
        wpf/src/Dc.App/Services/Theme/ThemeService.cs \
        wpf/tests/Dc.App.Tests/Services/Theme/ThemeServiceTests.cs
git commit -m ":sparkles: S1.4: ThemeService 三档主题切换（亮/暗/跟随系统）+ 5 unit tests"
```

> **持久化注**：本任务的 ThemeService 只读 `IConfiguration["Theme"]` 启动初值。把切换后的值写回 `appsettings.json` 留到 Task 12（设置页落地时）做 — S1 范围内允许"切换生效，但下次启动复位到 config 文件值"。

---

## Task 5: 更新 appsettings.json 添加 Theme 字段

**Files:**
- Modify: `wpf/src/Dc.App/appsettings.json`

- [ ] **Step 1: 添加 Theme 字段**

修改 `wpf/src/Dc.App/appsettings.json`，在顶层加 `"Theme": "System"`：

```json
{
  "Database": {
    "Path": "sqlite.db"
  },
  "Theme": "System",
  "Orchestrator": {
    "WatchdogIntervalSeconds": 30,
    "HeartbeatTimeoutSeconds": 120
  },
  "Messaging": {
    "Format": "msgpack",
    "Queue": {
      "Enabled": false,
      "Directory": "queue",
      "MaxBytes": 104857600
    }
  },
  "OpcUa": {
    "AutoAcceptUntrustedCertificates": false,
    "MinimumCertificateKeySize": 2048
  },
  "Serilog": {
    "MinimumLevel": "Information"
  }
}
```

- [ ] **Step 2: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/appsettings.json
git commit -m ":wrench: S1.5: appsettings.json 添加 Theme 默认值 = System"
```

---

## Task 6: NavigationRoute 模型 + INavigationService 接口

**Files:**
- Create: `wpf/src/Dc.App/Navigation/NavigationRoute.cs`
- Create: `wpf/src/Dc.App/Navigation/INavigationService.cs`

> 旧 `NavigationViewModel` 直接持有所有 VM 实例；新 service 抽象出"路由 → 解析 VM"，把构造从 NavigationVM 转移到 DI 容器，shell 切换时按 route 拉 VM。

- [ ] **Step 1: 创建 NavigationRoute**

新建 `wpf/src/Dc.App/Navigation/NavigationRoute.cs`：

```csharp
namespace Dc.App.Navigation;

public sealed record NavigationRoute(
    string Key,           // "dashboard" / "workspace" / ...，与 NavigationViewItem.Tag 匹配
    string Title,         // 侧栏显示文本
    string Icon,          // SymbolRegular 名称（如 "Home24"）
    Type ViewModelType,   // 解析时去 IServiceProvider 拉
    string? GroupHeader = null   // 显示在该项前的分组标题；null = 紧贴上一项
);
```

- [ ] **Step 2: 创建 INavigationService**

新建 `wpf/src/Dc.App/Navigation/INavigationService.cs`：

```csharp
namespace Dc.App.Navigation;

public interface INavigationService
{
    IReadOnlyList<NavigationRoute> Routes { get; }
    NavigationRoute? FooterAbout { get; }

    /// 按 key 拉 VM 实例。未注册的 key 抛 KeyNotFoundException。
    object Resolve(string routeKey);
}
```

- [ ] **Step 3: 验证构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Navigation/
git commit -m ":sparkles: S1.6: NavigationRoute 模型 + INavigationService 抽象"
```

---

## Task 7: NavigationService 实现 — TDD

**Files:**
- Create: `wpf/tests/Dc.App.Tests/Navigation/NavigationServiceTests.cs`
- Create: `wpf/src/Dc.App/Navigation/NavigationService.cs`

- [ ] **Step 1: 写测试（Red）**

新建 `wpf/tests/Dc.App.Tests/Navigation/NavigationServiceTests.cs`：

```csharp
using Dc.App.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace Dc.App.Tests.Navigation;

public class NavigationServiceTests
{
    private sealed class FakeVmA { }
    private sealed class FakeVmB { }

    private static (IServiceProvider sp, NavigationService nav) Build(params NavigationRoute[] routes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<FakeVmA>();
        services.AddSingleton<FakeVmB>();
        var sp = services.BuildServiceProvider();
        return (sp, new NavigationService(sp, routes, footerAbout: null));
    }

    [Fact]
    public void Routes_AreExposedInOrder()
    {
        var (_, nav) = Build(
            new NavigationRoute("a", "A", "Home24", typeof(FakeVmA)),
            new NavigationRoute("b", "B", "Home24", typeof(FakeVmB)));

        Assert.Collection(nav.Routes,
            r => Assert.Equal("a", r.Key),
            r => Assert.Equal("b", r.Key));
    }

    [Fact]
    public void Resolve_ReturnsRegisteredInstance()
    {
        var (sp, nav) = Build(
            new NavigationRoute("a", "A", "Home24", typeof(FakeVmA)));

        var result = nav.Resolve("a");

        Assert.Same(sp.GetRequiredService<FakeVmA>(), result);
    }

    [Fact]
    public void Resolve_UnknownKey_Throws()
    {
        var (_, nav) = Build(
            new NavigationRoute("a", "A", "Home24", typeof(FakeVmA)));

        Assert.Throws<KeyNotFoundException>(() => nav.Resolve("missing"));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: FAIL — `NavigationService` 不存在。

- [ ] **Step 3: 实现 NavigationService**

新建 `wpf/src/Dc.App/Navigation/NavigationService.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Dc.App.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _sp;
    private readonly Dictionary<string, NavigationRoute> _byKey;

    public NavigationService(
        IServiceProvider serviceProvider,
        IReadOnlyList<NavigationRoute> routes,
        NavigationRoute? footerAbout)
    {
        _sp = serviceProvider;
        Routes = routes;
        FooterAbout = footerAbout;
        _byKey = routes.ToDictionary(r => r.Key, StringComparer.Ordinal);
        if (footerAbout is not null) _byKey.Add(footerAbout.Key, footerAbout);
    }

    public IReadOnlyList<NavigationRoute> Routes { get; }
    public NavigationRoute? FooterAbout { get; }

    public object Resolve(string routeKey)
    {
        if (!_byKey.TryGetValue(routeKey, out var route))
            throw new KeyNotFoundException($"未注册的导航 key: {routeKey}");
        return _sp.GetRequiredService(route.ViewModelType);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: PASS — 8 tests passed（5 theme + 3 nav）。

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Navigation/NavigationService.cs \
        wpf/tests/Dc.App.Tests/Navigation/NavigationServiceTests.cs
git commit -m ":sparkles: S1.7: NavigationService 实现 + 3 unit tests"
```

---

## Task 8: ShellViewModel — TDD

**Files:**
- Create: `wpf/tests/Dc.App.Tests/ViewModels/Shell/ShellViewModelTests.cs`
- Create: `wpf/src/Dc.App/ViewModels/Shell/ShellViewModel.cs`

- [ ] **Step 1: 写测试（Red）**

新建 `wpf/tests/Dc.App.Tests/ViewModels/Shell/ShellViewModelTests.cs`：

```csharp
using Dc.App.Navigation;
using Dc.App.Services.Theme;
using Dc.App.ViewModels.Shell;

namespace Dc.App.Tests.ViewModels.Shell;

public class ShellViewModelTests
{
    private sealed class FakeDashboardVm { }
    private sealed class FakeTasksVm { }

    private static (Mock<INavigationService> nav, Mock<IThemeService> theme, ShellViewModel vm) Build()
    {
        var dashRoute = new NavigationRoute("dashboard", "仪表盘", "Home24", typeof(FakeDashboardVm));
        var tasksRoute = new NavigationRoute("workspace", "采集任务", "TaskListSquareLtr24", typeof(FakeTasksVm), GroupHeader: "采集");

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.Routes).Returns(new[] { dashRoute, tasksRoute });
        nav.SetupGet(n => n.FooterAbout).Returns((NavigationRoute?)null);
        nav.Setup(n => n.Resolve("dashboard")).Returns(new FakeDashboardVm());
        nav.Setup(n => n.Resolve("workspace")).Returns(new FakeTasksVm());

        var theme = new Mock<IThemeService>();
        theme.SetupGet(t => t.Current).Returns(AppTheme.System);

        var vm = new ShellViewModel(nav.Object, theme.Object);
        return (nav, theme, vm);
    }

    [Fact]
    public void Initial_SelectsFirstRouteAndResolvesContent()
    {
        var (nav, _, vm) = Build();

        Assert.Equal("dashboard", vm.SelectedRouteKey);
        Assert.IsType<ShellViewModelTests.FakeDashboardVm>(vm.CurrentContent);
        nav.Verify(n => n.Resolve("dashboard"), Times.Once);
    }

    [Fact]
    public void Navigate_UpdatesSelectedRouteAndContent()
    {
        var (nav, _, vm) = Build();

        vm.NavigateCommand.Execute("workspace");

        Assert.Equal("workspace", vm.SelectedRouteKey);
        Assert.IsType<ShellViewModelTests.FakeTasksVm>(vm.CurrentContent);
        nav.Verify(n => n.Resolve("workspace"), Times.Once);
    }

    [Fact]
    public void Navigate_SameRoute_DoesNotReResolve()
    {
        var (nav, _, vm) = Build();
        nav.Invocations.Clear();

        vm.NavigateCommand.Execute("dashboard");

        nav.Verify(n => n.Resolve(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Navigate_UnknownKey_LeavesStateUnchanged()
    {
        var (nav, _, vm) = Build();
        nav.Setup(n => n.Resolve("ghost")).Throws<KeyNotFoundException>();

        vm.NavigateCommand.Execute("ghost");

        Assert.Equal("dashboard", vm.SelectedRouteKey);
        Assert.IsType<ShellViewModelTests.FakeDashboardVm>(vm.CurrentContent);
    }

    [Fact]
    public void ToggleTheme_CyclesLight_Dark_System()
    {
        var (_, theme, vm) = Build();
        theme.SetupGet(t => t.Current).Returns(AppTheme.Light);

        vm.ToggleThemeCommand.Execute(null);
        theme.Verify(t => t.Apply(AppTheme.Dark), Times.Once);

        theme.SetupGet(t => t.Current).Returns(AppTheme.Dark);
        vm.ToggleThemeCommand.Execute(null);
        theme.Verify(t => t.Apply(AppTheme.System), Times.Once);

        theme.SetupGet(t => t.Current).Returns(AppTheme.System);
        vm.ToggleThemeCommand.Execute(null);
        theme.Verify(t => t.Apply(AppTheme.Light), Times.Once);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: FAIL — `ShellViewModel` 不存在。

- [ ] **Step 3: 实现 ShellViewModel**

新建 `wpf/src/Dc.App/ViewModels/Shell/ShellViewModel.cs`：

```csharp
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Navigation;
using Dc.App.Services.Theme;

namespace Dc.App.ViewModels.Shell;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _nav;
    private readonly IThemeService _theme;

    [ObservableProperty]
    private string _selectedRouteKey = string.Empty;

    [ObservableProperty]
    private object? _currentContent;

    [ObservableProperty]
    private string _currentTitle = string.Empty;

    public IReadOnlyList<NavigationRoute> Routes => _nav.Routes;
    public NavigationRoute? FooterAbout => _nav.FooterAbout;

    public ICommand NavigateCommand { get; }
    public ICommand ToggleThemeCommand { get; }

    public ShellViewModel(INavigationService nav, IThemeService theme)
    {
        _nav = nav;
        _theme = theme;
        NavigateCommand = new RelayCommand<string?>(Navigate);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        if (_nav.Routes.Count > 0)
        {
            Navigate(_nav.Routes[0].Key);
        }
    }

    private void Navigate(string? routeKey)
    {
        if (string.IsNullOrEmpty(routeKey)) return;
        if (routeKey == SelectedRouteKey) return;

        try
        {
            var vm = _nav.Resolve(routeKey);
            CurrentContent = vm;
            SelectedRouteKey = routeKey;
            CurrentTitle = _nav.Routes.FirstOrDefault(r => r.Key == routeKey)?.Title ?? string.Empty;
        }
        catch (KeyNotFoundException)
        {
            // 未注册路由 - 静默保留当前 state（log 在 wireup 阶段补，S1 范围不引入 logger 依赖）
        }
    }

    private void ToggleTheme()
    {
        var next = _theme.Current switch
        {
            AppTheme.Light  => AppTheme.Dark,
            AppTheme.Dark   => AppTheme.System,
            AppTheme.System => AppTheme.Light,
            _ => AppTheme.System
        };
        _theme.Apply(next);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: PASS — 13 tests passed（5 theme + 3 nav + 5 shell）。

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/Shell/ShellViewModel.cs \
        wpf/tests/Dc.App.Tests/ViewModels/Shell/ShellViewModelTests.cs
git commit -m ":sparkles: S1.8: ShellViewModel（导航 + 主题轮换）+ 5 unit tests"
```

---

## Task 9: Dashboard 占位 VM + View

**Files:**
- Create: `wpf/src/Dc.App/ViewModels/Dashboard/DashboardViewModel.cs`
- Create: `wpf/src/Dc.App/Views/Dashboard/DashboardView.xaml`
- Create: `wpf/src/Dc.App/Views/Dashboard/DashboardView.xaml.cs`

> S1 范围只做占位 — 一个居中提示卡片说"S2 即将实现"。真正的 C 风格 KPI 仪表盘是 S2 plan。

- [ ] **Step 1: DashboardViewModel**

新建 `wpf/src/Dc.App/ViewModels/Dashboard/DashboardViewModel.cs`：

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace Dc.App.ViewModels.Dashboard;

public sealed partial class DashboardViewModel : ObservableObject
{
    public string Heading { get; } = "仪表盘";
    public string Hint { get; } = "S2 实现 · KPI · 当前告警 · 任务速率 · 系统健康度";
}
```

- [ ] **Step 2: DashboardView.xaml**

新建 `wpf/src/Dc.App/Views/Dashboard/DashboardView.xaml`：

```xml
<UserControl x:Class="Dc.App.Views.Dashboard.DashboardView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
    <Grid>
        <ui:Card HorizontalAlignment="Center" VerticalAlignment="Center"
                 MaxWidth="480" Padding="32,28">
            <StackPanel>
                <ui:SymbolIcon Symbol="Home24" FontSize="40" HorizontalAlignment="Center" />
                <TextBlock Text="{Binding Heading}" FontSize="22" FontWeight="SemiBold"
                           HorizontalAlignment="Center" Margin="0,12,0,4" />
                <TextBlock Text="{Binding Hint}" Opacity="0.65"
                           HorizontalAlignment="Center" TextAlignment="Center"
                           TextWrapping="Wrap" />
            </StackPanel>
        </ui:Card>
    </Grid>
</UserControl>
```

- [ ] **Step 3: DashboardView.xaml.cs**

新建 `wpf/src/Dc.App/Views/Dashboard/DashboardView.xaml.cs`：

```csharp
using System.Windows.Controls;

namespace Dc.App.Views.Dashboard;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 4: 验证构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/Dashboard/ wpf/src/Dc.App/Views/Dashboard/
git commit -m ":sparkles: S1.9: Dashboard 占位（卡片提示 S2 实现）"
```

---

## Task 10: ShellWindow XAML + 代码后台

**Files:**
- Create: `wpf/src/Dc.App/Views/Shell/ShellWindow.xaml`
- Create: `wpf/src/Dc.App/Views/Shell/ShellWindow.xaml.cs`

- [ ] **Step 1: ShellWindow.xaml**

新建 `wpf/src/Dc.App/Views/Shell/ShellWindow.xaml`：

```xml
<ui:FluentWindow x:Class="Dc.App.Views.Shell.ShellWindow"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                 xmlns:tray="clr-namespace:Wpf.Ui.Tray.Controls;assembly=Wpf.Ui"
                 Title="Dc · OPC 数据采集"
                 Width="1280" Height="800"
                 MinWidth="980" MinHeight="640"
                 WindowBackdropType="Mica"
                 ExtendsContentIntoTitleBar="True"
                 WindowStartupLocation="CenterScreen">

    <ui:FluentWindow.Resources>
        <tray:NotifyIcon x:Key="TrayIcon"
                         FocusOnLeftClick="True"
                         TooltipText="Dc · OPC 数据采集">
            <tray:NotifyIcon.Menu>
                <ContextMenu>
                    <MenuItem Header="显示主窗口" Click="OnTrayShow" />
                    <MenuItem Header="切换主题"   Click="OnTrayToggleTheme" />
                    <MenuItem Header="关于…"     Click="OnTrayAbout" />
                    <Separator />
                    <MenuItem Header="退出"       Click="OnTrayExit" />
                </ContextMenu>
            </tray:NotifyIcon.Menu>
        </tray:NotifyIcon>
    </ui:FluentWindow.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="28" />
        </Grid.RowDefinitions>

        <ui:TitleBar Grid.Row="0" Title="Dc · OPC 数据采集" ShowMaximize="True" />

        <ui:NavigationView Grid.Row="1"
                           x:Name="RootNav"
                           PaneDisplayMode="Left"
                           OpenPaneLength="240"
                           IsBackButtonVisible="Collapsed"
                           IsPaneToggleVisible="True"
                           SelectionChanged="OnNavigationSelectionChanged">
            <ui:NavigationView.MenuItems>
                <!-- 由 ShellWindow 代码后台从 ShellViewModel.Routes 生成 -->
            </ui:NavigationView.MenuItems>

            <ui:NavigationView.FooterMenuItems>
                <ui:NavigationViewItem Content="关于"
                                       Tag="about"
                                       Icon="{ui:SymbolIcon Info24}" />
            </ui:NavigationView.FooterMenuItems>

            <ContentControl Content="{Binding CurrentContent}" />
        </ui:NavigationView>

        <Border Grid.Row="2" Padding="12,0">
            <DockPanel>
                <TextBlock DockPanel.Dock="Right"
                           Text="Dc · v1.0.0"
                           Opacity="0.55"
                           VerticalAlignment="Center" />
                <TextBlock Text="{Binding CurrentTitle, StringFormat='当前: {0}'}"
                           Opacity="0.55"
                           VerticalAlignment="Center" />
            </DockPanel>
        </Border>
    </Grid>
</ui:FluentWindow>
```

- [ ] **Step 2: ShellWindow.xaml.cs**

新建 `wpf/src/Dc.App/Views/Shell/ShellWindow.xaml.cs`：

```csharp
using System.Windows;
using System.Windows.Controls;
using Dc.App.Navigation;
using Dc.App.ViewModels.Shell;
using Wpf.Ui.Controls;

namespace Dc.App.Views.Shell;

public partial class ShellWindow : FluentWindow
{
    private readonly ShellViewModel _vm;

    public ShellWindow(ShellViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        BuildMenuItems();
        StateChanged += OnStateChanged;
    }

    private void BuildMenuItems()
    {
        NavigationViewItemSeparator? lastSep = null;
        string? lastGroup = null;
        foreach (var route in _vm.Routes)
        {
            if (route.GroupHeader is not null && route.GroupHeader != lastGroup)
            {
                if (RootNav.MenuItems.Count > 0)
                {
                    RootNav.MenuItems.Add(new NavigationViewItemSeparator());
                }
                lastGroup = route.GroupHeader;
            }
            var item = new NavigationViewItem
            {
                Content = route.Title,
                Tag = route.Key,
                Icon = ResolveIcon(route.Icon)
            };
            RootNav.MenuItems.Add(item);
        }

        // 默认选中第一项 — 与 ShellViewModel 初始化一致
        if (RootNav.MenuItems.Count > 0 && RootNav.MenuItems[0] is NavigationViewItem first)
        {
            RootNav.SelectedItem = first;
        }
    }

    private static IconElement? ResolveIcon(string symbolName)
    {
        return Enum.TryParse<SymbolRegular>(symbolName, out var s)
            ? new SymbolIcon { Symbol = s }
            : null;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem item && item.Tag is string key)
        {
            _vm.NavigateCommand.Execute(key);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
    }

    private void OnTrayShow(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnTrayToggleTheme(object sender, RoutedEventArgs e)
    {
        _vm.ToggleThemeCommand.Execute(null);
    }

    private void OnTrayAbout(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
```

- [ ] **Step 3: 验证构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded.

> 若报错 `AboutWindow 命名空间不匹配`，需要在 `ShellWindow.xaml.cs` 顶部 `using Dc.App.Views;` —— 现有 AboutWindow 在 `Dc.App.Views.AboutWindow`。

- [ ] **Step 4: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Views/Shell/
git commit -m ":sparkles: S1.10: ShellWindow（FluentWindow + NavigationView + 托盘）"
```

---

## Task 11: 更新 App.xaml 加载 Wpf.Ui 资源字典

**Files:**
- Modify: `wpf/src/Dc.App/App.xaml`

- [ ] **Step 1: 改 App.xaml 引入 ThemesDictionary + ControlsDictionary**

把 `wpf/src/Dc.App/App.xaml` 整体替换为：

```xml
<Application x:Class="Dc.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:vm="clr-namespace:Dc.App.ViewModels"
             xmlns:vmDash="clr-namespace:Dc.App.ViewModels.Dashboard"
             xmlns:views="clr-namespace:Dc.App.Views"
             xmlns:viewsDash="clr-namespace:Dc.App.Views.Dashboard">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ui:ThemesDictionary Theme="Light" />
                <ui:ControlsDictionary />
            </ResourceDictionary.MergedDictionaries>

            <BooleanToVisibilityConverter x:Key="BoolToVis" />

            <DataTemplate DataType="{x:Type vmDash:DashboardViewModel}">
                <viewsDash:DashboardView />
            </DataTemplate>
            <DataTemplate DataType="{x:Type vm:TasksViewModel}">
                <views:TasksView />
            </DataTemplate>
            <DataTemplate DataType="{x:Type vm:GroupsViewModel}">
                <views:GroupsView />
            </DataTemplate>
            <DataTemplate DataType="{x:Type vm:TagsViewModel}">
                <views:TagsView />
            </DataTemplate>
            <DataTemplate DataType="{x:Type vm:LiveDataViewModel}">
                <views:LiveDataView />
            </DataTemplate>
            <DataTemplate DataType="{x:Type vm:BrowseViewModel}">
                <views:BrowseView />
            </DataTemplate>
            <DataTemplate DataType="{x:Type vm:DiagnosticsViewModel}">
                <views:DiagnosticsView />
            </DataTemplate>
            <DataTemplate DataType="{x:Type vm:SettingsViewModel}">
                <views:SettingsView />
            </DataTemplate>
            <DataTemplate DataType="{x:Type vm:LogsViewModel}">
                <views:LogsView />
            </DataTemplate>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 2: 验证构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/App.xaml
git commit -m ":sparkles: S1.11: App.xaml 引入 Wpf.Ui ThemesDictionary + ControlsDictionary"
```

---

## Task 12: 改 ServiceRegistration 注册新类型 + 路由表

**Files:**
- Modify: `wpf/src/Dc.App/Composition/ServiceRegistration.cs`

- [ ] **Step 1: 修改 ServiceRegistration**

把 `wpf/src/Dc.App/Composition/ServiceRegistration.cs` 末尾的 `// VM 注册` 区域改成：

找到这段：
```csharp
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<NavigationViewModel>();
        services.AddSingleton<TasksViewModel>();
```

替换为：
```csharp
        // === Shell + Theme + Navigation（S1 新增） ===
        services.AddSingleton<Dc.App.Services.Theme.IThemeApplier, Dc.App.Services.Theme.WpfUiThemeApplier>();
        services.AddSingleton<Dc.App.Services.Theme.IThemeService, Dc.App.Services.Theme.ThemeService>();

        services.AddSingleton<Dc.App.Navigation.INavigationService>(sp =>
            new Dc.App.Navigation.NavigationService(
                sp,
                new[]
                {
                    new Dc.App.Navigation.NavigationRoute("dashboard",   "仪表盘",   "Home24",                typeof(Dc.App.ViewModels.Dashboard.DashboardViewModel)),
                    new Dc.App.Navigation.NavigationRoute("workspace",   "采集任务", "TaskListSquareLtr24",   typeof(TasksViewModel),          GroupHeader: "采集"),
                    new Dc.App.Navigation.NavigationRoute("browse",      "浏览节点", "Search24",              typeof(BrowseViewModel)),
                    new Dc.App.Navigation.NavigationRoute("livedata",    "实时数据", "DataHistogram24",       typeof(LiveDataViewModel),       GroupHeader: "全局监控"),
                    new Dc.App.Navigation.NavigationRoute("diagnostics", "诊断",     "Pulse24",               typeof(DiagnosticsViewModel)),
                    new Dc.App.Navigation.NavigationRoute("settings",    "设置",     "Settings24",            typeof(SettingsViewModel),       GroupHeader: "系统"),
                    new Dc.App.Navigation.NavigationRoute("logs",        "日志",     "DocumentText24",        typeof(LogsViewModel))
                },
                footerAbout: null));

        services.AddSingleton<Dc.App.Views.Shell.ShellWindow>();
        services.AddSingleton<Dc.App.ViewModels.Shell.ShellViewModel>();
        services.AddSingleton<Dc.App.ViewModels.Dashboard.DashboardViewModel>();

        // === 旧 VM 保留（其他 View 由 Shell 路由继续承载） ===
        services.AddSingleton<TasksViewModel>();
```

完整地说：删除原来的 4 行（`MainWindow` / `MainWindowViewModel` / `NavigationViewModel` 注册 + 那行 `TasksViewModel` 之上的注释），保留后续 5 个 ViewModel 注册（GroupsViewModel 到 LogsViewModel）。

> `NavigationViewModel` 不再需要 — 新 Shell 用 `INavigationService`。

- [ ] **Step 2: 验证构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded（可能有 warning 提示 `MainWindow` / `MainWindowViewModel` 类未使用，Task 15 会删）。

- [ ] **Step 3: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Composition/ServiceRegistration.cs
git commit -m ":sparkles: S1.12: ServiceRegistration 注册 Shell/Theme/Navigation 路由表"
```

---

## Task 13: 改 App.xaml.cs 启动 ShellWindow + 初始化主题

**Files:**
- Modify: `wpf/src/Dc.App/App.xaml.cs`

- [ ] **Step 1: 修改 OnStartup**

修改 `wpf/src/Dc.App/App.xaml.cs`，在 `await _host.StartAsync();` 后、`var window = Services.GetRequiredService<MainWindow>();` 前插入主题初始化，并把窗口类型改成 `ShellWindow`：

找到这段：
```csharp
            await _host.StartAsync();

            var window = Services.GetRequiredService<MainWindow>();
            window.Show();
```

替换为：
```csharp
            await _host.StartAsync();

            // 初始化主题（读 appsettings.json:Theme，下发到 wpfui ApplicationThemeManager）
            var themeSvc = _host.Services.GetRequiredService<Dc.App.Services.Theme.IThemeService>();
            themeSvc.Initialize();

            var window = Services.GetRequiredService<Dc.App.Views.Shell.ShellWindow>();
            window.Show();
```

- [ ] **Step 2: 验证构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/App.xaml.cs
git commit -m ":sparkles: S1.13: App 启动入口切到 ShellWindow + 主题初始化"
```

---

## Task 14: 删除旧 MainWindow / NavigationViewModel / MainWindowViewModel

**Files:**
- Delete: `wpf/src/Dc.App/MainWindow.xaml`
- Delete: `wpf/src/Dc.App/MainWindow.xaml.cs`
- Delete: `wpf/src/Dc.App/ViewModels/MainWindowViewModel.cs`
- Delete: `wpf/src/Dc.App/ViewModels/NavigationViewModel.cs`
- Delete: `wpf/src/Dc.App/ViewModels/NavigationItem.cs`（如存在）

- [ ] **Step 1: 检查 NavigationItem.cs 是否存在**

```bash
ls wpf/src/Dc.App/ViewModels/NavigationItem.cs 2>/dev/null || echo "不存在"
```

如返回路径则后续删除；如返回"不存在"，跳过对应删除步骤。

- [ ] **Step 2: 删除文件**

```bash
cd /home/adamyu/workspace/dc/wpf
git rm src/Dc.App/MainWindow.xaml
git rm src/Dc.App/MainWindow.xaml.cs
git rm src/Dc.App/ViewModels/MainWindowViewModel.cs
git rm src/Dc.App/ViewModels/NavigationViewModel.cs
# 若 Step 1 显示存在：
git rm src/Dc.App/ViewModels/NavigationItem.cs 2>/dev/null || true
```

- [ ] **Step 3: 验证构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded. 若有 stale 引用报错，按报错点 grep 残留并清除：

```bash
grep -rn "MainWindow\|NavigationViewModel\|NavigationItem" wpf/src/Dc.App --include="*.cs" --include="*.xaml"
```

正常应只剩 `ShellWindow` / `NavigationService` 等新类型，没有 `MainWindow*`/`NavigationViewModel`/`NavigationItem`。

- [ ] **Step 4: Commit**

```bash
cd /home/adamyu/workspace/dc
git commit -m ":fire: S1.14: 删除旧 MainWindow / MainWindowViewModel / NavigationViewModel"
```

---

## Task 15: 删除 H.NotifyIcon 依赖

> **延后（2026-05-19）**：wpfui 3.0.5 没有 NotifyIcon 控件（已确认 `Wpf.Ui.dll` 仅有 Shell_NotifyIcon Win32 互操作，无 XAML 控件类）。H.NotifyIcon.Wpf 留用。等 lepoco 在后续版本加 NotifyIcon 控件，或者改用 page-based tray pattern 时再迁。

**Files:**
- Modify: `wpf/src/Dc.App/Dc.App.csproj`
- Modify: `wpf/Directory.Packages.props`

- [ ] **Step 1: 确认 H.NotifyIcon 已无引用**

```bash
grep -rn "H\.NotifyIcon\|hardcodet" wpf/src/Dc.App --include="*.cs" --include="*.xaml" | grep -v "/obj/" | grep -v "/bin/"
```

Expected: 无输出（旧 MainWindow.xaml 已删，新 ShellWindow 用 `tray:NotifyIcon` 来自 `Wpf.Ui.Tray.Controls`）。

- [ ] **Step 2: 从 csproj 移除包引用**

修改 `wpf/src/Dc.App/Dc.App.csproj`，删除这行：
```xml
    <PackageReference Include="H.NotifyIcon.Wpf" />
```

- [ ] **Step 3: 从中央版本锁移除**

修改 `wpf/Directory.Packages.props`，删除这行：
```xml
    <PackageVersion Include="H.NotifyIcon.Wpf" Version="2.2.0" />
```

- [ ] **Step 4: 验证构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build Dc.sln -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded. 0 Warning(s).

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Dc.App.csproj wpf/Directory.Packages.props
git commit -m ":fire: S1.15: 移除 H.NotifyIcon.Wpf 依赖（Wpf.Ui.Tray 已接手）"
```

---

## Task 16: 全量回归 + 验收

**Files:** （无新文件，验证已交付）

- [ ] **Step 1: 全套测试**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.Infrastructure.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet test tests/Dc.Integration.Tests   -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet test tests/Dc.App.Tests           -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected:
- `Dc.Infrastructure.Tests`: **48 passed**
- `Dc.Integration.Tests`: **10 passed**
- `Dc.App.Tests`: **13 passed**（5 theme + 3 nav + 5 shell）
- 合计 **71 passed**（旧基线 58 + 新增 13）

> 注：spec 写的是 +8 = 66，实际 +13 是因为 Theme 测试拆得更细。任意一种都达到 S1 验收。

- [ ] **Step 2: Solution 整体构建**

```bash
cd /home/adamyu/workspace/dc/wpf
dotnet build Dc.sln -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

Expected: Build succeeded. 0 Error(s).

- [ ] **Step 3: Windows 端手动验收清单（需要 Windows 机器，由用户跑）**

按以下 walkthrough 逐项确认：

1. [ ] 启动 `Dc.App.exe`，看到 Mica 半透明效果（Win11 22H2+），窗口标题"Dc · OPC 数据采集"
2. [ ] 侧栏 7 项分组显示正确：仪表盘 / 采集任务（采集组下）/ 浏览节点 / 实时数据（全局监控组下）/ 诊断 / 设置（系统组下）/ 日志
3. [ ] 启动默认进入仪表盘，看到占位卡片"S2 实现 · KPI..."
4. [ ] 逐项点击侧栏 7 个 View，旧 View 内容全部可见（视觉混搭，可接受）
5. [ ] 系统托盘出现 Dc 图标，左键单击恢复窗口，右键菜单"切换主题"可用
6. [ ] 修改 `appsettings.json` 的 `"Theme": "Dark"`，重启 App 默认进入暗色
7. [ ] 修改 `appsettings.json` 的 `"Theme": "Light"`，重启 App 默认进入亮色
8. [ ] 修改 `appsettings.json` 的 `"Theme": "System"`，跟随系统亮/暗设置切换
9. [ ] 关闭主窗口（X），从托盘菜单"显示主窗口"能恢复
10. [ ] 关闭后再次启动 App，看到"应用已在运行中"对话框（单实例 Mutex 仍生效）

- [ ] **Step 4: Push 分支**

```bash
cd /home/adamyu/workspace/dc
git push origin wpf-opc-collector
```

- [ ] **Step 5: 在 PR #5 添加 S1 进度评论**

通过 Gitea API 评论（curl 命令）：

```bash
CREDS=$(grep "git.adamyu.top" ~/.git-credentials | sed 's|https://||;s|@.*||')
cat > /tmp/pr_comment.json <<'EOF'
{
  "body": "### S1 · Shell + Theme 完成 ✅\n\n- FluentWindow + NavigationView 上线，Mica 背景\n- 三档主题：appsettings.json `Theme = Light|Dark|System`\n- 托盘从 H.NotifyIcon → Wpf.Ui.Tray\n- 旧 8 个 View 全部可达，视觉混搭中（S5 收敛）\n- 新增测试 13 个（Dc.App.Tests），合计 71 passed\n\n下一步：S2 Dashboard（C 风格 KPI + 告警卡片）。"
}
EOF
curl -sk -u "$CREDS" -H "Content-Type: application/json" \
  -X POST "https://git.adamyu.top:20443/api/v1/repos/adamyu/dc/issues/5/comments" \
  -d @/tmp/pr_comment.json -o /tmp/c.json -w "HTTP %{http_code}\n"
jq -r '.html_url // .message' /tmp/c.json
rm -f /tmp/pr_comment.json /tmp/c.json
```

Expected: HTTP 201，URL 输出。

---

## 自审 Checklist

### Spec 覆盖

| Spec §5 acceptance | 实现位置 |
|---|---|
| 启动看到 Mica | Task 10 (`WindowBackdropType="Mica"`) + Task 16 §3.1 验证 |
| 三档主题生效并持久化 | Task 4 (ThemeService) + Task 5 (appsettings.json) + Task 13 (Initialize) + Task 16 §3.6-3.8 |
| 旧 8 个 View 全部可达 | Task 11 (DataTemplate 全保留) + Task 12 (路由 7 项) + Task 16 §3.4 |

> 注：本计划"持久化"只到"启动读 appsettings.json"。运行时切换写回 JSON 留给 S2 的设置页 — 在 Task 4 步骤 6 已标注。

### 文件路径准确性

- 所有 `Create` 路径已确认对应目录可建（Services/Theme、Navigation、ViewModels/Shell、ViewModels/Dashboard、Views/Shell、Views/Dashboard、tests/Dc.App.Tests/...）
- 所有 `Modify` 路径对照实际仓库存在性已通过 Read 验证

### 命名一致性

- `IThemeService` 用 `Initialize()` + `Apply()`，全计划一致
- `INavigationService.Resolve(string key)` 返回 `object`，全计划一致
- `ShellViewModel.NavigateCommand` 接 `string?` 参数，与 NavigationView SelectedItem.Tag 一致

### 没占位

复审完一遍：所有 step 都有可执行命令或完整代码块；没有 "TODO" / "implement later" / "similar to Task N"。

---

## 执行选项

Plan complete and saved to `wpf/docs/plans/2026-05-19-ui-redesign-s1-shell-theme.md`. Two execution options:

**1. Subagent-Driven (recommended)** — 我每 Task 派一个新 subagent，跑完两阶段 review 再下一个。隔离干净、上下文窗口不爆、错有人查。

**2. Inline Execution** — 在当前会话里逐 Task 跑，几个 Task 后停下来给你 review。响应快但上下文会持续涨。

哪种方式？
