# Dc.App 双语运行时切换 i18n 设计

- **日期**:2026-06-23
- **状态**:已批准设计,待实现
- **触发**:GitHub issue #2(@aemzayn,“Can you add english to the UI?”)
- **范围**:仅 `Dc.App`(WPF / net8.0-windows)。`Dc.Cli` 无头、Serilog 日志均不在范围。

## 1. 背景与目标

Dc.App 目前 UI 文本**全中文硬编码**,无任何 `.resx` 或本地化设施(约 875 处:343 在 XAML,532 在 ViewModels/Services 的 `.cs`)。

目标:给 UI **增加**英文(而非替换中文),**运行时实时切换免重启**,选择持久化。中文用户体验不受影响。

## 2. 已锁定的决策

| 决策 | 选择 |
|---|---|
| 覆盖范围 | **全量**:XAML 静态文本 + 所有运行时字符串(Toast / 校验 / 弹窗 / 重启提示 / 枚举显示名 / 导航栏标签) |
| 默认语言 | **跟随系统**;首次启动按 OS UI culture 解析(`zh-Hans*`/`zh-CN` → 中文,否则英文) |
| 语言菜单 | 3 项:简体中文 / English / 跟随系统 |
| 实现机制 | **原生 `.resx` + 自写 `LocalizationManager`(INPC 索引器)+ `{loc:Loc}` 标记扩展**;VM 注入 `ILocalizer`。零第三方依赖 |
| 持久化 | `appsettings.json` 的 `"Language"` 键(与既有 `"Theme"` 并列) |
| 切换方式 | 运行时实时刷新,免重启 |
| 译文来源 | 由实现方产出(领域术语 OPC/SCADA),用户在 PR/spec 复核 |

设计**镜像现有主题(Theme)栈**——一套已验证的“运行时切换 + 持久化 + 设置页单选”模式,降低风险、保持代码一致性。

## 3. 架构

新增目录 `src/Dc.App/Services/I18n/`,与 `Services/Theme/` 一一对应:

| 主题栈(现有,模板) | 语言栈(新增) | 职责 |
|---|---|---|
| `enum AppTheme {Light,Dark,System}` | `enum AppLanguage {System,ChineseSimplified,English}` | 选项枚举 |
| `IThemeService` | `ILanguageService` | 编排:Initialize/Apply/Current/事件 |
| `ThemeService` | `LanguageService` | 读 `IConfiguration["Language"]` → Apply → 持久化 → 发事件 |
| `IThemeApplier` / `WpfUiThemeApplier` | `ILanguageApplier` / `CultureLanguageApplier` | 设 `CurrentUICulture` + 通知 manager;解析 System→culture |
| `IThemePreferenceWriter` / `JsonThemePreferenceWriter` | `ILanguagePreferenceWriter` / `JsonLanguagePreferenceWriter` | 写 `appsettings.json` 的 `Language` 键(失败不抛) |
| `ThemeSettingsViewModel` | `LanguageSettingsViewModel` | 绑设置页单选 |

i18n 特有的两件(主题不需要,因为主题靠 `{DynamicResource}` 自动刷):

- **`LocalizationManager`**(单例,`INotifyPropertyChanged`):
  - 索引器 `public string this[string key] => Strings.ResourceManager.GetString(key, _culture) ?? key;`
  - `void SetCulture(CultureInfo c)`:更新内部 culture 字段,发 `PropertyChanged(Binding.IndexerName)`(即 `"Item[]"`)。
  - 静态 `Instance` 供 XAML 标记扩展引用。
- **`LocExtension : MarkupExtension`**(XML 命名空间 `xmlns:loc`):
  - 用法 `Text="{loc:Loc Settings_Title}"`。
  - `ProvideValue` 返回 `new Binding($"[{Key}]") { Source = LocalizationManager.Instance, Mode = OneWay }`。
  - culture 变化时索引器 INPC 触发,所有此类绑定重取 → 实时刷。
- **`ILocalizer` / `ResourceLocalizer`**:供 ViewModel/Service 注入,`string this[string key]` / `string Format(string key, params object[] args)`,内部走同一 `LocalizationManager` / `Strings.ResourceManager`,保证即时串按当前 culture 取。

### 类型签名(供实现对齐)

```csharp
public interface ILanguageService
{
    AppLanguage Current { get; }
    event Action<AppLanguage>? LanguageChanged;
    void Initialize();           // 读 IConfiguration["Language"] → Apply 一次(不发事件、不写盘)
    void Apply(AppLanguage lang);
}

public interface ILanguageApplier
{
    void Apply(CultureInfo effective);   // 设 CurrentUICulture + DefaultThreadCurrentUICulture + 通知 LocalizationManager
    CultureInfo DetectSystemCulture();   // OS UI culture → 受支持 culture(zh-CN | en)
}

public interface ILanguagePreferenceWriter { void Write(AppLanguage lang); }

public interface ILocalizer
{
    string this[string key] { get; }
    string Format(string key, params object[] args);
}
```

`LanguageService.Apply` 逻辑(镜像 `ThemeService.Apply`):
```
effective = lang == System ? applier.DetectSystemCulture() : Map(lang)   // ChineseSimplified→zh-CN, English→en
applier.Apply(effective)
_current = lang
if (raiseEvent) { writer?.Write(lang); LanguageChanged?.Invoke(lang); }
```

## 4. 资源与 key 策略

- `src/Dc.App/Resources/Strings.resx`(**中性文化 = zh-CN**)+ `src/Dc.App/Resources/Strings.en.resx`(英文)。
- `[assembly: NeutralResourcesLanguage("zh-CN", UltimateResourceFallbackLocation.MainAssembly)]`(放 `AssemblyInfo` 或 csproj):中文走主程序集,英文走 `en/Dc.App.resources.dll` 卫星程序集。
- 文件夹形态发布(exe+dll)天然带卫星程序集;**不影响** “不用 PublishSingleFile/Trimmed” 的约束。
- `.resx` 启用强类型生成 → `Dc.App.Resources.Strings`(`internal`),`ResXFileCodeGenerator`。
- **key 命名**:`Area_Meaning`,按视图/领域分组。示例:
  - 通用:`Common_Save` `Common_Cancel` `Common_Delete` `Common_Edit` `Common_New` `Common_Refresh`
  - 设置:`Settings_Title` `Settings_Appearance` `Settings_Theme` `Settings_Language`
  - Tag 列:`Tag_Col_DataType` `Tag_Col_Task`
  - 即时串(带占位符):`Toast_TagsAdded`(值含 `{0}`/`{1}`)`Validation_NameRequired`
- 缺译回退:某 key 在 `en.resx` 缺失 → `ResourceManager` 自动回退中性(zh-CN);平价测试(§7)防止此情况静默发生。

## 5. 运行时切换机制

- 只设 **`CurrentUICulture`**(`Thread.CurrentThread.CurrentUICulture` + `CultureInfo.DefaultThreadCurrentUICulture`),**不动 `CurrentCulture`**。
  - 原因:`CurrentCulture` 控制日期/数值格式与解析;UI 用的 `StringFormat={}{0:yyyy-MM-dd HH:mm:ss}` 是文化中性模式,OPC 数值解析也需稳定。只切 UICulture 隔离“界面语言”与“数据格式”。
- `LocalizationManager.SetCulture` 发 `PropertyChanged("Item[]")` → 所有 `{loc:Loc}` 绑定重取。
- `System` 解析(`DetectSystemCulture`):取 `CultureInfo.InstalledUICulture`(或 `CultureInfo.CurrentUICulture`),若 `Name` 以 `zh` 开头 → `zh-CN`,否则 → `en`。

## 6. 全量覆盖落法(按字符串性质分类)

| 类别 | 处理方式 | 实时刷 |
|---|---|---|
| XAML 静态文本(343) | 替换为 `{loc:Loc Key}` | ✅ 索引器 INPC |
| VM 即时串(Toast / `MessageDialog` / `MessageBox` / 校验红字 / 重启提示) | 注入 `ILocalizer`,弹出时按当前 culture 取 | ✅ 取值即当前语言 |
| VM 绑定显示串(`SettingsViewModel.Title`、各页 `Title`/`EmbeddedTitle` 等) | 优先把展示点改 XAML `{loc:Loc}`;确需 VM 计算的,VM 订阅 `LanguageChanged` 后 `OnPropertyChanged(nameof(...))` | ✅ |
| 导航栏标签 + 分组头(`ServiceRegistration` 里 `NavigationRoute("仪表盘"…)`/`GroupHeader:"采集"`) | `NavigationRoute` 改携带 loc **key**;ShellView 渲染项经 `LocalizationManager` 索引器绑定 Content/Header | ✅ |
| 枚举显示名(`OpcDataTypeOption` 的「默认」「未知(...)」) | 走资源 key(其余 `Boolean`/`Int32` 等类型名通用,保持原样) | 取值即当前语言 |

> 导航栏是唯一需特殊接线处:路由标签在 DI 注册时构造一次,故不能存“已解析的中文串”,而要存 key 并在视图层经 manager 绑定;否则切换不刷新。

## 7. 测试

单元测试(`Dc.App.Tests`):
1. `LocalizationManager` 按 culture 取值正确;未知 key 返回 key 本身;缺译回退中性。
2. `DetectSystemCulture`:`zh-*`→zh-CN、其它→en 的映射。
3. `JsonLanguagePreferenceWriter` 往返:写后再读 `appsettings.json` 的 `Language` 键值正确;文件不存在/非法 JSON 不抛。
4. **resx 平价测试**:加载 `Strings.resx` 与 `Strings.en.resx` 的 key 集合,断言**完全相等**(无缺译、无多余 key)。

真机验证(dc-remote,office):
- 切 English → 全界面(含导航栏/Toast/弹窗/枚举)英文;切回简体中文;切“跟随系统”。
- 重启后语言保持;首屏即正确语言(无中文闪现)。
- 日期/数值显示格式不变。

## 8. 涉及文件

**新增**
- `src/Dc.App/Services/I18n/AppLanguage.cs`
- `src/Dc.App/Services/I18n/ILanguageService.cs` / `LanguageService.cs`
- `src/Dc.App/Services/I18n/ILanguageApplier.cs` / `CultureLanguageApplier.cs`
- `src/Dc.App/Services/I18n/ILanguagePreferenceWriter.cs` / `JsonLanguagePreferenceWriter.cs`
- `src/Dc.App/Services/I18n/LocalizationManager.cs`
- `src/Dc.App/Services/I18n/ILocalizer.cs` / `ResourceLocalizer.cs`
- `src/Dc.App/Markup/LocExtension.cs`
- `src/Dc.App/Resources/Strings.resx` / `Strings.en.resx`(+ 生成的 `Strings.Designer.cs`)
- `src/Dc.App.Tests/I18n/*`(上述单测)

**修改**
- `src/Dc.App/App.xaml.cs`:`OnStartup` 加 `langSvc.Initialize()`(在 `themeSvc.Initialize()` 旁、`window.Show()` 前)。
- `src/Dc.App/Composition/ServiceRegistration.cs`:注册语言栈 + `LocalizationManager`/`ILocalizer`;`NavigationRoute` 标签/分组头改 key。
- `src/Dc.App/appsettings.json`:加 `"Language": "System"`。
- `src/Dc.App/Views/SettingsView.xaml` + `ViewModels/SettingsViewModel.cs`:加「语言」单选行 + `LanguageSettingsViewModel`。
- 全部 20 个 `*.xaml`:静态文本 → `{loc:Loc}`。
- ViewModels/Services 中含中文串的 `.cs`:注入 `ILocalizer`,即时串改取资源;绑定串按 §6 处理。
- `src/Dc.App/ViewModels/OpcDataTypeOption.cs`:「默认」「未知」走资源。
- `src/Dc.App/Dc.App.csproj`(或 `AssemblyInfo`):`NeutralResourcesLanguage` 特性 + resx 生成项。
- `src/Dc.App/Views/Shell/*`:导航项 Content/Header 经 manager 绑定 key。

## 9. 范围外 / YAGNI / 风险

**范围外**:`Dc.Cli`;Serilog 日志(诊断面,保持中文);RTL 布局;复数/ICU 消息格式;>2 语言的额外设施(resx 已天然支持更多 culture,无需预建)。

**风险与缓解**:
- ~875 处机械抽取量大 → 是主要工时;按视图分批,平价测试兜底防漏。
- 英文译文领域术语 → 实现方产出后用户在 PR 复核。
- 导航栏实时刷 → 唯一需特殊接线处,§6 已给方案。
- 卫星程序集发布 → 文件夹发布天然带上,已确认不与“禁单文件/裁剪”冲突。

## 10. 实现顺序(供 writing-plans 细化)

1. 基础设施:枚举/服务/applier/writer/manager/markup/localizer + DI 注册 + appsettings + App 启动接线。
2. 设置页语言单选 + `LanguageSettingsViewModel`(先能切,验证机制通)。
3. 平价测试 + 单测先行(TDD)。
4. 分批抽取:导航栏 → 设置页 → 工作台/Tag → 浏览 → 实时数据/诊断 → 日志/仪表盘 → 各对话框/即时串 → 枚举。
5. 真机全量验证。
