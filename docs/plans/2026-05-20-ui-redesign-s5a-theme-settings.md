# UI 重设计 S5a — Settings 主题开关 + 持久化 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).

**Goal:** 补 S1 延后的两件事 —— ① Settings 页加三档主题切换 UI（亮/暗/跟随系统），实时调 `IThemeService.Apply`；② 主题选择持久化到 `appsettings.json`（之前只在启动时读，切换重启即丢）。纯视觉重样（Browse/Logs 卡片化）不在本计划，留 Windows 上做。

**Architecture:**
新增 `IThemePreferenceWriter` 抽象 + `JsonThemePreferenceWriter`（用 JsonNode 可变 DOM 改写 appsettings.json 的 `Theme` 键，保留其他键）。`ThemeService` 注入可选 writer，用户态 `Apply` 后写盘（`Initialize` 读取时不写）。`SettingsViewModel` 注入 `IThemeService`，暴露 `SelectedTheme`（三档），setter 调 `Apply`。

**Tech Stack:** .NET 8 + WPF + Wpf.Ui + CommunityToolkit.Mvvm + System.Text.Json + xUnit + Moq

**Spec:** `wpf/docs/specs/2026-05-19-ui-redesign-fluent-design.md` (§1.4/§3.4 主题三档 + 持久化；S1 Task 4 标注 deferred)
**前置:** S4 完成（commit 0263d8f）

---

## 已锁定决策

| 项 | 决策 |
|---|---|
| 范围 | 仅 Settings 主题开关 + 持久化；Browse/Logs 视觉重样留 Windows |
| 主题档位 | 亮 / 暗 / 跟随系统（复用 `AppTheme` 枚举） |
| 持久化目标 | `appsettings.json` 的顶层 `Theme` 键（用 JsonNode 改写保留其他键） |
| 持久化时机 | 用户态 `Apply` 写盘；`Initialize`（启动读取）不写 |
| 写盘路径 | `Path.Combine(AppContext.BaseDirectory, "appsettings.json")`（与启动读取同一文件） |
| 失败容错 | 写盘异常吞掉 + 不崩（主题已应用，持久化失败仅影响下次启动） |

---

## 前置说明

dotnet 在 `/home/adamyu/.dotnet/dotnet`，PATH 先 export。Linux 上 Dc.App.Tests 跑不了 net8.0-windows runtime，build 验证为准；但 `JsonThemePreferenceWriter` 是纯 .NET 文件 IO 逻辑，其单测**理论上可跑**——若 Dc.App.Tests 整体 runtime 跑不了，至少 build 通过即可。

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

测试基线：infra 48 + integration 10 + Dc.App.Tests ~46。S5a 完成 +约 7。

确认的既有 API：
- `AppTheme { Light, Dark, System }`（`Dc.App.Services.Theme`）
- `IThemeService`（`Dc.App.Services.Theme`）：`AppTheme Current`、`event Action<AppTheme>? ThemeChanged`、`void Initialize()`、`void Apply(AppTheme)`
- `ThemeService` 现 ctor：`(IConfiguration config, IThemeApplier applier)`，内有私有 `Apply(AppTheme theme, bool raiseEvent)`，`Initialize` 调 `Apply(initial, raiseEvent:false)`，公开 `Apply` 调 `Apply(theme, raiseEvent:true)`
- ThemeService 已注册 `AddSingleton<IThemeService, ThemeService>()`（自动构造）
- `SettingsViewModel` ctor：`(IDbContextFactory<DcDbContext>, IConfigEditorDialog, IConfigBackupService, IFilePicker)`，注册为 `AddSingleton<SettingsViewModel>()`（自动构造）
- `appsettings.json` 顶层已有 `"Theme": "System"`（S1.5）

---

## Task 1: IThemePreferenceWriter + JsonThemePreferenceWriter (TDD)

**Files:**
- Create: `wpf/src/Dc.App/Services/Theme/IThemePreferenceWriter.cs`
- Create: `wpf/src/Dc.App/Services/Theme/JsonThemePreferenceWriter.cs`
- Create: `wpf/tests/Dc.App.Tests/Services/Theme/JsonThemePreferenceWriterTests.cs`

- [ ] **Step 1: 接口**

`wpf/src/Dc.App/Services/Theme/IThemePreferenceWriter.cs`:

```csharp
namespace Dc.App.Services.Theme;

public interface IThemePreferenceWriter
{
    /// 把主题选择写入持久化（appsettings.json 的 Theme 键）。失败不应抛。
    void Write(AppTheme theme);
}
```

- [ ] **Step 2: 写测试（Red）**

`wpf/tests/Dc.App.Tests/Services/Theme/JsonThemePreferenceWriterTests.cs`:

```csharp
using System.IO;
using System.Text.Json;
using Dc.App.Services.Theme;

namespace Dc.App.Tests.Services.Theme;

public class JsonThemePreferenceWriterTests
{
    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-theme-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Write_UpdatesThemeKey_PreservesOtherKeys()
    {
        var path = TempFile("""
        {
          "Database": { "Path": "sqlite.db" },
          "Theme": "System",
          "Serilog": { "MinimumLevel": "Information" }
        }
        """);
        try
        {
            new JsonThemePreferenceWriter(path).Write(AppTheme.Dark);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal("Dark", root.GetProperty("Theme").GetString());
            // 其他键保留
            Assert.Equal("sqlite.db", root.GetProperty("Database").GetProperty("Path").GetString());
            Assert.Equal("Information", root.GetProperty("Serilog").GetProperty("MinimumLevel").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_AddsThemeKey_WhenMissing()
    {
        var path = TempFile("""{ "Database": { "Path": "sqlite.db" } }""");
        try
        {
            new JsonThemePreferenceWriter(path).Write(AppTheme.Light);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("Light", doc.RootElement.GetProperty("Theme").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_MissingFile_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-missing-{Guid.NewGuid():N}.json");
        // 文件不存在：不抛（容错）。可选：创建出含 Theme 的文件。
        var ex = Record.Exception(() => new JsonThemePreferenceWriter(path).Write(AppTheme.Dark));
        Assert.Null(ex);
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Write_MalformedJson_DoesNotThrow()
    {
        var path = TempFile("{ this is not json ");
        try
        {
            var ex = Record.Exception(() => new JsonThemePreferenceWriter(path).Write(AppTheme.Dark));
            Assert.Null(ex);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 3: Red**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: FAILED — JsonThemePreferenceWriter 不存在。

- [ ] **Step 4: 实现**

`wpf/src/Dc.App/Services/Theme/JsonThemePreferenceWriter.cs`:

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dc.App.Services.Theme;

public sealed class JsonThemePreferenceWriter : IThemePreferenceWriter
{
    private readonly string _path;

    public JsonThemePreferenceWriter(string path) => _path = path;

    public void Write(AppTheme theme)
    {
        try
        {
            if (!File.Exists(_path)) return;   // 无文件：放弃持久化，不崩
            var text = File.ReadAllText(_path);
            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch (JsonException) { return; }  // 坏 JSON：放弃，不崩
            if (root is not JsonObject obj) return;

            obj["Theme"] = theme.ToString();
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_path, obj.ToJsonString(options));
        }
        catch (IOException) { /* 写盘失败：吞掉，主题已应用 */ }
        catch (UnauthorizedAccessException) { /* 只读文件：吞掉 */ }
    }
}
```

- [ ] **Step 5: Green**

```bash
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: Build succeeded。（若 Dc.App.Tests runtime 能在本机跑，4 个测试应过；跑不了则 build 通过即可。）

- [ ] **Step 6: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Services/Theme/IThemePreferenceWriter.cs \
        wpf/src/Dc.App/Services/Theme/JsonThemePreferenceWriter.cs \
        wpf/tests/Dc.App.Tests/Services/Theme/JsonThemePreferenceWriterTests.cs
git commit -m ":sparkles: S5a.1: JsonThemePreferenceWriter 写回 appsettings.json（4 unit tests）"
```

---

## Task 2: ThemeService 持久化接入 (TDD)

**Files:**
- Modify: `wpf/src/Dc.App/Services/Theme/ThemeService.cs`
- Modify: `wpf/tests/Dc.App.Tests/Services/Theme/ThemeServiceTests.cs`

> ctor 加可选 `IThemePreferenceWriter? writer = null`（默认 null 让现有 6 测试不破）。用户态 `Apply` 写盘；`Initialize` 不写。

- [ ] **Step 1: 加测试（Red）**

在 `ThemeServiceTests.cs` 追加（沿用现有 Mock<IThemeApplier> 风格，加 Mock<IThemePreferenceWriter>）：

```csharp
    [Fact]
    public void Apply_PersistsViaWriter()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);
        var writer = new Mock<IThemePreferenceWriter>();
        var svc = new ThemeService(ConfigWithTheme(null), applier.Object, writer.Object);
        svc.Initialize();

        svc.Apply(AppTheme.Dark);

        writer.Verify(w => w.Write(AppTheme.Dark), Times.Once);
    }

    [Fact]
    public void Initialize_DoesNotPersist()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);
        var writer = new Mock<IThemePreferenceWriter>();
        var svc = new ThemeService(ConfigWithTheme("Dark"), applier.Object, writer.Object);

        svc.Initialize();

        writer.Verify(w => w.Write(It.IsAny<AppTheme>()), Times.Never);
    }
```

- [ ] **Step 2: Red**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: FAILED — ThemeService 三参 ctor 不存在。

- [ ] **Step 3: 改 ThemeService**

读现有 `ThemeService.cs`。改 ctor：

```csharp
    private readonly IThemePreferenceWriter? _writer;

    public ThemeService(IConfiguration config, IThemeApplier applier, IThemePreferenceWriter? writer = null)
    {
        _config = config;
        _applier = applier;
        _writer = writer;
    }
```

在私有 `Apply(AppTheme theme, bool raiseEvent)` 里，**仅当 raiseEvent==true**（即用户态）持久化：

```csharp
    private void Apply(AppTheme theme, bool raiseEvent)
    {
        var effective = theme == AppTheme.System ? _applier.DetectSystemTheme() : theme;
        _applier.Apply(effective);
        _current = theme;
        if (raiseEvent)
        {
            _writer?.Write(theme);
            ThemeChanged?.Invoke(theme);
        }
    }
```

（保留现有其余逻辑不动。）

- [ ] **Step 4: Green**

```bash
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: Build succeeded（现有 6 + 新 2 = 8 ThemeService 测试编译通过；旧测试因 writer 可选不破）。

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Services/Theme/ThemeService.cs \
        wpf/tests/Dc.App.Tests/Services/Theme/ThemeServiceTests.cs
git commit -m ":sparkles: S5a.2: ThemeService 用户态 Apply 持久化主题（2 unit tests）"
```

---

## Task 3: SettingsViewModel 主题开关 (TDD)

**Files:**
- Modify: `wpf/src/Dc.App/ViewModels/SettingsViewModel.cs`
- Create: `wpf/tests/Dc.App.Tests/ViewModels/SettingsViewModelThemeTests.cs`

> 加 `SelectedTheme`（AppTheme），初值取 `IThemeService.Current`，setter 调 `Apply`。注意避免回环：`Apply` 不应再触发 setter 死循环 —— 用守卫。

- [ ] **Step 1: 写测试（Red）**

`wpf/tests/Dc.App.Tests/ViewModels/SettingsViewModelThemeTests.cs`:

```csharp
using Dc.App.Services.Theme;
using Dc.App.ViewModels;

namespace Dc.App.Tests.ViewModels;

public class SettingsViewModelThemeTests
{
    // SettingsViewModel ctor 较重（DbFactory/dialog/backup/filePicker）。
    // 为单测主题部分，本测试只构造 ThemeSection 子逻辑：把主题开关抽成独立小 VM。
    // 见实现：新增 ThemeSettingsViewModel(IThemeService)，SettingsViewModel 持有它。

    private sealed class FakeThemeService : IThemeService
    {
        public AppTheme Current { get; private set; } = AppTheme.System;
        public event Action<AppTheme>? ThemeChanged;
        public int ApplyCount;
        public void Initialize() { }
        public void Apply(AppTheme theme) { ApplyCount++; Current = theme; ThemeChanged?.Invoke(theme); }
    }

    [Fact]
    public void Initial_SelectedThemeMatchesService()
    {
        var svc = new FakeThemeService();
        svc.Apply(AppTheme.Dark);
        var vm = new ThemeSettingsViewModel(svc);
        Assert.Equal(AppTheme.Dark, vm.SelectedTheme);
    }

    [Fact]
    public void SettingSelectedTheme_CallsApply()
    {
        var svc = new FakeThemeService();
        var vm = new ThemeSettingsViewModel(svc);
        vm.SelectedTheme = AppTheme.Light;
        Assert.Equal(AppTheme.Light, svc.Current);
        Assert.True(svc.ApplyCount >= 1);
    }

    [Fact]
    public void ThemeChangedExternally_UpdatesSelectedTheme()
    {
        var svc = new FakeThemeService();
        var vm = new ThemeSettingsViewModel(svc);
        // 外部（如托盘菜单）切主题 → VM 同步
        svc.Apply(AppTheme.Dark);
        Assert.Equal(AppTheme.Dark, vm.SelectedTheme);
    }

    [Fact]
    public void SettingSameTheme_DoesNotReapplyInfinitely()
    {
        var svc = new FakeThemeService();
        var vm = new ThemeSettingsViewModel(svc);
        vm.SelectedTheme = AppTheme.Light;
        var countAfterFirst = svc.ApplyCount;
        vm.SelectedTheme = AppTheme.Light;   // 同值
        Assert.Equal(countAfterFirst, svc.ApplyCount);  // 不重复 Apply
    }
}
```

> 设计取舍：把主题开关抽成独立 `ThemeSettingsViewModel(IThemeService)`，比把逻辑塞进重 ctor 的 SettingsViewModel 更可测。SettingsViewModel 持有一个 `Theme` 属性指向它。

- [ ] **Step 2: Red**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: FAILED — ThemeSettingsViewModel 不存在。

- [ ] **Step 3: 实现 ThemeSettingsViewModel**

`wpf/src/Dc.App/ViewModels/ThemeSettingsViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Dc.App.Services.Theme;

namespace Dc.App.ViewModels;

public sealed partial class ThemeSettingsViewModel : ObservableObject
{
    private readonly IThemeService _theme;
    private bool _syncing;

    [ObservableProperty] private AppTheme _selectedTheme;

    public ThemeSettingsViewModel(IThemeService theme)
    {
        _theme = theme;
        _selectedTheme = theme.Current;
        _theme.ThemeChanged += OnServiceThemeChanged;
    }

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        if (_syncing) return;             // 来自服务回调的同步，不回写
        if (_theme.Current == value) return;
        _theme.Apply(value);
    }

    private void OnServiceThemeChanged(AppTheme theme)
    {
        _syncing = true;
        SelectedTheme = theme;
        _syncing = false;
    }
}
```

- [ ] **Step 4: SettingsViewModel 持有它**

改 `SettingsViewModel`：ctor 追加 `IThemeService theme` 参数，加 `public ThemeSettingsViewModel Theme { get; }`，ctor 内 `Theme = new ThemeSettingsViewModel(theme);`。加 `using Dc.App.Services.Theme;`。

- [ ] **Step 5: Green**

```bash
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```

Expected: 两个 Build succeeded。

- [ ] **Step 6: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/ThemeSettingsViewModel.cs \
        wpf/src/Dc.App/ViewModels/SettingsViewModel.cs \
        wpf/tests/Dc.App.Tests/ViewModels/SettingsViewModelThemeTests.cs
git commit -m ":sparkles: S5a.3: ThemeSettingsViewModel 三档主题开关（4 unit tests）"
```

---

## Task 4: SettingsView 外观 section + DI 接线

**Files:**
- Modify: `wpf/src/Dc.App/Views/SettingsView.xaml`
- Modify: `wpf/src/Dc.App/Composition/ServiceRegistration.cs`
- Create: `wpf/src/Dc.App/Views/Converters/EnumMatchConverter.cs`（radio 绑定 enum）

- [ ] **Step 1: enum↔radio 转换器**

`wpf/src/Dc.App/Views/Converters/EnumMatchConverter.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;

namespace Dc.App.Views.Converters;

/// RadioButton.IsChecked ↔ enum：ConverterParameter 传枚举名，匹配则 true。
/// 选中时 ConvertBack 返回该枚举值。
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null && targetType.IsEnum)
            return Enum.Parse(targetType, parameter.ToString()!);
        return Binding.DoNothing;
    }
}
```

- [ ] **Step 2: SettingsView.xaml 加外观 section**

读现有 `SettingsView.xaml`（54 行，ConfigEntry 列表 + 备份按钮）。在合适位置（顶部或底部）加一个「外观」分区。先在 UserControl 根加 xmlns + 资源：

```xml
xmlns:conv="clr-namespace:Dc.App.Views.Converters"
xmlns:theme="clr-namespace:Dc.App.Services.Theme"
```
资源里：
```xml
<conv:EnumMatchConverter x:Key="EnumMatch" />
```

在布局里加（DataContext 根是 SettingsViewModel，主题子 VM 在 `Theme`）：

```xml
<GroupBox Header="外观" Margin="0,0,0,12">
    <StackPanel DataContext="{Binding Theme}" Orientation="Horizontal">
        <RadioButton Content="亮色" GroupName="apptheme" Margin="0,0,12,0"
            IsChecked="{Binding SelectedTheme, Converter={StaticResource EnumMatch}, ConverterParameter=Light}" />
        <RadioButton Content="暗色" GroupName="apptheme" Margin="0,0,12,0"
            IsChecked="{Binding SelectedTheme, Converter={StaticResource EnumMatch}, ConverterParameter=Dark}" />
        <RadioButton Content="跟随系统" GroupName="apptheme"
            IsChecked="{Binding SelectedTheme, Converter={StaticResource EnumMatch}, ConverterParameter=System}" />
    </StackPanel>
</GroupBox>
```

> 把它放在现有内容外层 —— 若现有 SettingsView 根是单一 Grid/DataGrid，包一层 DockPanel 或 StackPanel，把「外观」GroupBox 放 DockPanel.Dock="Top"，原内容填满剩余。读实际结构后决定最小改法，保持原 ConfigEntry 列表 + 备份按钮功能不变。

- [ ] **Step 3: DI 接线**

`ServiceRegistration.cs`：
- 注册 writer（在 ThemeService 注册附近）：
```csharp
        services.AddSingleton<Dc.App.Services.Theme.IThemePreferenceWriter>(_ =>
            new Dc.App.Services.Theme.JsonThemePreferenceWriter(
                System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
```
- `ThemeService` 注册保持 `AddSingleton<IThemeService, ThemeService>()` —— 自动构造会注入 IThemePreferenceWriter（已注册）。确认 ThemeService 当前是自动构造注册；若是工厂 lambda，把 writer 加进去。
- `SettingsViewModel` 注册保持 `AddSingleton<SettingsViewModel>()` —— 自动构造注入新加的 IThemeService 参数（已注册）。

- [ ] **Step 4: 构建**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -8
```

Expected: Build succeeded。

- [ ] **Step 5: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Views/ wpf/src/Dc.App/Composition/ServiceRegistration.cs
git commit -m ":sparkles: S5a.4: SettingsView 外观 section（三档主题 radio）+ DI 接线 writer"
```

---

## Task 5: 全量回归 + push

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
cat > /tmp/s5a_comment.json <<'EOF'
{"body":"### S5a · Settings 主题开关 + 持久化 完成\n\n补 S1 延后的两件事：\n- Settings 加「外观」三档主题 radio（亮/暗/跟随系统），实时调 IThemeService.Apply\n- 主题选择持久化到 appsettings.json（JsonThemePreferenceWriter 用 JsonNode 改写，保留其他键）；用户态切换写盘，启动读取不写\n- ThemeSettingsViewModel 抽出便于单测；与托盘菜单切主题双向同步\n\n纯视觉重样（Browse/Logs 卡片化）未做 —— 留 Windows 上边看边调，避免盲改返工。\n\n验证：Infra 48 + Integration 10 全绿；Dc.App + Tests build 0 错误；Dc.App.Tests +约 10（writer 4 + ThemeService 2 + ThemeSettings 4）。\n\nWindows walkthrough：设置页选暗色→立即变暗→重启仍暗色；选跟随系统→改 Windows 主题跟随切换；托盘「切换主题」与设置页 radio 同步。"}
EOF
curl -sk -u "$CREDS" -H "Content-Type: application/json" \
  -X POST "https://git.adamyu.top:20443/api/v1/repos/adamyu/dc/issues/5/comments" \
  -d @/tmp/s5a_comment.json -o /tmp/c.json -w "HTTP %{http_code}\n"
jq -r '.html_url // .message' /tmp/c.json
rm -f /tmp/s5a_comment.json /tmp/c.json
```

---

## 验收

- Infra 48 + Integration 10 全绿
- Dc.App.Tests +约 10，累计约 56，build 0 错误
- 设置页有三档主题 radio，切换实时生效 + 写回 appsettings.json
- 托盘切主题与设置页 radio 双向同步
- PR #5 评论已加 S5a 段
- Browse/Logs 视觉重样明确留 Windows
