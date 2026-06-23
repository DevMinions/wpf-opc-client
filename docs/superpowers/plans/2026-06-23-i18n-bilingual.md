# Dc.App 双语运行时切换 i18n 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 给 Dc.App 增加英文 UI（不替换中文），运行时实时切换免重启，选择持久化；响应 GitHub issue #2。

**架构：** 镜像现有主题（Theme）栈——`ILanguageService`/`LanguageService`（读 `IConfiguration["Language"]`→应用→持久化→发事件）+ `ILanguageApplier`（设 `CurrentUICulture`）+ `JsonLanguagePreferenceWriter`（写 `appsettings.json`）+ 设置页单选。i18n 特有的三件：`LocalizationManager`（单例 `INotifyPropertyChanged` 索引器，XAML 实时刷的源）、`LocExtension`（`{loc:Loc Key}` 标记扩展）、`ILocalizer`（VM/Service 取即时串）。资源走原生 `.resx`（中性=zh-CN + en 卫星程序集），按字符串 key 访问，平价测试防漏译。

**技术栈：** .NET 8 / WPF / net8.0-windows，CommunityToolkit.Mvvm，WPF-UI，xUnit + Moq，原生 `System.Resources` resx + 卫星程序集。

**规格：** `docs/superpowers/specs/2026-06-23-i18n-bilingual-runtime-switch-design.md`

---

## 前置约定（所有任务通用）

- **构建/测试必须走 Windows**（WPF）。用 dc-remote office 工作区（UA 模拟器即可，i18n 切换不依赖 OPC 运行时）：
  - 同步：`~/dc-remote.sh office sync`
  - 构建 App：`~/dc-remote.sh office build`（默认构建 `Dc.App`，自动带 `-p:Platform=x64 -p:CustomTestTarget=net8.0-windows`）
  - 跑测试：`~/dc-remote.sh office test tests/Dc.App.Tests/Dc.App.Tests.csproj`（自动带 x64/CustomTestTarget + TRX 解析 + blame-hang 超时）
  - **不要**在 Linux 本机 `dotnet build`/`test` Dc.App / Dc.App.Tests（net8.0-windows 编不过）。
- **不要**给仓库加 `global.json`（会把远程构建/test runner 弄挂）。
- 编辑后 `dotnet format` 钩子会自动修正风格（文件作用域命名空间、`using` 在 namespace 外、`var` 优先、4 空格、LF、行宽 120）。
- 每个任务末尾 commit；commit message 结尾加 `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`。
- 当前分支 `feat/i18n-bilingual`（已含规格 commit `c17c879`），全程在此分支推进。

## 文件结构（创建/修改清单与职责）

**新增（基础设施）**
| 文件 | 职责 |
|---|---|
| `src/Dc.App/Resources/Strings.resx` | 中性资源（zh-CN），key→中文 |
| `src/Dc.App/Resources/Strings.en.resx` | 英文资源，key→English（→ `en/Dc.App.resources.dll` 卫星） |
| `src/Dc.App/Resources/Strings.cs` | 手写 `ResourceManager` 访问器（不用 VS 设计器代码生成） |
| `src/Dc.App/Properties/AssemblyInfo.cs` | `[assembly: NeutralResourcesLanguage("zh-CN", MainAssembly)]` |
| `src/Dc.App/Services/I18n/AppLanguage.cs` | 语言选项枚举 |
| `src/Dc.App/Services/I18n/LocalizationManager.cs` | 单例 INPC 索引器，XAML 绑定源 + 当前 culture |
| `src/Dc.App/Services/I18n/ILocalizer.cs` + `ResourceLocalizer.cs` | VM/Service 注入取即时串 |
| `src/Dc.App/Services/I18n/ILanguageApplier.cs` + `CultureLanguageApplier.cs` | 设 CurrentUICulture + 通知 manager；System→culture 解析 |
| `src/Dc.App/Services/I18n/ILanguagePreferenceWriter.cs` + `JsonLanguagePreferenceWriter.cs` | 写 appsettings.json 的 `Language` 键 |
| `src/Dc.App/Services/I18n/ILanguageService.cs` + `LanguageService.cs` | 编排：Initialize/Apply/Current/事件 |
| `src/Dc.App/Markup/LocExtension.cs` | `{loc:Loc Key}` 标记扩展 |
| `src/Dc.App/ViewModels/LanguageSettingsViewModel.cs` | 绑设置页语言单选 |
| `tests/Dc.App.Tests/Services/I18n/*` | 上述各单测 + resx 平价测试 |

**修改**
| 文件 | 改动 |
|---|---|
| `src/Dc.App/appsettings.json` | 加 `"Language": "System"` |
| `src/Dc.App/Composition/ServiceRegistration.cs` | 注册语言栈 + manager/localizer；`NavigationRoute` 标签/分组头改 key |
| `src/Dc.App/App.xaml.cs` | `OnStartup` 加 `langSvc.Initialize()` |
| `src/Dc.App/Views/SettingsView.xaml` + `ViewModels/SettingsViewModel.cs` | 加语言单选行 + `Language` 属性 |
| `src/Dc.App/Navigation/NavigationRoute.cs` | 注释改“资源 key” |
| `src/Dc.App/Views/Shell/ShellWindow.xaml` + `.xaml.cs` | 导航项经 localizer 构建/重建；tray/footer/status 走 `{loc:Loc}` |
| `src/Dc.App/ViewModels/Shell/ShellViewModel.cs` | 注入 localizer/language；标题栏/主题标签/健康文案本地化 |
| `tests/Dc.App.Tests/ViewModels/Shell/ShellViewModelTests.cs` | 构造补 localizer/language 参数 |
| 全部 20 个 `*.xaml` 的静态文本 | → `{loc:Loc Key}` |
| 含中文的 ViewModels/Services `.cs` | 注入 `ILocalizer`，即时串改取资源 |
| `src/Dc.App/ViewModels/OpcDataTypeOption.cs` | 「默认」「未知」走资源 |

> **规格的“强类型 Strings 类”改为极简手写访问器**：原因——VS 设计器代码生成在 headless/CI 的 `dotnet build` 下不可靠；访问本就按字符串 key（标记扩展 + `ILocalizer` 都用 key），强类型属性用不上；key 存在性由平价测试保障。这是落地细化，不改变设计意图。

---

# Phase 0 — 资源基座

### 任务 1：resx 文件 + 访问器 + 中性语言特性

**文件：**
- 创建：`src/Dc.App/Resources/Strings.resx`
- 创建：`src/Dc.App/Resources/Strings.en.resx`
- 创建：`src/Dc.App/Resources/Strings.cs`
- 创建：`src/Dc.App/Properties/AssemblyInfo.cs`

- [ ] **步骤 1：创建 `Strings.cs` 访问器**

```csharp
using System.Resources;

namespace Dc.App.Resources;

// 手写资源访问器：不用 VS 设计器代码生成(headless/CI 的 dotnet build 不跑设计器)。
// 统一按字符串 key 访问;key 存在性由 StringsParityTests 保证。
// base name 必须等于 resx 的清单名: {RootNamespace}.{folder}.{file} = Dc.App.Resources.Strings
internal static class Strings
{
    public static ResourceManager ResourceManager { get; } =
        new("Dc.App.Resources.Strings", typeof(Strings).Assembly);
}
```

- [ ] **步骤 2：创建 `Properties/AssemblyInfo.cs`**（声明中性语言=zh-CN，避免去找 zh-CN 卫星）

```csharp
using System.Resources;

[assembly: NeutralResourcesLanguage("zh-CN", UltimateResourceFallbackLocation.MainAssembly)]
```

- [ ] **步骤 3：创建 `Strings.resx`（中性=中文）含种子 key**

用以下**精确**的 resx 骨架（VS/dotnet 均认）。种子 key 用于后续单测；其余 key 在抽取批次中追加。

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="metadata">
            <xsd:complexType>
              <xsd:sequence><xsd:element name="value" type="xsd:string" minOccurs="0" /></xsd:sequence>
              <xsd:attribute name="name" use="required" type="xsd:string" />
              <xsd:attribute name="type" type="xsd:string" />
              <xsd:attribute name="mimetype" type="xsd:string" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="assembly">
            <xsd:complexType>
              <xsd:attribute name="alias" type="xsd:string" />
              <xsd:attribute name="name" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence><xsd:element name="value" type="xsd:string" minOccurs="0" /></xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>

  <data name="Common_Save" xml:space="preserve"><value>保存</value></data>
  <data name="Common_Cancel" xml:space="preserve"><value>取消</value></data>
  <data name="Lang_ChineseSimplified" xml:space="preserve"><value>简体中文</value></data>
  <data name="Lang_English" xml:space="preserve"><value>English</value></data>
  <data name="Lang_System" xml:space="preserve"><value>跟随系统</value></data>
  <data name="Settings_Language" xml:space="preserve"><value>语言</value></data>
</root>
```

- [ ] **步骤 4：创建 `Strings.en.resx`（英文）含相同 key**

复制步骤 3 的完整 resx 骨架（schema + resheader 原样），把 `<data>` 段替换为：

```xml
  <data name="Common_Save" xml:space="preserve"><value>Save</value></data>
  <data name="Common_Cancel" xml:space="preserve"><value>Cancel</value></data>
  <data name="Lang_ChineseSimplified" xml:space="preserve"><value>简体中文</value></data>
  <data name="Lang_English" xml:space="preserve"><value>English</value></data>
  <data name="Lang_System" xml:space="preserve"><value>Follow system</value></data>
  <data name="Settings_Language" xml:space="preserve"><value>Language</value></data>
```
（`Lang_ChineseSimplified`/`Lang_English` 两个语言名两文件都保留原文，故 en 里也是「简体中文」「English」。）

- [ ] **步骤 5：构建验证**

运行：`~/dc-remote.sh office sync && ~/dc-remote.sh office build`
预期：构建成功（0 错）。`.resx` 自动作为 EmbeddedResource 编译；`Strings.en.resx` 自动生成 `en/Dc.App.resources.dll` 卫星程序集。
确认卫星生成：`~/dc-remote.sh office psh 'Test-Path "D:\code\wpf-opc-client\src\Dc.App\bin\x64\Release\net8.0-windows\en\Dc.App.resources.dll"'`
预期：`True`

- [ ] **步骤 6：Commit**

```bash
git add src/Dc.App/Resources/ src/Dc.App/Properties/AssemblyInfo.cs
git commit -m "feat(i18n): resx 资源基座(zh 中性 + en 卫星)+ 访问器"
```

---

# Phase 1 — i18n 核心（TDD）

### 任务 2：LocalizationManager（XAML 实时刷的源）

**文件：**
- 创建：`src/Dc.App/Services/I18n/LocalizationManager.cs`
- 测试：`tests/Dc.App.Tests/Services/I18n/LocalizationManagerTests.cs`

- [ ] **步骤 1：写失败测试**

```csharp
using System.Globalization;
using Dc.App.Services.I18n;

namespace Dc.App.Tests.Services.I18n;

public class LocalizationManagerTests
{
    [Fact]
    public void Indexer_ReturnsValueForCurrentCulture()
    {
        var m = new LocalizationManager();
        m.SetCulture(new CultureInfo("zh-CN"));
        Assert.Equal("保存", m["Common_Save"]);
        m.SetCulture(new CultureInfo("en"));
        Assert.Equal("Save", m["Common_Save"]);
    }

    [Fact]
    public void Indexer_UnknownKey_ReturnsKeyItself()
    {
        var m = new LocalizationManager();
        m.SetCulture(new CultureInfo("en"));
        Assert.Equal("__nope__", m["__nope__"]);
    }

    [Fact]
    public void SetCulture_RaisesIndexerPropertyChanged()
    {
        var m = new LocalizationManager();
        string? changed = null;
        m.PropertyChanged += (_, e) => changed = e.PropertyName;
        m.SetCulture(new CultureInfo("en"));
        Assert.Equal("Item[]", changed); // Binding.IndexerName
    }
}
```

- [ ] **步骤 2：运行验证失败**

运行：`~/dc-remote.sh office sync && ~/dc-remote.sh office test tests/Dc.App.Tests/Dc.App.Tests.csproj`
预期：编译失败（`LocalizationManager` 不存在）。

- [ ] **步骤 3：实现**

```csharp
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using Dc.App.Resources;

namespace Dc.App.Services.I18n;

public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    public CultureInfo Culture => _culture;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Strings.ResourceManager.GetString(key, _culture) ?? key;

    public void SetCulture(CultureInfo culture)
    {
        _culture = culture;
        // Binding.IndexerName == "Item[]" → 所有索引器绑定重取
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    }
}
```

- [ ] **步骤 4：运行验证通过**

运行：同步 + test 命令。预期：3 项 PASS。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/Services/I18n/LocalizationManager.cs tests/Dc.App.Tests/Services/I18n/LocalizationManagerTests.cs
git commit -m "feat(i18n): LocalizationManager(INPC 索引器,XAML 实时刷源)"
```

---

### 任务 3：ILocalizer / ResourceLocalizer（VM 取即时串）

**文件：**
- 创建：`src/Dc.App/Services/I18n/ILocalizer.cs`
- 创建：`src/Dc.App/Services/I18n/ResourceLocalizer.cs`
- 测试：`tests/Dc.App.Tests/Services/I18n/ResourceLocalizerTests.cs`

- [ ] **步骤 1：写失败测试**

```csharp
using System.Globalization;
using Dc.App.Services.I18n;

namespace Dc.App.Tests.Services.I18n;

public class ResourceLocalizerTests
{
    [Fact]
    public void Indexer_FollowsManagerCulture()
    {
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        var loc = new ResourceLocalizer();
        Assert.Equal("Save", loc["Common_Save"]);
        LocalizationManager.Instance.SetCulture(new CultureInfo("zh-CN"));
        Assert.Equal("保存", loc["Common_Save"]);
    }

    [Fact]
    public void Format_SubstitutesArgs()
    {
        // 用一个带占位符的临时 key 不便,直接验证 Format 走 string.Format 语义:
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        var loc = new ResourceLocalizer();
        // Common_Save 无占位符,Format 后不变;占位符 key 在抽取批次加入后由集成行为覆盖。
        Assert.Equal("Save", loc.Format("Common_Save"));
    }
}
```

- [ ] **步骤 2：运行验证失败**

运行：同步 + test。预期：编译失败。

- [ ] **步骤 3：实现**

```csharp
// ILocalizer.cs
namespace Dc.App.Services.I18n;

public interface ILocalizer
{
    string this[string key] { get; }
    string Format(string key, params object[] args);
}
```

```csharp
// ResourceLocalizer.cs
namespace Dc.App.Services.I18n;

public sealed class ResourceLocalizer : ILocalizer
{
    public string this[string key] => LocalizationManager.Instance[key];

    public string Format(string key, params object[] args) =>
        string.Format(LocalizationManager.Instance.Culture, this[key], args);
}
```

- [ ] **步骤 4：运行验证通过**。预期：2 项 PASS。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/Services/I18n/ILocalizer.cs src/Dc.App/Services/I18n/ResourceLocalizer.cs tests/Dc.App.Tests/Services/I18n/ResourceLocalizerTests.cs
git commit -m "feat(i18n): ILocalizer/ResourceLocalizer(VM 取即时串)"
```

---

### 任务 4：AppLanguage + CultureLanguageApplier

**文件：**
- 创建：`src/Dc.App/Services/I18n/AppLanguage.cs`
- 创建：`src/Dc.App/Services/I18n/ILanguageApplier.cs`
- 创建：`src/Dc.App/Services/I18n/CultureLanguageApplier.cs`
- 测试：`tests/Dc.App.Tests/Services/I18n/CultureLanguageApplierTests.cs`

- [ ] **步骤 1：写失败测试**（只测纯映射逻辑，避开静态 OS culture）

```csharp
using Dc.App.Services.I18n;

namespace Dc.App.Tests.Services.I18n;

public class CultureLanguageApplierTests
{
    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh-TW", "zh-CN")]   // 简化:本版本仅简中,繁中暂归 zh-CN(升级路径:加 zh-Hant)
    [InlineData("en-US", "en")]
    [InlineData("en", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("de-DE", "en")]
    public void ResolveSupported_MapsByPrefix(string input, string expected)
        => Assert.Equal(expected, CultureLanguageApplier.ResolveSupported(input).Name);
}
```

- [ ] **步骤 2：运行验证失败**。预期：编译失败。

- [ ] **步骤 3：实现**

```csharp
// AppLanguage.cs
namespace Dc.App.Services.I18n;

public enum AppLanguage
{
    System,
    ChineseSimplified,
    English
}
```

```csharp
// ILanguageApplier.cs
using System.Globalization;

namespace Dc.App.Services.I18n;

public interface ILanguageApplier
{
    void Apply(CultureInfo effective);   // 设 CurrentUICulture + 通知 manager
    CultureInfo DetectSystemCulture();   // OS UI culture → 受支持 culture
}
```

```csharp
// CultureLanguageApplier.cs
using System.Globalization;
using Dc.App.Services.I18n;

namespace Dc.App.Services.I18n;

public sealed class CultureLanguageApplier : ILanguageApplier
{
    public void Apply(CultureInfo effective)
    {
        // 只动 UICulture(界面语言),不动 CurrentCulture(日期/数值格式与 OPC 数值解析保持稳定)
        CultureInfo.DefaultThreadCurrentUICulture = effective;
        Thread.CurrentThread.CurrentUICulture = effective;
        LocalizationManager.Instance.SetCulture(effective);
    }

    public CultureInfo DetectSystemCulture() => ResolveSupported(CultureInfo.InstalledUICulture.Name);

    // 纯函数,便于单测:zh* → zh-CN,其它 → en
    public static CultureInfo ResolveSupported(string cultureName) =>
        cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? new CultureInfo("zh-CN")
            : new CultureInfo("en");
}
```

- [ ] **步骤 4：运行验证通过**。预期：8 项 PASS。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/Services/I18n/AppLanguage.cs src/Dc.App/Services/I18n/ILanguageApplier.cs src/Dc.App/Services/I18n/CultureLanguageApplier.cs tests/Dc.App.Tests/Services/I18n/CultureLanguageApplierTests.cs
git commit -m "feat(i18n): AppLanguage 枚举 + CultureLanguageApplier(只切 UICulture)"
```

---

### 任务 5：ILanguagePreferenceWriter / JsonLanguagePreferenceWriter

**文件：**
- 创建：`src/Dc.App/Services/I18n/ILanguagePreferenceWriter.cs`
- 创建：`src/Dc.App/Services/I18n/JsonLanguagePreferenceWriter.cs`
- 测试：`tests/Dc.App.Tests/Services/I18n/JsonLanguagePreferenceWriterTests.cs`

- [ ] **步骤 1：写失败测试**（镜像 `JsonThemePreferenceWriterTests`）

```csharp
using System.IO;
using System.Text.Json;
using Dc.App.Services.I18n;

namespace Dc.App.Tests.Services.I18n;

public class JsonLanguagePreferenceWriterTests
{
    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-lang-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Write_UpdatesLanguageKey_PreservesOtherKeys()
    {
        var path = TempFile("""
        { "Database": { "Path": "sqlite.db" }, "Language": "System", "Theme": "Dark" }
        """);
        try
        {
            new JsonLanguagePreferenceWriter(path).Write(AppLanguage.English);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal("English", root.GetProperty("Language").GetString());
            Assert.Equal("sqlite.db", root.GetProperty("Database").GetProperty("Path").GetString());
            Assert.Equal("Dark", root.GetProperty("Theme").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_AddsLanguageKey_WhenMissing()
    {
        var path = TempFile("""{ "Database": { "Path": "sqlite.db" } }""");
        try
        {
            new JsonLanguagePreferenceWriter(path).Write(AppLanguage.ChineseSimplified);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("ChineseSimplified", doc.RootElement.GetProperty("Language").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_MissingFile_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-missing-{Guid.NewGuid():N}.json");
        var ex = Record.Exception(() => new JsonLanguagePreferenceWriter(path).Write(AppLanguage.English));
        Assert.Null(ex);
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Write_MalformedJson_DoesNotThrow()
    {
        var path = TempFile("{ not json ");
        try
        {
            var ex = Record.Exception(() => new JsonLanguagePreferenceWriter(path).Write(AppLanguage.English));
            Assert.Null(ex);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **步骤 2：运行验证失败**。预期：编译失败。

- [ ] **步骤 3：实现**（镜像 `JsonThemePreferenceWriter`）

```csharp
// ILanguagePreferenceWriter.cs
namespace Dc.App.Services.I18n;

public interface ILanguagePreferenceWriter
{
    /// 把语言选择写入持久化(appsettings.json 的 Language 键)。失败不抛。
    void Write(AppLanguage language);
}
```

```csharp
// JsonLanguagePreferenceWriter.cs
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dc.App.Services.I18n;

public sealed class JsonLanguagePreferenceWriter : ILanguagePreferenceWriter
{
    private readonly string _path;

    public JsonLanguagePreferenceWriter(string path) => _path = path;

    public void Write(AppLanguage language)
    {
        try
        {
            if (!File.Exists(_path)) return;
            var text = File.ReadAllText(_path);
            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch (JsonException) { return; }
            if (root is not JsonObject obj) return;

            obj["Language"] = language.ToString();
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_path, obj.ToJsonString(options));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **步骤 4：运行验证通过**。预期：4 项 PASS。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/Services/I18n/ILanguagePreferenceWriter.cs src/Dc.App/Services/I18n/JsonLanguagePreferenceWriter.cs tests/Dc.App.Tests/Services/I18n/JsonLanguagePreferenceWriterTests.cs
git commit -m "feat(i18n): JsonLanguagePreferenceWriter(写 appsettings.json Language 键)"
```

---

### 任务 6：ILanguageService / LanguageService（编排）

**文件：**
- 创建：`src/Dc.App/Services/I18n/ILanguageService.cs`
- 创建：`src/Dc.App/Services/I18n/LanguageService.cs`
- 测试：`tests/Dc.App.Tests/Services/I18n/LanguageServiceTests.cs`

- [ ] **步骤 1：写失败测试**（镜像 `ThemeServiceTests`）

```csharp
using System.Globalization;
using Dc.App.Services.I18n;
using Microsoft.Extensions.Configuration;

namespace Dc.App.Tests.Services.I18n;

public class LanguageServiceTests
{
    private static IConfiguration Config(string? value)
    {
        var dict = value is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["Language"] = value };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static Mock<ILanguageApplier> Applier(string systemName = "en")
    {
        var a = new Mock<ILanguageApplier>();
        a.Setup(x => x.DetectSystemCulture()).Returns(new CultureInfo(systemName));
        return a;
    }

    [Fact]
    public void Initial_DefaultsToSystem_WhenConfigMissing()
    {
        var applier = Applier("en");
        var svc = new LanguageService(Config(null), applier.Object);
        svc.Initialize();
        Assert.Equal(AppLanguage.System, svc.Current);
        applier.Verify(a => a.Apply(It.Is<CultureInfo>(c => c.Name == "en")), Times.Once);
    }

    [Theory]
    [InlineData("ChineseSimplified", AppLanguage.ChineseSimplified)]
    [InlineData("English", AppLanguage.English)]
    [InlineData("System", AppLanguage.System)]
    public void Initial_ReadsConfiguredValue(string configured, AppLanguage expected)
    {
        var svc = new LanguageService(Config(configured), Applier().Object);
        svc.Initialize();
        Assert.Equal(expected, svc.Current);
    }

    [Fact]
    public void Apply_English_AppliesEnCulture()
    {
        var applier = Applier();
        var svc = new LanguageService(Config(null), applier.Object);
        svc.Initialize();
        applier.Invocations.Clear();
        svc.Apply(AppLanguage.English);
        applier.Verify(a => a.Apply(It.Is<CultureInfo>(c => c.Name == "en")), Times.Once);
        Assert.Equal(AppLanguage.English, svc.Current);
    }

    [Fact]
    public void Apply_ChineseSimplified_AppliesZhCulture()
    {
        var applier = Applier();
        var svc = new LanguageService(Config(null), applier.Object);
        svc.Initialize();
        applier.Invocations.Clear();
        svc.Apply(AppLanguage.ChineseSimplified);
        applier.Verify(a => a.Apply(It.Is<CultureInfo>(c => c.Name == "zh-CN")), Times.Once);
    }

    [Fact]
    public void Apply_System_ResolvesViaApplier()
    {
        var applier = Applier("zh-CN");
        var svc = new LanguageService(Config("English"), applier.Object);
        svc.Initialize();
        applier.Invocations.Clear();
        svc.Apply(AppLanguage.System);
        applier.Verify(a => a.DetectSystemCulture(), Times.Once);
        applier.Verify(a => a.Apply(It.Is<CultureInfo>(c => c.Name == "zh-CN")), Times.Once);
    }

    [Fact]
    public void Apply_RaisesLanguageChanged()
    {
        var svc = new LanguageService(Config(null), Applier().Object);
        svc.Initialize();
        AppLanguage? got = null;
        svc.LanguageChanged += l => got = l;
        svc.Apply(AppLanguage.English);
        Assert.Equal(AppLanguage.English, got);
    }

    [Fact]
    public void Initialize_DoesNotRaiseOrPersist()
    {
        var writer = new Mock<ILanguagePreferenceWriter>();
        var svc = new LanguageService(Config("English"), Applier().Object, writer.Object);
        bool fired = false;
        svc.LanguageChanged += _ => fired = true;
        svc.Initialize();
        Assert.False(fired);
        writer.Verify(w => w.Write(It.IsAny<AppLanguage>()), Times.Never);
    }

    [Fact]
    public void Apply_PersistsViaWriter()
    {
        var writer = new Mock<ILanguagePreferenceWriter>();
        var svc = new LanguageService(Config(null), Applier().Object, writer.Object);
        svc.Initialize();
        svc.Apply(AppLanguage.English);
        writer.Verify(w => w.Write(AppLanguage.English), Times.Once);
    }
}
```

- [ ] **步骤 2：运行验证失败**。预期：编译失败。

- [ ] **步骤 3：实现**

```csharp
// ILanguageService.cs
namespace Dc.App.Services.I18n;

public interface ILanguageService
{
    AppLanguage Current { get; }
    event Action<AppLanguage>? LanguageChanged;

    /// 启动时调用一次:读 IConfiguration["Language"] → Apply 一次(不发事件、不写盘)。
    void Initialize();

    /// 用户切换语言。System 会被解析为 effective culture 再下发。
    void Apply(AppLanguage language);
}
```

```csharp
// LanguageService.cs
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Dc.App.Services.I18n;

public sealed class LanguageService : ILanguageService
{
    private readonly IConfiguration _config;
    private readonly ILanguageApplier _applier;
    private readonly ILanguagePreferenceWriter? _writer;
    private AppLanguage _current = AppLanguage.System;

    public LanguageService(IConfiguration config, ILanguageApplier applier, ILanguagePreferenceWriter? writer = null)
    {
        _config = config;
        _applier = applier;
        _writer = writer;
    }

    public AppLanguage Current => _current;
    public event Action<AppLanguage>? LanguageChanged;

    public void Initialize()
    {
        var initial = ParseOrDefault(_config["Language"], AppLanguage.System);
        Apply(initial, raiseEvent: false);
    }

    public void Apply(AppLanguage language) => Apply(language, raiseEvent: true);

    private void Apply(AppLanguage language, bool raiseEvent)
    {
        var effective = language == AppLanguage.System ? _applier.DetectSystemCulture() : Map(language);
        _applier.Apply(effective);
        _current = language;
        if (raiseEvent)
        {
            _writer?.Write(language);
            LanguageChanged?.Invoke(language);
        }
    }

    private static CultureInfo Map(AppLanguage language) => language switch
    {
        AppLanguage.English => new CultureInfo("en"),
        _ => new CultureInfo("zh-CN")
    };

    private static AppLanguage ParseOrDefault(string? raw, AppLanguage fallback)
        => Enum.TryParse<AppLanguage>(raw, ignoreCase: true, out var v) ? v : fallback;
}
```

> 说明：语言不设“OS 语言实时监听”（OS 显示语言变更通常要重登录，YAGNI）。`System` 仅在启动 `Initialize` 时解析一次。

- [ ] **步骤 4：运行验证通过**。预期：全部 PASS。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/Services/I18n/ILanguageService.cs src/Dc.App/Services/I18n/LanguageService.cs tests/Dc.App.Tests/Services/I18n/LanguageServiceTests.cs
git commit -m "feat(i18n): LanguageService(编排:读配置→应用→持久化→发事件)"
```

---

### 任务 7：LocExtension（`{loc:Loc Key}` 标记扩展）

**文件：**
- 创建：`src/Dc.App/Markup/LocExtension.cs`

- [ ] **步骤 1：实现**（无单测：WPF 标记扩展依赖 XAML 解析，由后续视图集成 + 真机验证覆盖）

```csharp
using System;
using System.Windows.Data;
using System.Windows.Markup;
using Dc.App.Services.I18n;

namespace Dc.App.Markup;

// 用法: Text="{loc:Loc Settings_Title}"
// 绑定到 LocalizationManager 单例索引器;culture 变化时索引器发 PropertyChanged("Item[]") → 实时刷。
// Source 显式指向单例,故与 DataContext 无关 → ContextMenu/Tray 等独立可视化树也能用。
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
```

- [ ] **步骤 2：构建验证**

运行：`~/dc-remote.sh office sync && ~/dc-remote.sh office build`。预期：0 错。

- [ ] **步骤 3：Commit**

```bash
git add src/Dc.App/Markup/LocExtension.cs
git commit -m "feat(i18n): LocExtension 标记扩展({loc:Loc Key})"
```

---

### 任务 8：resx 平价测试（防漏译）

**文件：**
- 测试：`tests/Dc.App.Tests/Services/I18n/StringsParityTests.cs`

- [ ] **步骤 1：写测试**（用已编译的资源集比较 key，路径无关、健壮）

```csharp
using System.Collections;
using System.Globalization;
using Dc.App.Resources;

namespace Dc.App.Tests.Services.I18n;

public class StringsParityTests
{
    private static HashSet<string> Keys(CultureInfo culture, bool tryParents)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: tryParents)
                  ?? throw new Xunit.Sdk.XunitException($"找不到 {culture.Name} 资源集");
        return set.Cast<DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
    }

    [Fact]
    public void Zh_And_En_HaveIdenticalKeySets()
    {
        var zh = Keys(new CultureInfo("zh-CN"), tryParents: true);   // 中性=主程序集
        var en = Keys(new CultureInfo("en"), tryParents: false);     // 仅 en 卫星,不回退父级

        var missingInEn = zh.Except(en).OrderBy(x => x).ToList();
        var extraInEn = en.Except(zh).OrderBy(x => x).ToList();

        Assert.True(missingInEn.Count == 0, "en 缺译: " + string.Join(", ", missingInEn));
        Assert.True(extraInEn.Count == 0, "en 多余: " + string.Join(", ", extraInEn));
    }
}
```

- [ ] **步骤 2：运行验证通过**

运行：同步 + test。预期：PASS（种子 key 两文件一致）。**此后每个抽取批次必须保持此测试 PASS。**

- [ ] **步骤 3：Commit**

```bash
git add tests/Dc.App.Tests/Services/I18n/StringsParityTests.cs
git commit -m "test(i18n): resx 平价测试(zh/en key 集合相等,防漏译)"
```

---

# Phase 2 — 接线 + 切换 UI（端到端能切）

### 任务 9：DI 注册 + appsettings + 启动初始化

**文件：**
- 修改：`src/Dc.App/Composition/ServiceRegistration.cs`（Theme 注册块旁，约 154-160 行）
- 修改：`src/Dc.App/appsettings.json`
- 修改：`src/Dc.App/App.xaml.cs:94-95`

- [ ] **步骤 1：注册语言栈**

在 `ServiceRegistration.cs` 的 `// === Shell + Theme + Navigation` 块里、`IThemeService` 注册之后加：

```csharp
        // === Language / i18n ===
        services.AddSingleton<Dc.App.Services.I18n.ILanguageApplier, Dc.App.Services.I18n.CultureLanguageApplier>();
        services.AddSingleton<Dc.App.Services.I18n.ILanguagePreferenceWriter>(_ =>
            new Dc.App.Services.I18n.JsonLanguagePreferenceWriter(
                System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        services.AddSingleton<Dc.App.Services.I18n.ILanguageService, Dc.App.Services.I18n.LanguageService>();
        services.AddSingleton<Dc.App.Services.I18n.ILocalizer, Dc.App.Services.I18n.ResourceLocalizer>();
```

- [ ] **步骤 2：appsettings.json 加 Language 键**

在 `"Theme": "System",` 行后加：
```json
  "Language": "System",
```

- [ ] **步骤 3：App 启动初始化**

`App.xaml.cs` 中 `themeSvc.Initialize();`（约 95 行）之后加：

```csharp
            // 初始化语言(读 appsettings.json:Language → 设 CurrentUICulture)。必须在 window.Show() 前,首屏即正确语言。
            var langSvc = _host.Services.GetRequiredService<Dc.App.Services.I18n.ILanguageService>();
            langSvc.Initialize();
```

- [ ] **步骤 4：构建验证**

运行：`~/dc-remote.sh office sync && ~/dc-remote.sh office build`。预期：0 错。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/Composition/ServiceRegistration.cs src/Dc.App/appsettings.json src/Dc.App/App.xaml.cs
git commit -m "feat(i18n): DI 注册语言栈 + appsettings Language 键 + 启动初始化"
```

---

### 任务 10：设置页语言单选

**文件：**
- 创建：`src/Dc.App/ViewModels/LanguageSettingsViewModel.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/SettingsViewModelLanguageTests.cs`
- 修改：`src/Dc.App/ViewModels/SettingsViewModel.cs`
- 修改：`src/Dc.App/Views/SettingsView.xaml`

- [ ] **步骤 1：写失败测试**（镜像 `SettingsViewModelThemeTests`）

```csharp
using Dc.App.Services.I18n;
using Dc.App.ViewModels;

namespace Dc.App.Tests.ViewModels;

public class SettingsViewModelLanguageTests
{
    private sealed class FakeLanguageService : ILanguageService
    {
        public AppLanguage Current { get; private set; } = AppLanguage.System;
        public event Action<AppLanguage>? LanguageChanged;
        public int ApplyCount;
        public void Initialize() { }
        public void Apply(AppLanguage lang) { ApplyCount++; Current = lang; LanguageChanged?.Invoke(lang); }
    }

    [Fact]
    public void Initial_SelectedLanguageMatchesService()
    {
        var svc = new FakeLanguageService();
        svc.Apply(AppLanguage.English);
        var vm = new LanguageSettingsViewModel(svc);
        Assert.Equal(AppLanguage.English, vm.SelectedLanguage);
    }

    [Fact]
    public void SettingSelectedLanguage_CallsApply()
    {
        var svc = new FakeLanguageService();
        var vm = new LanguageSettingsViewModel(svc);
        vm.SelectedLanguage = AppLanguage.English;
        Assert.Equal(AppLanguage.English, svc.Current);
        Assert.True(svc.ApplyCount >= 1);
    }

    [Fact]
    public void ChangedExternally_UpdatesSelectedLanguage()
    {
        var svc = new FakeLanguageService();
        var vm = new LanguageSettingsViewModel(svc);
        svc.Apply(AppLanguage.ChineseSimplified);
        Assert.Equal(AppLanguage.ChineseSimplified, vm.SelectedLanguage);
    }

    [Fact]
    public void SettingSameLanguage_DoesNotReapply()
    {
        var svc = new FakeLanguageService();
        var vm = new LanguageSettingsViewModel(svc);
        vm.SelectedLanguage = AppLanguage.English;
        var after = svc.ApplyCount;
        vm.SelectedLanguage = AppLanguage.English;
        Assert.Equal(after, svc.ApplyCount);
    }
}
```

- [ ] **步骤 2：运行验证失败**。预期：编译失败。

- [ ] **步骤 3：实现 LanguageSettingsViewModel**（镜像 `ThemeSettingsViewModel`）

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Dc.App.Services.I18n;

namespace Dc.App.ViewModels;

public sealed partial class LanguageSettingsViewModel : ObservableObject
{
    private readonly ILanguageService _language;
    private bool _syncing;

    [ObservableProperty] private AppLanguage _selectedLanguage;

    public LanguageSettingsViewModel(ILanguageService language)
    {
        _language = language;
        _selectedLanguage = language.Current;
        _language.LanguageChanged += OnServiceLanguageChanged;
    }

    partial void OnSelectedLanguageChanged(AppLanguage value)
    {
        if (_syncing) return;
        if (_language.Current == value) return;
        _language.Apply(value);
    }

    private void OnServiceLanguageChanged(AppLanguage lang)
    {
        _syncing = true;
        SelectedLanguage = lang;
        _syncing = false;
    }
}
```

- [ ] **步骤 4：SettingsViewModel 暴露 Language**

`SettingsViewModel.cs`：加 `using Dc.App.Services.I18n;`；构造参数加 `ILanguageService language`；类内加属性并在 ctor 构造：

```csharp
    public LanguageSettingsViewModel Language { get; }
```
ctor 体内（`Theme = new ThemeSettingsViewModel(theme);` 旁）：
```csharp
        Language = new LanguageSettingsViewModel(language);
```

> `SettingsViewModel` 在 DI 中为默认激活（`services.AddSingleton<SettingsViewModel>();`），新增的 `ILanguageService` 参数会被自动注入（任务 9 已注册），无需改注册。

- [ ] **步骤 5：SettingsView.xaml 加语言行**

在「外观」卡片里、主题 `Grid`（`</Grid>` 收尾，约 69 行）之后、卡片 `</StackPanel>` 之前，插入语言行（结构对齐主题行，文案走 `{loc:Loc}`）。先在根元素加命名空间：`xmlns:loc="clr-namespace:Dc.App.Markup"`。

```xml
                    <!-- 语言 row -->
                    <Grid Margin="0,14,0,0">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <StackPanel Grid.Column="0" VerticalAlignment="Center">
                            <TextBlock Text="{loc:Loc Settings_Language}"
                                       Foreground="{DynamicResource TextFillColorPrimaryBrush}"
                                       FontSize="13" />
                        </StackPanel>
                        <StackPanel Grid.Column="1"
                                    DataContext="{Binding Language}"
                                    Orientation="Horizontal"
                                    VerticalAlignment="Center">
                            <RadioButton Content="{loc:Loc Lang_ChineseSimplified}"
                                         GroupName="applang"
                                         Margin="0,0,16,0"
                                         IsChecked="{Binding SelectedLanguage,
                                             Converter={StaticResource EnumMatch},
                                             ConverterParameter=ChineseSimplified}" />
                            <RadioButton Content="{loc:Loc Lang_English}"
                                         GroupName="applang"
                                         Margin="0,0,16,0"
                                         IsChecked="{Binding SelectedLanguage,
                                             Converter={StaticResource EnumMatch},
                                             ConverterParameter=English}" />
                            <RadioButton Content="{loc:Loc Lang_System}"
                                         GroupName="applang"
                                         IsChecked="{Binding SelectedLanguage,
                                             Converter={StaticResource EnumMatch},
                                             ConverterParameter=System}" />
                        </StackPanel>
                    </Grid>
```

- [ ] **步骤 6：运行验证通过 + 构建**

运行：`~/dc-remote.sh office sync && ~/dc-remote.sh office build && ~/dc-remote.sh office test tests/Dc.App.Tests/Dc.App.Tests.csproj`
预期：构建 0 错；语言 VM 测试 4 项 PASS；既有测试不回归。

- [ ] **步骤 7：Commit**

```bash
git add src/Dc.App/ViewModels/LanguageSettingsViewModel.cs src/Dc.App/ViewModels/SettingsViewModel.cs src/Dc.App/Views/SettingsView.xaml tests/Dc.App.Tests/ViewModels/SettingsViewModelLanguageTests.cs
git commit -m "feat(i18n): 设置页语言单选(简体中文/English/跟随系统)"
```

---

### 任务 11：导航栏本地化（唯一需特殊接线处）+ 端到端验证

导航项在 `ShellWindow.xaml.cs` 用代码从 `ShellViewModel.Routes` 构建（不在 XAML），故路由 `Title`/`GroupHeader` 改存**资源 key**，构建时经 `ILocalizer` 解析，并在语言切换时**重建导航**保持实时刷。

**文件：**
- 修改：`src/Dc.App/Navigation/NavigationRoute.cs`（注释）
- 修改：`src/Dc.App/Composition/ServiceRegistration.cs`（路由 162-175 行，标签/分组头改 key）
- 修改：`src/Dc.App/Views/Shell/ShellWindow.xaml.cs`（注入 localizer + 重建）
- 修改：`src/Dc.App/Views/Shell/ShellWindow.xaml`（footer/tray/status 文案 → `{loc:Loc}`）
- 修改：`src/Dc.App/ViewModels/Shell/ShellViewModel.cs`（标题栏/主题标签/健康文案本地化）
- 修改：`tests/Dc.App.Tests/ViewModels/Shell/ShellViewModelTests.cs`（构造补参）

- [ ] **步骤 1：加 nav/shell key 到两 resx**（中性 + en）

`Strings.resx` 加：
```xml
  <data name="Nav_Dashboard" xml:space="preserve"><value>仪表盘</value></data>
  <data name="Nav_Workspace" xml:space="preserve"><value>采集任务</value></data>
  <data name="Nav_Browse" xml:space="preserve"><value>浏览节点</value></data>
  <data name="Nav_LiveData" xml:space="preserve"><value>实时数据</value></data>
  <data name="Nav_Diagnostics" xml:space="preserve"><value>诊断</value></data>
  <data name="Nav_Settings" xml:space="preserve"><value>设置</value></data>
  <data name="Nav_Logs" xml:space="preserve"><value>日志</value></data>
  <data name="Nav_About" xml:space="preserve"><value>关于</value></data>
  <data name="NavGroup_Collection" xml:space="preserve"><value>采集</value></data>
  <data name="NavGroup_Monitoring" xml:space="preserve"><value>全局监控</value></data>
  <data name="NavGroup_System" xml:space="preserve"><value>系统</value></data>
  <data name="Theme_Light" xml:space="preserve"><value>亮色</value></data>
  <data name="Theme_Dark" xml:space="preserve"><value>暗色</value></data>
  <data name="Theme_System" xml:space="preserve"><value>跟随系统</value></data>
  <data name="Health_AllNormal" xml:space="preserve"><value>● 全部正常</value></data>
  <data name="Health_Warnings" xml:space="preserve"><value>● {0} 告警</value></data>
  <data name="Shell_TitleBarFormat" xml:space="preserve"><value>Dc · OPC 数采  ›  {0}</value></data>
  <data name="Shell_StatusTheme" xml:space="preserve"><value>主题 · </value></data>
  <data name="Shell_StatusDb" xml:space="preserve"><value>    ·    DB · </value></data>
  <data name="Tray_Show" xml:space="preserve"><value>显示主窗口</value></data>
  <data name="Tray_ToggleTheme" xml:space="preserve"><value>切换主题</value></data>
  <data name="Tray_About" xml:space="preserve"><value>关于…</value></data>
  <data name="Tray_Exit" xml:space="preserve"><value>退出</value></data>
  <data name="Tray_Tooltip" xml:space="preserve"><value>Dc · OPC 数据采集</value></data>
  <data name="Tray_BalloonTitle" xml:space="preserve"><value>Dc · OPC 数据采集仍在后台运行</value></data>
  <data name="Tray_BalloonMessage" xml:space="preserve"><value>已最小化到系统托盘 · 双击图标唤回 · 右键「退出」可彻底关闭</value></data>
```

`Strings.en.resx` 加对应英文：
```xml
  <data name="Nav_Dashboard" xml:space="preserve"><value>Dashboard</value></data>
  <data name="Nav_Workspace" xml:space="preserve"><value>Tasks</value></data>
  <data name="Nav_Browse" xml:space="preserve"><value>Browse</value></data>
  <data name="Nav_LiveData" xml:space="preserve"><value>Live Data</value></data>
  <data name="Nav_Diagnostics" xml:space="preserve"><value>Diagnostics</value></data>
  <data name="Nav_Settings" xml:space="preserve"><value>Settings</value></data>
  <data name="Nav_Logs" xml:space="preserve"><value>Logs</value></data>
  <data name="Nav_About" xml:space="preserve"><value>About</value></data>
  <data name="NavGroup_Collection" xml:space="preserve"><value>Collection</value></data>
  <data name="NavGroup_Monitoring" xml:space="preserve"><value>Monitoring</value></data>
  <data name="NavGroup_System" xml:space="preserve"><value>System</value></data>
  <data name="Theme_Light" xml:space="preserve"><value>Light</value></data>
  <data name="Theme_Dark" xml:space="preserve"><value>Dark</value></data>
  <data name="Theme_System" xml:space="preserve"><value>System</value></data>
  <data name="Health_AllNormal" xml:space="preserve"><value>● All normal</value></data>
  <data name="Health_Warnings" xml:space="preserve"><value>● {0} alert(s)</value></data>
  <data name="Shell_TitleBarFormat" xml:space="preserve"><value>Dc · OPC DAQ  ›  {0}</value></data>
  <data name="Shell_StatusTheme" xml:space="preserve"><value>Theme · </value></data>
  <data name="Shell_StatusDb" xml:space="preserve"><value>    ·    DB · </value></data>
  <data name="Tray_Show" xml:space="preserve"><value>Show window</value></data>
  <data name="Tray_ToggleTheme" xml:space="preserve"><value>Toggle theme</value></data>
  <data name="Tray_About" xml:space="preserve"><value>About…</value></data>
  <data name="Tray_Exit" xml:space="preserve"><value>Exit</value></data>
  <data name="Tray_Tooltip" xml:space="preserve"><value>Dc · OPC Data Collector</value></data>
  <data name="Tray_BalloonTitle" xml:space="preserve"><value>Dc · OPC Data Collector is still running</value></data>
  <data name="Tray_BalloonMessage" xml:space="preserve"><value>Minimized to system tray · double-click to restore · right-click "Exit" to quit</value></data>
```

- [ ] **步骤 2：ServiceRegistration 路由标签改 key**

把 `NavigationRoute(...)` 的第 2 个参数（Title）与 `GroupHeader:` 改为 key：

```csharp
                    new Dc.App.Navigation.NavigationRoute("dashboard",   "Nav_Dashboard",   "Home24",                typeof(Dc.App.ViewModels.Dashboard.DashboardViewModel)),
                    new Dc.App.Navigation.NavigationRoute("workspace",   "Nav_Workspace",   "TaskListSquareLtr24",   typeof(Dc.App.ViewModels.Workspace.TaskWorkspaceViewModel), GroupHeader: "NavGroup_Collection"),
                    new Dc.App.Navigation.NavigationRoute("browse",      "Nav_Browse",      "Search24",              typeof(BrowseViewModel)),
                    new Dc.App.Navigation.NavigationRoute("livedata",    "Nav_LiveData",    "DataHistogram24",       typeof(LiveDataViewModel),       GroupHeader: "NavGroup_Monitoring"),
                    new Dc.App.Navigation.NavigationRoute("diagnostics", "Nav_Diagnostics", "Pulse24",               typeof(DiagnosticsViewModel)),
                    new Dc.App.Navigation.NavigationRoute("settings",    "Nav_Settings",    "Settings24",            typeof(SettingsViewModel),       GroupHeader: "NavGroup_System"),
                    new Dc.App.Navigation.NavigationRoute("logs",        "Nav_Logs",        "DocumentText24",        typeof(LogsViewModel))
```

`NavigationRoute.cs`：把 `Title` 注释从“侧栏显示文本”改为“侧栏显示文本的资源 key”，`GroupHeader` 同理改“分组标题的资源 key”。

- [ ] **步骤 3：ShellWindow.xaml.cs 注入 localizer + 重建导航**

构造函数改为接收 `ILocalizer` + `ILanguageService`，构建用 localizer，订阅语言变化重建。完整替换 ctor、`BuildMenuItems`，新增 `WireFooter`/`RebuildMenuItems`：

```csharp
    private readonly ShellViewModel _vm;
    private readonly Dc.App.Services.I18n.ILocalizer _loc;
    private bool _reallyExit;
    private bool _trayHintShown;

    public ShellWindow(ShellViewModel vm, Dc.App.Services.I18n.ILocalizer loc, Dc.App.Services.I18n.ILanguageService language)
    {
        InitializeComponent();
        _vm = vm;
        _loc = loc;
        DataContext = vm;

        BuildMenuItems();
        WireFooter();
        // 初始选中 + 导航(仅一次,不放进 BuildMenuItems,以免重建时重复导航)
        if (_vm.Routes.Count > 0)
        {
            SelectNavItemByKey(_vm.Routes[0].Key);
            _vm.NavigateCommand.Execute(_vm.Routes[0].Key);
        }
        // 语言切换 → 重建导航项(文字按新 culture 取),保持当前选中
        language.LanguageChanged += _ => Dispatcher.Invoke(RebuildMenuItems);

        Closing += OnClosing;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void BuildMenuItems()
    {
        string? lastGroup = null;
        foreach (var route in _vm.Routes)
        {
            if (route.GroupHeader is not null && route.GroupHeader != lastGroup)
            {
                RootNav.MenuItems.Add(new NavigationViewItemHeader
                {
                    Text = _loc[route.GroupHeader],
                    Margin = new Thickness(12, 10, 0, 2),
                    FontSize = 11
                });
                lastGroup = route.GroupHeader;
            }
            var item = new NavigationViewItem
            {
                Content = _loc[route.Title],
                Tag = route.Key,
                Icon = ResolveIcon(route.Icon)
            };
            item.PreviewMouseLeftButtonUp += OnNavItemClicked;
            RootNav.MenuItems.Add(item);
        }
    }

    private void WireFooter()
    {
        // footer「关于」item 点击处理(一次性;Content 由 XAML {loc:Loc} 实时刷,无需重建)
        foreach (var obj in RootNav.FooterMenuItems)
            if (obj is NavigationViewItem fi)
                fi.PreviewMouseLeftButtonUp += OnNavItemClicked;
    }

    private void RebuildMenuItems()
    {
        foreach (var obj in RootNav.MenuItems)
            if (obj is NavigationViewItem nvi)
                nvi.PreviewMouseLeftButtonUp -= OnNavItemClicked;
        RootNav.MenuItems.Clear();
        BuildMenuItems();
        if (!string.IsNullOrEmpty(_vm.SelectedRouteKey))
            SelectNavItemByKey(_vm.SelectedRouteKey);
    }
```

> 其余方法（`OnNavItemClicked`/`HandleNavKey`/`SelectNavItemByKey`/`ResolveIcon`/`OnClosing`/tray 处理器等）保持不变，但其中的中文串在步骤 5 一并替换。

- [ ] **步骤 4：ShellViewModel 本地化标题栏/主题标签/健康文案**

`ShellViewModel.cs`：加 `using Dc.App.Services.I18n;`；构造参数加 `ILocalizer loc, ILanguageService language`（放在 `IThemeService theme` 之后、可选参数之前）；存字段 `_loc`。改动如下：

把 `CurrentThemeLabel` 改为走资源：
```csharp
    public string CurrentThemeLabel => _theme.Current switch
    {
        AppTheme.Light => _loc["Theme_Light"],
        AppTheme.Dark => _loc["Theme_Dark"],
        _ => _loc["Theme_System"]
    };
```

新增标题栏组合属性（替代 XAML 里的中文 StringFormat）：
```csharp
    public string TitleBarText => _loc.Format("Shell_TitleBarFormat", CurrentTitle);
```

`_healthText` 初值改在 ctor 设（不能用字段初始化器引用 `_loc`）：把字段声明改为
```csharp
    [ObservableProperty] private string _healthText = string.Empty;
```
ctor 末尾（订阅前）加：
```csharp
        HealthText = _loc["Health_AllNormal"];
```

`RefreshHealth()` 内文案改资源：
```csharp
        if (errorTasks > 0) { HealthOk = false; HealthText = _loc.Format("Health_Warnings", errorTasks); }
        else { HealthOk = true; HealthText = _loc["Health_AllNormal"]; }
```

`Navigate()` 里 `CurrentTitle` 仍存路由 key→解析为显示名,并刷新 TitleBarText：
```csharp
            SelectedRouteKey = routeKey;
            CurrentTitle = _loc[_nav.Routes.FirstOrDefault(r => r.Key == routeKey)?.Title ?? string.Empty];
            OnPropertyChanged(nameof(TitleBarText));
```

ctor 内订阅主题/语言变化以刷新派生文案：
```csharp
        _theme.ThemeChanged += _ => OnPropertyChanged(nameof(CurrentThemeLabel));
        language.LanguageChanged += _ =>
        {
            OnPropertyChanged(nameof(CurrentThemeLabel));
            CurrentTitle = _loc[_nav.Routes.FirstOrDefault(r => r.Key == SelectedRouteKey)?.Title ?? string.Empty];
            OnPropertyChanged(nameof(TitleBarText));
            RefreshHealth();
        };
```

- [ ] **步骤 5：ShellWindow.xaml 文案 → {loc:Loc}**

根元素加 `xmlns:loc="clr-namespace:Dc.App.Markup"`。替换：
- `Title="Dc · OPC 数据采集"`（FluentWindow，第 6 行）→ 保留为应用名常量（窗口任务栏标题，可不译；若译则用 `{loc:Loc Tray_Tooltip}` 不适用——窗口 Title 不支持 loc 绑定的场景下保留原文即可）。**保留不改。**
- `ui:TitleBar` 的 `Title="{Binding CurrentTitle, ... StringFormat='Dc · OPC 数采  ›  {0}'}"` → `Title="{Binding TitleBarText, Mode=OneWay}"`
- tray `ToolTipText="Dc · OPC 数据采集"` → `ToolTipText="{loc:Loc Tray_Tooltip}"`
- tray 菜单 `Header="显示主窗口"/"切换主题"/"关于…"/"退出"` → `{loc:Loc Tray_Show}`/`{loc:Loc Tray_ToggleTheme}`/`{loc:Loc Tray_About}`/`{loc:Loc Tray_Exit}`
- footer `NavigationViewItem Content="关于"` → `Content="{loc:Loc Nav_About}"`
- 状态栏 `<Run Text="主题 · " />` → `<Run Text="{loc:Loc Shell_StatusTheme}" />`；`<Run Text="    ·    DB · " />` → `<Run Text="{loc:Loc Shell_StatusDb}" />`
- `Text="Dc · v1.0.0"` → 保留（版本号常量）。

ShellWindow.xaml.cs 的 tray 气泡（`OnClosing` 内 `TrayIcon.ShowNotification`）：
```csharp
            TrayIcon.ShowNotification(
                title: _loc["Tray_BalloonTitle"],
                message: _loc["Tray_BalloonMessage"],
                icon: NotificationIcon.Info);
```

- [ ] **步骤 6：更新 ShellViewModelTests 构造**

`ShellViewModelTests.cs` 顶部加 `using Dc.App.Services.I18n;`。加一个 fake 语言服务，`Build()` 构造 VM 时补两参：

```csharp
    private sealed class FakeLanguageService : ILanguageService
    {
        public AppLanguage Current => AppLanguage.System;
        public event Action<AppLanguage>? LanguageChanged { add { } remove { } }
        public void Initialize() { }
        public void Apply(AppLanguage lang) { }
    }
```
把 `var vm = new ShellViewModel(nav.Object, theme.Object);` 改为：
```csharp
        var vm = new ShellViewModel(nav.Object, theme.Object, new ResourceLocalizer(), new FakeLanguageService());
```
> 这些测试只断言 `SelectedRouteKey`/`CurrentContent`，不断言标题文本，故不受语言影响（测试路由 Title 是任意串，`ResourceLocalizer` 找不到对应 key 时回退返回 key 本身）。

- [ ] **步骤 7：构建 + 测试 + 平价**

运行：`~/dc-remote.sh office sync && ~/dc-remote.sh office build && ~/dc-remote.sh office test tests/Dc.App.Tests/Dc.App.Tests.csproj`
预期：0 错；平价测试 PASS；ShellViewModelTests 全过；无回归。

- [ ] **步骤 8：真机端到端验证（第一次可切！）**

```
~/dc-remote.sh office stop
~/dc-remote.sh office run
~/dc-remote.sh office shot            # 默认中文(或跟随系统)
~/dc-remote.sh office ui click "设置"
# 在设置页点 English 单选(若 ui click 命中困难,见 dc-remote ui 用法)
~/dc-remote.sh office ui click "English"
~/dc-remote.sh office shot            # 导航栏/标题栏/状态栏/托盘应变英文
```
预期：切 English 后导航栏标签、分组头、标题栏、状态栏「Theme · / DB ·」即时变英文；切回「简体中文」恢复。Read 截图确认。

- [ ] **步骤 9：Commit**

```bash
git add src/Dc.App/Navigation/NavigationRoute.cs src/Dc.App/Composition/ServiceRegistration.cs src/Dc.App/Views/Shell/ src/Dc.App/ViewModels/Shell/ShellViewModel.cs src/Dc.App/Resources/ tests/Dc.App.Tests/ViewModels/Shell/ShellViewModelTests.cs
git commit -m "feat(i18n): 导航栏/标题栏/状态栏/托盘本地化 + 语言切换重建导航"
```

---

# Phase 3 — 全量抽取（分批）

## 抽取微循环（所有批次通用）

对批次内**每个文件**，重复这个 2-5 分钟微循环：

1. 找出文件里所有中文字面量（XAML 的 `Text=`/`Header=`/`Content=`/`controls:Placeholder.Text=`/`ToolTip=` 等；`.cs` 的字符串字面量：toast/`MessageDialog`/`MessageBox`/校验消息/重启提示等）。
2. 为每条想一个 `Area_Meaning` key，**同时**写进 `Strings.resx`（中文原文）和 `Strings.en.resx`（英文翻译）。带运行时变量的用 `{0}`/`{1}` 占位（如 `Toast_TagsAdded` = `已添加 {0} 个 Tag 到 {1}` / `Added {0} tag(s) to {1}`）。
3. 替换引用：
   - **XAML**：根元素确保有 `xmlns:loc="clr-namespace:Dc.App.Markup"`；`Text="中文"` → `Text="{loc:Loc Area_Meaning}"`。
   - **`.cs`（VM/Service）**：构造注入 `ILocalizer`（DI 已注册；默认激活的 VM 自动注入，工厂 lambda 构造的 VM 在 `ServiceRegistration` 里补 `sp.GetRequiredService<ILocalizer>()`）。即时串 `"中文"` → `_loc["Area_Meaning"]`；带参 → `_loc.Format("Area_Meaning", a, b)`。
   - **绑定显示串**（`[ObservableProperty] _title = "中文"` 等）：优先把展示点改 XAML `{loc:Loc}` 并删除该 VM 串；确需 VM 计算保留的，改为 `_loc[...]` 且在 `LanguageChanged` 时 `OnPropertyChanged`。
4. 该文件改完，整批改完后统一构建 + 平价测试。
5. 批次末尾构建通过 + 平价 PASS + 真机抽查 → commit。

**注入 ILocalizer 到工厂构造的 VM**：`ServiceRegistration` 中以 lambda 构造的 VM（`TagsViewModel`/`LiveDataViewModel`/`BrowseViewModel`/`DiagnosticsViewModel`/`TaskWorkspaceViewModel` 等）需在其构造参数加 `ILocalizer`，并在 lambda 里传 `sp.GetRequiredService<Dc.App.Services.I18n.ILocalizer>()`。对话框服务（`MessageDialog` 是静态类）见批次 C 的专门处理。

> 不预列全部 ~875 条 key：它们是执行期按上面微循环逐文件产出的数据，平价测试 + 构建是硬护栏。下面每批给出**文件清单**与**示例 key**，确保覆盖无遗漏。

---

### 任务 12：批次 A — 主要视图静态文本（XAML）

**文件（逐个走微循环）：**
- `src/Dc.App/Views/TagsView.xaml`（搜索/查询/新建/导入/导出/列头 Item·数据类型·任务·创建时间/空状态）
- `src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml` + `Workspace/OverviewTabPanel.xaml` + `Workspace/ConfigTabPanel.xaml`
- `src/Dc.App/Views/BrowseView.xaml`（连接条/协议/端点/连接/地址树/批量加 Tag 动作条/任务选择器）
- `src/Dc.App/Views/LiveDataView.xaml` + `src/Dc.App/Views/DiagnosticsView.xaml`（列头/筛选/空状态/CTA）
- `src/Dc.App/Views/LogsView.xaml` + `src/Dc.App/Views/Dashboard/DashboardView.xaml`
- `src/Dc.App/Views/PlaceholderView.xaml`
- `src/Dc.App/Views/SettingsView.xaml`（剩余静态文本：页标题/外观/主题及说明/系统配置项/各按钮/备份与恢复/列头）

**示例 key：** `Tags_Search`/`Tags_Query`/`Common_New`/`Common_Import`/`Common_Export`/`Tag_Col_Item`/`Tag_Col_DataType`/`Tag_Col_Task`/`Tag_Col_CreatedAt`/`TagsEmpty_Title`/`TagsEmpty_Hint`/`TagsEmpty_Action`/`Settings_Title`/`Settings_Appearance`/`Settings_Theme`/`Settings_ThemeHint`/`Settings_ConfigItems`/`Settings_Backup` …

- [ ] **步骤 1：逐文件走微循环**（XAML 静态文本 → `{loc:Loc}`，key 双写 resx）
- [ ] **步骤 2：构建 + 平价**：`~/dc-remote.sh office sync && ~/dc-remote.sh office build && ~/dc-remote.sh office test tests/Dc.App.Tests/Dc.App.Tests.csproj`（平价 PASS）
- [ ] **步骤 3：真机抽查**：`run` + `shot`，切 English 看各页静态文本变英文
- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/Views/ src/Dc.App/Resources/
git commit -m "feat(i18n): 主要视图静态文本本地化(批次 A)"
```

---

### 任务 13：批次 B — 对话框窗口（XAML + 少量 code-behind）

**文件：**
- `src/Dc.App/Views/TaskEditorWindow.xaml`（任务名/服务器/节点/采样间隔/死区/TCP 地址等标签 + 标题 + 保存/取消）
- `src/Dc.App/Views/TagEditorWindow.xaml`（Item/数据类型/虚拟测点/公式/浏览/保存/取消 + `AutomationProperties.Name`）
- `src/Dc.App/Views/ConfigEntryEditorWindow.xaml`（Key/Value/描述/保存/取消）
- `src/Dc.App/Views/MessageDialogWindow.xaml`（确定/取消等按钮）
- `src/Dc.App/Views/AboutWindow.xaml`（关于内容）
- `src/Dc.App/Views/BrowseDialogWindow.xaml`（标题/选择/取消）
- 对应 `*.xaml.cs` 中若有中文（按 `grep` 清单：`BrowseDialogWindow.xaml.cs`/`ConfigEntryEditorWindow.xaml.cs`/`MessageDialogWindow.xaml.cs`/`TagEditorWindow.xaml.cs`/`TaskEditorWindow.xaml.cs`/`ModalWindowBase.cs`/`DiagnosticsView.xaml.cs`/`LiveDataView.xaml.cs`/`LogsView.xaml.cs`/`Workspace/TaskWorkspaceView.xaml.cs`）

**示例 key：** `TaskEditor_Title_New`/`TaskEditor_Title_Edit`/`TaskEditor_Name`/`TaskEditor_Server`/`TaskEditor_Node`/`TaskEditor_Interval`/`TaskEditor_Deviation`/`TaskEditor_TcpAddr`/`Common_Save`/`Common_Cancel`/`TagEditor_Item`/`TagEditor_Virtual`/`TagEditor_Formula`/`TagEditor_Browse`/`Config_Key`/`Config_Value`/`Config_Desc` …

- [ ] **步骤 1：逐文件走微循环**
- [ ] **步骤 2：构建 + 平价**（命令同上，PASS）
- [ ] **步骤 3：真机抽查**：打开新建任务/新建 Tag/新建配置对话框，切 English 看标签/按钮英文（`ui click` + `shot`）
- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/Views/ src/Dc.App/Resources/
git commit -m "feat(i18n): 对话框窗口本地化(批次 B)"
```

---

### 任务 14：批次 C — ViewModel/Service 运行时字符串

把所有 toast/校验红字/确认弹窗/重启提示/错误消息本地化。

**关键接线 — `MessageDialog` 静态类**：`src/Dc.App/Services/MessageDialog.cs` 是静态 `Show/Confirm`，无法注入。它接收**调用方传入的字符串**，故本地化在**调用点**完成（调用方注入 `_loc` 后传 `_loc[...]`）。`MessageDialog` 内部若有自身中文（如默认按钮文字）→ 改走 `LocalizationManager.Instance[...]`（静态可达）。

**文件（逐个走微循环；构造注入 `ILocalizer`）：**
- VM：`SettingsViewModel`（导出/导入成功失败消息、导入模式 MessageBox 文案）、`TagsViewModel`、`TagEditorViewModel`、`TaskEditorViewModel`、`ConfigEntryEditorViewModel`、`BrowseViewModel`、`BrowseNodeRowViewModel`、`LiveDataViewModel`、`LiveDataRowViewModel`、`DiagnosticsViewModel`、`DiagnosticsRowViewModel`、`LogsViewModel`、`AboutViewModel`、`Dashboard/DashboardViewModel`、`Workspace/TaskWorkspaceViewModel`、`Workspace/WorkspaceOverviewViewModel`、`Workspace/DbWorkspaceTaskSource`、`Workspace/TaskNames`
- Service：`SnackbarNotificationService`、`WpfConfirmDialog`、`WpfBrowseDialog`、`MessageDialog`、`TagEditResult`、`IBrowseDialog`（若含用户可见串）
- 其它含中文：`Controls/EmptyState.cs`、`Dashboard/HealthEvaluator.cs`、`Views/Dashboard/Converters.cs`（若产出用户可见文本）

**DI 接线**：在 `ServiceRegistration` 中所有以 lambda 构造、且本批加了 `ILocalizer` 参数的 VM，补 `sp.GetRequiredService<Dc.App.Services.I18n.ILocalizer>()` 实参（`TagsViewModel`/`LiveDataViewModel`/`BrowseViewModel`/`DiagnosticsViewModel`/`TaskWorkspaceViewModel`/`WorkspaceConfigViewModel`/`WorkspaceOverviewViewModel`/`DashboardViewModel` 等）。`MessageDialog` 内部串改 `LocalizationManager.Instance[...]`。

**示例 key（带占位符）：** `Toast_TagsAdded`=`已添加 {0} 个 Tag 到 {1}`/`Added {0} tag(s) to {1}`；`Validation_NameRequired`；`Confirm_DeleteTask`=`确定删除任务 {0}?`；`Restart_Required`；`Backup_ExportOk`=`已导出 {0} 任务 / {1} Tag / {2} 配置到 {3}`；`Import_ModePrompt` …

- [ ] **步骤 1：逐文件走微循环 + 补 DI 实参**
- [ ] **步骤 2：构建 + 全测**：`~/dc-remote.sh office sync && ~/dc-remote.sh office build && ~/dc-remote.sh office test tests/Dc.App.Tests/Dc.App.Tests.csproj`（平价 PASS；既有 VM 测试若因构造签名变化编译失败，按需在测试里补 `new ResourceLocalizer()` 实参）
- [ ] **步骤 3：真机抽查**：触发一次 toast（如批量加 Tag）、一次校验、一次删除确认，切 English 看英文
- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/ tests/Dc.App.Tests/ src/Dc.App/Resources/
git commit -m "feat(i18n): VM/Service 运行时字符串本地化(批次 C)"
```

---

### 任务 15：批次 D — 枚举显示名 + 收尾扫描

- [ ] **步骤 1：OpcDataTypeOption 走资源**

`src/Dc.App/ViewModels/OpcDataTypeOption.cs`：`"默认"` 与 `$"未知({code})"` 改为资源。因这是静态 `record`/列表，用 `LocalizationManager.Instance`：

```csharp
        new OpcDataTypeOption(0, LocalizationManager.Instance["DataType_Default"]),
```
`FromCode` 的未知分支：
```csharp
        All.FirstOrDefault(o => o.Code == code)
            ?? new OpcDataTypeOption(code, LocalizationManager.Instance.Format("DataType_Unknown", code));
```
（加 `using Dc.App.Services.I18n;`。）resx 加 `DataType_Default`=`默认`/`Default`、`DataType_Unknown`=`未知({0})`/`Unknown({0})`。
> 注：类型名 Boolean/Int32… 通用,保持原文不入资源。`All` 列表是静态初始化,语言切换后已建对象的显示名不会自动刷新——`OpcDataTypeOption` 仅用于编辑器下拉(每次打开重取 `FromCode`),可接受;若发现下拉残留旧语言,改为方法每次构造。

- [ ] **步骤 2：全仓中文扫描兜底**

运行：`grep -rnP '[\x{4e00}-\x{9fff}]' src/Dc.App --include='*.xaml' --include='*.cs' | grep -v '/obj/' | grep -v '/bin/'`
逐条判断：用户可见 → 按微循环补 key；非可见（代码注释、日志 `Log.X`、`AutomationId` 之类）→ 跳过。注释与 Serilog 日志**不**本地化（范围外）。

- [ ] **步骤 3：构建 + 全测 + 平价**（PASS）
- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/ src/Dc.App/Resources/
git commit -m "feat(i18n): 枚举显示名本地化 + 收尾中文扫描(批次 D)"
```

---

### 任务 16：全量验收 + 收尾

- [ ] **步骤 1：全解决方案构建 + 全测**

```
~/dc-remote.sh office sync
~/dc-remote.sh office build
~/dc-remote.sh office test tests/Dc.App.Tests/Dc.App.Tests.csproj
```
预期：构建 0 错；`Dc.App.Tests` 全过（含 i18n 单测 + 平价测试）；既有测试无回归。

- [ ] **步骤 2：跨平台不回归**（Linux 本机）：`dotnet build src/Dc.Cli/Dc.Cli.csproj -c Release`（Cli 未被触及，应 0 错）。

- [ ] **步骤 3：真机全量验收（dc-remote office）**

```
~/dc-remote.sh office stop && ~/dc-remote.sh office run
~/dc-remote.sh office shot                 # 首屏(跟随系统)
# 设置→English：逐页 shot(仪表盘/采集任务/浏览/实时数据/诊断/日志/设置 + 新建任务/Tag/配置对话框)
# 设置→简体中文：抽查恢复
# 设置→English→stop→run：确认 appsettings.json Language=English 持久化、重启仍英文、首屏无中文闪现
~/dc-remote.sh office psh 'Get-Content "D:\code\wpf-opc-client\src\Dc.App\bin\x64\Release\net8.0-windows\appsettings.json" | Select-String Language'
```
预期：① 全界面（含导航栏/Toast/弹窗/枚举/状态栏/托盘）随语言切换；② 重启持久化；③ 日期/数值格式不变；④ 无残留中文（除日志/注释）。Read 截图逐一确认。

- [ ] **步骤 4：架构 + 收尾**

运行 `opc-arch-reviewer` 子代理确认无分层越界（i18n 全在 App 层，预期通过）。确认 `Dc.Cli`/Serilog 日志未改。

- [ ] **步骤 5：合并交付**

按 `finishing-a-development-branch` 技能收尾：构建/测试全绿后，提供合并/PR/清理选项；PR 描述关联 issue #2（`Closes #2`），附 English/中文切换截图。

---

## 自检结果

**规格覆盖度**（对照规格各节）：
- §3 架构 → 任务 2-7、9（全部类型 + DI）。
- §4 资源与 key → 任务 1（基座）+ 各抽取批次（key 产出）。
- §5 运行时切换（只切 UICulture）→ 任务 4 `CultureLanguageApplier.Apply`。
- §6 全量覆盖分类 → XAML(批次 A/B)、VM 即时串(批次 C)、VM 绑定串(批次 A/C + Shell 任务 11)、导航栏(任务 11)、枚举(批次 D)。
- §7 测试（4 类单测 + 平价 + 真机）→ 任务 2/4/5/6/8/10 + 任务 16。
- §8 涉及文件 → 文件结构表 + 各任务路径。
- §9 范围外（Cli/日志/注释）→ 任务 15 步骤 2、任务 16 步骤 2/4 明确不碰。

**占位符扫描**：基础设施任务（1-11）均含完整代码与精确命令；抽取批次（12-15）的“逐条 key”是执行期数据，已给微循环 + 文件清单 + 示例 key + 平价护栏，非占位（875 条字面量不可能也不应预列）。

**类型一致性**：`ILocalizer`/`LocalizationManager`/`ILanguageService`/`AppLanguage`/`CultureLanguageApplier.ResolveSupported`/`LanguageService.Map` 在定义与使用处签名一致；`LocExtension` 绑定 `LocalizationManager.Instance` 索引器与任务 2 一致；resx key 命名 `Area_Meaning` 全程统一。
