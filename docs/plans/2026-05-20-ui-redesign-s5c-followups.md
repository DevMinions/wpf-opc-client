# UI 重设计 S5c — 功能补缺：跟随系统主题 + 工作台导入接通

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).

**Goal:** 补两个功能性欠账 —— ① 「跟随系统」主题运行时跟随 OS 切换（订阅 `SystemEvents.UserPreferenceChanged`）；② 工作台「导入」按钮接通（之前 no-op），跳 Tag tab 并触发 Excel 导入。

**Architecture:**
①新增 `ISystemThemeWatcher` 抽象（隔离 Windows-only 的 `SystemEvents`，便于单测）+ `SystemEventsThemeWatcher` 实现。`ThemeService` 注入可选 watcher，OS 主题变 + 当前为 `System` 档时重新解析下发（不改 Current、不持久化）。
②`IEmbeddableTagPanel` 加 `Task ImportAsync()`，`TagsViewModel.ImportAsync` 改 public；工作台 `ImportAsync()` 委托给 TagsPanel + 切 Tag tab + 刷新。

**Tech Stack:** .NET 8 + WPF + CommunityToolkit.Mvvm + Moq + xUnit
**前置:** S5a 完成（commit 8d38c5f）

---

## 已确认 API
- `ThemeService` ctor：`(IConfiguration, IThemeApplier, IThemePreferenceWriter? = null)`；私有 `Apply(theme, raiseEvent)`，`Initialize()` 调 `Apply(initial, false)`；`_current` 字段
- `IThemeApplier`：`void Apply(AppTheme effective)` + `AppTheme DetectSystemTheme()`
- `TagsViewModel`：`[RelayCommand] private async Task ImportAsync()`（line ~189，用 `_filePicker` + `_excel`）；实现 `IEmbeddableTagPanel`
- `IEmbeddableTagPanel`（现）：`IsEmbedded` / `TaskScope` / `GroupFilter` / `Task LoadAsync()`
- `TaskWorkspaceViewModel.ImportAsync()` 现为 `=> Task.CompletedTask`；有 `SelectedTab` / `TagsPanel`(IEmbeddableTagPanel) / `LoadAsync()`
- ThemeService 注册 `AddSingleton<IThemeService, ThemeService>()`（自动构造，会注入新注册的 watcher）

构建命令：
```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```
Linux 上 Dc.App.Tests runtime 跑不了，build 验证为准。

---

## Task 1: ISystemThemeWatcher + ThemeService 跟随系统 (TDD)

**Files:**
- Create: `wpf/src/Dc.App/Services/Theme/ISystemThemeWatcher.cs`
- Create: `wpf/src/Dc.App/Services/Theme/SystemEventsThemeWatcher.cs`
- Modify: `wpf/src/Dc.App/Services/Theme/ThemeService.cs`
- Modify: `wpf/tests/Dc.App.Tests/Services/Theme/ThemeServiceTests.cs`

- [ ] **Step 1: 接口**

`wpf/src/Dc.App/Services/Theme/ISystemThemeWatcher.cs`:
```csharp
namespace Dc.App.Services.Theme;

/// 监听 OS 主题（亮/暗）变化。隔离 Windows-only SystemEvents 以便单测。
public interface ISystemThemeWatcher
{
    event Action? SystemThemeChanged;
    void Start();
}
```

- [ ] **Step 2: 实现（Windows SystemEvents，无单测，薄包装）**

`wpf/src/Dc.App/Services/Theme/SystemEventsThemeWatcher.cs`:
```csharp
using Microsoft.Win32;

namespace Dc.App.Services.Theme;

public sealed class SystemEventsThemeWatcher : ISystemThemeWatcher
{
    public event Action? SystemThemeChanged;

    public void Start() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // General 覆盖亮/暗主题切换；Color 兜底
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            SystemThemeChanged?.Invoke();
    }
}
```

- [ ] **Step 3: 写 ThemeService 测试（Red）**

在 `ThemeServiceTests.cs` 加 fake watcher + 3 测试：
```csharp
    private sealed class FakeWatcher : ISystemThemeWatcher
    {
        public event Action? SystemThemeChanged;
        public bool StartCalled;
        public void Start() => StartCalled = true;
        public void Raise() => SystemThemeChanged?.Invoke();
    }

    [Fact]
    public void Initialize_StartsSystemWatcher()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);
        var watcher = new FakeWatcher();
        var svc = new ThemeService(ConfigWithTheme("System"), applier.Object, null, watcher);
        svc.Initialize();
        Assert.True(watcher.StartCalled);
    }

    [Fact]
    public void SystemThemeChange_WhenFollowingSystem_Reapplies()
    {
        var applier = new Mock<IThemeApplier>();
        applier.SetupSequence(a => a.DetectSystemTheme())
               .Returns(AppTheme.Light)   // Initialize
               .Returns(AppTheme.Dark);    // OS 切到暗
        var watcher = new FakeWatcher();
        var svc = new ThemeService(ConfigWithTheme("System"), applier.Object, null, watcher);
        svc.Initialize();
        applier.Invocations.Clear();

        watcher.Raise();

        applier.Verify(a => a.Apply(AppTheme.Dark), Times.Once);
    }

    [Fact]
    public void SystemThemeChange_WhenFixedTheme_DoesNotReapply()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Dark);
        var watcher = new FakeWatcher();
        var svc = new ThemeService(ConfigWithTheme("Light"), applier.Object, null, watcher);
        svc.Initialize();   // Current=Light（固定）
        applier.Invocations.Clear();

        watcher.Raise();

        applier.Verify(a => a.Apply(It.IsAny<AppTheme>()), Times.Never);
    }
```

- [ ] **Step 4: Red**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```
Expected: FAILED — ThemeService 4 参 ctor / ISystemThemeWatcher 不存在。

- [ ] **Step 5: 改 ThemeService**

- ctor 加第 4 个可选参数：`ISystemThemeWatcher? watcher = null`，存字段 `private readonly ISystemThemeWatcher? _watcher;`
- ctor 内订阅：`if (_watcher is not null) _watcher.SystemThemeChanged += OnSystemThemeChanged;`
- `Initialize()` 末尾（首次 Apply 之后）加 `_watcher?.Start();`
- 新增私有方法：
```csharp
    private void OnSystemThemeChanged()
    {
        if (_current != AppTheme.System) return;
        _applier.Apply(_applier.DetectSystemTheme());   // 重新下发，不改 Current、不持久化、不触发 ThemeChanged
    }
```
其余逻辑不动。现有 8 个 ThemeService 测试用 2/3 参 ctor，optional 第 4 参不破。

- [ ] **Step 6: Green**

```bash
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```
Expected: 两个 Build succeeded。

- [ ] **Step 7: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Services/Theme/ISystemThemeWatcher.cs \
        wpf/src/Dc.App/Services/Theme/SystemEventsThemeWatcher.cs \
        wpf/src/Dc.App/Services/Theme/ThemeService.cs \
        wpf/tests/Dc.App.Tests/Services/Theme/ThemeServiceTests.cs
git commit -m ":sparkles: S5c.1: 跟随系统主题运行时跟随 OS 切换（ISystemThemeWatcher + 3 tests）"
```

---

## Task 2: 工作台「导入」接通 (TDD)

**Files:**
- Modify: `wpf/src/Dc.App/ViewModels/Workspace/IEmbeddableTagPanel.cs`
- Modify: `wpf/src/Dc.App/ViewModels/TagsViewModel.cs`
- Modify: `wpf/src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs`
- Modify: `wpf/tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs`

- [ ] **Step 1: 接口加 ImportAsync**

`IEmbeddableTagPanel.cs` 加成员：`Task ImportAsync();`

- [ ] **Step 2: TagsViewModel.ImportAsync 改 public**

`TagsViewModel.cs` line ~189：`[RelayCommand] private async Task ImportAsync()` → 改 `private` 为 `public`。`[RelayCommand]` 仍生成 `ImportCommand`，公开方法满足接口。不动方法体。

- [ ] **Step 3: 写工作台测试（Red）**

`TaskWorkspaceViewModelTests.cs`：给 `FakeTagPanel` 加 import 追踪 + 加 1 测试。
- FakeTagPanel 加：`public int ImportCount; public Task ImportAsync() { ImportCount++; return Task.CompletedTask; }`
- 新测试：
```csharp
    [Fact]
    public async Task Import_DelegatesToTagPanel_AndSwitchesToTagsTab()
    {
        var (d, vm) = BuildFull();
        d.Src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];

        await vm.ImportAsync();

        Assert.True(d.Tag.ImportCount >= 1);
        Assert.Equal("tags", vm.SelectedTab);
    }
```
（`BuildFull`/`Deps`/`FakeTagPanel` 来自 S3b.3 的测试结构。）

- [ ] **Step 4: Red**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```
Expected: FAILED — FakeTagPanel 没实现 ImportAsync / vm.ImportAsync 行为不符。

- [ ] **Step 5: 工作台 ImportAsync 接通**

`TaskWorkspaceViewModel.cs`：把 `public Task ImportAsync() => Task.CompletedTask;` 改为：
```csharp
    public async Task ImportAsync()
    {
        await TagsPanel.ImportAsync();
        SelectedTab = "tags";
        await LoadAsync();
    }
```

- [ ] **Step 6: Green**

```bash
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -5
```
Expected: 两个 Build succeeded。

- [ ] **Step 7: Commit**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/ViewModels/Workspace/IEmbeddableTagPanel.cs \
        wpf/src/Dc.App/ViewModels/TagsViewModel.cs \
        wpf/src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs \
        wpf/tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs
git commit -m ":sparkles: S5c.2: 工作台导入接通 → 委托 Tag 面板 Excel 导入 + 切 Tag tab"
```

---

## Task 3: DI 注册 watcher + 全量回归 + push

**Files:**
- Modify: `wpf/src/Dc.App/Composition/ServiceRegistration.cs`

- [ ] **Step 1: 注册 ISystemThemeWatcher**

在 ThemeService 注册附近（`IThemeApplier`/`IThemePreferenceWriter` 那一带）加：
```csharp
        services.AddSingleton<Dc.App.Services.Theme.ISystemThemeWatcher,
                              Dc.App.Services.Theme.SystemEventsThemeWatcher>();
```
ThemeService 自动构造会注入它（已是 optional 第 4 参）。确认 ThemeService 仍是 `AddSingleton<IThemeService, ThemeService>()` 自动构造（是）。

- [ ] **Step 2: 全量回归**

```bash
export PATH=/home/adamyu/.dotnet:$PATH
cd /home/adamyu/workspace/dc/wpf
dotnet test tests/Dc.Infrastructure.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo 2>&1 | tail -4
dotnet test tests/Dc.Integration.Tests   -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo 2>&1 | tail -4
dotnet build src/Dc.App -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -3
dotnet build tests/Dc.App.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows 2>&1 | tail -3
```
Expected: Infra 48 / Integration 10 / 两个 build 0 错误。

- [ ] **Step 3: Commit + push**

```bash
cd /home/adamyu/workspace/dc
git add wpf/src/Dc.App/Composition/ServiceRegistration.cs
git commit -m ":wrench: S5c.3: DI 注册 SystemEventsThemeWatcher"
git push origin wpf-opc-collector
```

- [ ] **Step 4: PR #5 评论**

```bash
CREDS=$(grep "git.adamyu.top" ~/.git-credentials | sed 's|https://||;s|@.*||')
cat > /tmp/s5c_comment.json <<'EOF'
{"body":"### S5c · 功能补缺 完成\n\n补两个非渲染功能欠账：\n- **#1 跟随系统主题运行时跟随**：ISystemThemeWatcher 隔离 SystemEvents.UserPreferenceChanged，OS 亮/暗切换时若当前为「跟随系统」档则重新下发（不改档位、不持久化）。3 unit tests。\n- **#2 工作台导入接通**：原 no-op 按钮 → 委托 Tag 面板 Excel 导入 + 切 Tag tab + 刷新。IEmbeddableTagPanel 加 ImportAsync。\n\n验证：Infra 48 + Integration 10 全绿；build 0 错误。\n\nWindows walkthrough：① 设置选「跟随系统」→ 改 Windows 主题 → App 实时跟随；② 工作台点「导入」→ 弹文件选择 → 选 Excel → 导入后跳 Tag tab 看到新 Tag。"}
EOF
curl -sk -u "$CREDS" -H "Content-Type: application/json" \
  -X POST "https://git.adamyu.top:20443/api/v1/repos/adamyu/dc/issues/5/comments" \
  -d @/tmp/s5c_comment.json -o /tmp/c.json -w "HTTP %{http_code}\n"
jq -r '.html_url // .message' /tmp/c.json
rm -f /tmp/s5c_comment.json /tmp/c.json
```

---

## 验收
- Infra 48 + Integration 10 全绿；Dc.App.Tests +4，累计约 60，build 0 错误
- 跟随系统主题运行时生效（Windows 验证）
- 工作台导入按钮真正触发 Excel 导入（Windows 验证）
- PR #5 评论已加 S5c 段
