# Tag 编辑器缩放/公式 UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 扩展 Tag 编辑器,让用户在 UI 里配置真实 Tag 的缩放(ScaleFactor/Offset)并创建/编辑虚拟测点(公式),通过 `TagEditResult` 把 Tag+Formula+Inputs 交回 `TagsViewModel` 持久化,带引用完整性拦截与级联删除。

**Architecture:** 编辑器加 `IsVirtual` 开关,BoolToVis 互斥显示真实字段(缩放)与虚拟字段(公式名/表达式/输入映射)。表达式优先:用户写表达式→正则提取变量(排除内置函数/常量)→为每个变量选同任务真实 Tag。`ITagEditorDialog.Edit` 返回 `TagEditResult` 代替 `Tag`,`TagsViewModel` 管持久化、引用完整性拦截、虚拟级联删、热同步(真实走现有路径,虚拟不热加→提示重启)。

**Tech Stack:** .NET 8, WPF + WPF-UI, CommunityToolkit.Mvvm (ObservableObject/ObservableProperty/RelayCommand), EF Core (SQLite, snake_case, EnsureCreated+手写 EnsureColumn/EnsureTable/EnsureIndex, 无 EF migrations), DynamicExpresso(已用于引擎), xUnit。

## Global Constraints

- 两个新接口参数 `taskTags`/`existingFormulas` 必须是 **可选且追加在末尾**(`defaultGroup = null, taskLookup = null, taskTags = null, existingFormulas = null`),保证现有位置参数调用 `_editor.Edit(AvailableGroups, null, GroupFilter, func)` 编译通过。返回类型 `Tag?`→`TagEditResult?`。
- `IFormulaValidator.Validate` 实际签名:`bool Validate(string expression, IReadOnlyDictionary<string, int> aliasToDataType, out string? error)`。接口无 `Parse`;校验走这一个方法。
- VM 内内置函数/常量排除集是 `static readonly HashSet<string>`,内容与 Infrastructure `FormulaBuiltins` 完全一致但独立(Infrastructure `internal static`,App 引不到):函数 `SQRT/ABS/SIN/COS/TAN/ASIN/ACOS/ATAN/EXP/LOG/LOG10/FLOOR/CEILING/POW/MIN/MAX/ROUND/IF/AVG/SUM`,常量 `PI/E`。语义正确性最终由 `IFormulaValidator.Validate`(内部用真实 `FormulaBuiltins`)兜底。
- 数据类型码沿用 `OpcDataTypeOption.All`:0=默认,11=Boolean,16=Int8,17=UInt8,2=Int16,18=UInt16,3=Int32,19=UInt32,20=Int64,21=UInt64,4=Float32,5=Float64,8=String,7=DateTime。validator 的 `NumericTypeCodes`={0,11,2,3,4,5,16,17,18,19,20,21},String(8)/DateTime(7)不可数值化。
- ID 生成统一用 `UlidGenerator.NewId()`(`src/Dc.Infrastructure/Persistence/UlidGenerator.cs`)。
- `MessageDialog`:静态 `Show(string title, string message, MessageDialogKind kind)`、`Show(Window? owner, ...)`,`Confirm(string, string, MessageDialogKind)` 返回 bool。`MessageDialogKind { Info, Warning, Error, Success }`。
- DI 风格:`services.AddSingleton<IInterface, Impl>()`(见 `ServiceRegistration.cs`)。
- 仅真实模式显示缩放;虚拟模式不缩放(Q7)。公式变更不热同步,提示"重启任务后生效"(Q8)。
- 不引入新 NuGet 包。

---

## File Structure

**新建:**
- `src/Dc.App/Services/TagEditResult.cs` — record,承载 Tag+Formula?+Inputs。
- `src/Dc.App/ViewModels/TagEditorViewModel.cs` 中新增嵌套类 `InputBindingRow`(同文件,紧随 `GroupRow` 之后)。不单独建文件。

**修改:**
- `src/Dc.App/Services/ITagEditorDialog.cs` — `Edit` 返回 `TagEditResult?`,加两可选末尾参。
- `src/Dc.App/Services/TagEditorDialog.cs` — 注入 `IFormulaValidator`,传新参,返回 `vm.ToResult()`。
- `src/Dc.App/ViewModels/TagEditorViewModel.cs` — 加 `IsVirtual`/缩放/公式字段/`InputBindings`/`AvailableInputTags`/`ExtractAliases`/`ToResult()`/扩展 `Validate()`;构造加 `taskTags`/`existingFormulas`/`validator`。
- `src/Dc.App/Views/TagEditorWindow.xaml` — 卡片宽 460→520,加开关 + 真实/虚拟两套互斥面板。
- `src/Dc.App/ViewModels/TagsViewModel.cs` — `NewAsync`/`EditAsync`/`DeleteAsync` 改用 `TagEditResult`,加引用完整性拦截 + 虚拟级联删 + 虚拟新建提示重启;加载任务 Tag 传入编辑器。
- `src/Dc.App/Composition/ServiceRegistration.cs` — 注册 `IFormulaValidator`。
- `tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs` — 扩展缩放/公式/提取测试。
- `tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs` — 修 stale `FakeGroupPanel`(补 `NavigateToTasksRequested`)。

---

## Task 1: 修 stale FakeGroupPanel + 注册 IFormulaValidator + TagEditResult 骨架(解除 App.Tests 编译阻断)

**Files:**
- Modify: `tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs:56-67`
- Modify: `src/Dc.App/Composition/ServiceRegistration.cs`(+1 行,约 line 145 后)
- Create: `src/Dc.App/Services/TagEditResult.cs`

**Interfaces:**
- Produces: `TagEditResult` record(后续 task 的编辑器返回类型)。

- [ ] **Step 1: 修 FakeGroupPanel 缺失成员**

在 `tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs` 的 `FakeGroupPanel` 类内(line 66 `public void SimulateSelect(Group g) => SelectedGroup = g;` 之后)补事件:

```csharp
        public event Action? NavigateToTasksRequested;
```

- [ ] **Step 2: 创建 TagEditResult**

`src/Dc.App/Services/TagEditResult.cs`:

```csharp
using Dc.Domain.Entities;

namespace Dc.App.Services;

/// <summary>
/// Tag 编辑器返回结果:真实 Tag 只带 Tag(Formula=null);虚拟 Tag 带 Tag+Formula+Inputs。
/// 持久化由调用方(TagsViewModel)负责,编辑器只出数据。
/// </summary>
public sealed record TagEditResult(
    Tag Tag,
    Formula? Formula,
    IReadOnlyList<FormulaInput> Inputs);
```

- [ ] **Step 3: 注册 IFormulaValidator**

在 `src/Dc.App/Composition/ServiceRegistration.cs` 的 `services.AddSingleton<IBrowseDialog, WpfBrowseDialog>();`(line 145)后加:

```csharp
        services.AddSingleton<IFormulaValidator, FormulaValidator>();
```

(`IFormulaValidator`/`FormulaValidator` 在 `Dc.Infrastructure.Orchestration` 命名空间,该文件已 `using Dc.Infrastructure.Orchestration;`。)

- [ ] **Step 4: 验证 App.Tests 编译通过**

Run: `dotnet build tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64`
Expected: BUILD SUCCEEDED(此前缺 `NavigateToTasksRequested` 的编译错消失)。

- [ ] **Step 5: 提交**

```bash
git add tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs src/Dc.App/Services/TagEditResult.cs src/Dc.App/Composition/ServiceRegistration.cs
git commit -m "feat(app): TagEditResult + 注册 IFormulaValidator + 修 stale FakeGroupPanel"
```

---

## Task 2: ITagEditorDialog 契约改为返回 TagEditResult

**Files:**
- Modify: `src/Dc.App/Services/ITagEditorDialog.cs`
- Modify: `src/Dc.App/Services/TagEditorDialog.cs`
- Modify: `src/Dc.App/ViewModels/TagsViewModel.cs:142`(NewAsync 调用点)、`TagsViewModel.cs:164`(EditAsync 调用点)

**Interfaces:**
- Consumes: `TagEditResult`(Task 1)。
- Produces: `ITagEditorDialog.Edit` 新签名;`TagEditorViewModel.ToResult()`(本 task 暂返回真实-only 占位,Task 4 补全)。

> 说明:本 task 只把契约与调用方接通到"真实 Tag 仍走通"的程度,虚拟分支由 Task 4 补。`TagEditorViewModel` 构造与 `ToResult()` 的虚拟逻辑在 Task 4 实现;这里先加最小 `ToResult()` 让编译通过且真实路径行为不变。

- [ ] **Step 1: 写失败测试 — 真实 Tag 经新契约往返**

在 `tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs` 末尾加(VM 此时还没有 `ToResult`,编译失败即测试失败):

```csharp
    [Fact]
    public void ToResult_RealTag_NoFormula()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g);
        vm.Item = "ns=3;i=1002";
        var result = vm.ToResult();

        Assert.NotNull(result);
        Assert.Equal("ns=3;i=1002", result.Tag.Item);
        Assert.Equal("g1", result.Tag.GroupId);
        Assert.Null(result.Formula);
        Assert.Empty(result.Inputs);
    }
```

- [ ] **Step 2: 运行确认失败(编译错:无 ToResult)**

Run: `dotnet test tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~ToResult_RealTag_NoFormula`
Expected: 编译失败(`TagEditorViewModel` 未定义 `ToResult`)。

- [ ] **Step 3: 改 ITagEditorDialog 签名**

`src/Dc.App/Services/ITagEditorDialog.cs` 整体替换为:

```csharp
using Dc.Domain.Entities;

namespace Dc.App.Services;

public interface ITagEditorDialog
{
    TagEditResult? Edit(
        IEnumerable<Group> availableGroups,
        Tag? existing,
        Group? defaultGroup = null,
        Func<string, CollectorTask?>? taskLookup = null,
        IReadOnlyCollection<Tag>? taskTags = null,
        IReadOnlyCollection<Formula>? existingFormulas = null);
}
```

- [ ] **Step 4: 在 TagEditorViewModel 加最小 ToResult()**

在 `src/Dc.App/ViewModels/TagEditorViewModel.cs` 的 `ToEntity()`(line 94)之后加(暂不删 `ToEntity`,Task 4 处理;真实路径用 ToResult):

```csharp
    // 真实 Tag 结果(本 task 占位);虚拟分支在 Task 4 补全。
    public TagEditResult ToResult() => new(ToEntity(), null, Array.Empty<FormulaInput>());
```

(文件顶部需 `using Dc.App.Services;` — 已有 `using Dc.App.Services;`(line 4)。`FormulaInput` 在 `Dc.Domain.Entities`(已 using)。)

- [ ] **Step 5: 改 TagEditorDialog.Edit 返回 TagEditResult**

`src/Dc.App/Services/TagEditorDialog.cs` 整体替换为:

```csharp
using System.Windows;
using Dc.App.ViewModels;
using Dc.App.Views;
using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.Services;

public sealed class TagEditorDialog : ITagEditorDialog
{
    private readonly IBrowseDialog _browseDialog;
    private readonly IFormulaValidator _formulaValidator;

    public TagEditorDialog(IBrowseDialog browseDialog, IFormulaValidator formulaValidator)
    {
        _browseDialog = browseDialog;
        _formulaValidator = formulaValidator;
    }

    public TagEditResult? Edit(
        IEnumerable<Group> availableGroups,
        Tag? existing,
        Group? defaultGroup = null,
        Func<string, CollectorTask?>? taskLookup = null,
        IReadOnlyCollection<Tag>? taskTags = null,
        IReadOnlyCollection<Formula>? existingFormulas = null)
    {
        var vm = new TagEditorViewModel(
            availableGroups, existing, defaultGroup, _browseDialog, taskLookup,
            taskTags, existingFormulas, _formulaValidator);
        var window = new TagEditorWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? vm.ToResult() : null;
    }
}
```

- [ ] **Step 6: 给 TagEditorViewModel 构造加可选尾参(暂忽略)**

把 `src/Dc.App/ViewModels/TagEditorViewModel.cs` 构造签名(line 29-34)改为(加两个可选尾参 + validator,本 task 不用,Task 4 用):

```csharp
    public TagEditorViewModel(
        IEnumerable<Group> groups,
        Tag? existing,
        Group? defaultGroup = null,
        IBrowseDialog? browseDialog = null,
        Func<string, CollectorTask?>? taskLookup = null,
        IReadOnlyCollection<Tag>? taskTags = null,
        IReadOnlyCollection<Formula>? existingFormulas = null,
        IFormulaValidator? formulaValidator = null)
```

构造体开头(line 35 `_browseDialog = browseDialog;` 前)加字段赋值:

```csharp
        _taskTags = taskTags;
        _existingFormulas = existingFormulas;
        _formulaValidator = formulaValidator;
```

并在字段区(line 11-12 附近)加:

```csharp
    private readonly IReadOnlyCollection<Tag>? _taskTags;
    private readonly IReadOnlyCollection<Formula>? _existingFormulas;
    private readonly IFormulaValidator? _formulaValidator;
```

文件顶部加 `using Dc.Infrastructure.Orchestration;`。

- [ ] **Step 7: 改 TagsViewModel 两处调用点接 TagEditResult**

`src/Dc.App/ViewModels/TagsViewModel.cs` `NewAsync`(line 142)与 `EditAsync`(line 164)目前拿到的是 `Tag?`。本 task 先接通类型,虚拟逻辑留到 Task 7。替换 `NewAsync` 开头(line 139-143):

```csharp
    [RelayCommand(CanExecute = nameof(CanNew))]
    private async Task NewAsync()
    {
        if (AvailableGroups.Count == 0) return;
        var result = await EditTagAsync(existing: null);
        if (result is null) return;
        await PersistNewAsync(result);
    }
```

`EditAsync`(line 160-193)替换为:

```csharp
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (SelectedTag is null) return;
        var result = await EditTagAsync(existing: SelectedTag.Tag);
        if (result is null) return;
        await PersistEditAsync(SelectedTag.Tag, result);
    }
```

(`EditTagAsync`/`PersistNewAsync`/`PersistEditAsync` 在 Task 7 实现;本 task 先让 NewAsync/EditAsync 引用它们会编译失败 → 由 Task 7 接通。为避免本 task 留下不编译状态,本 task 在 TagsViewModel 末尾加三个私有方法的最小占位实现:)

在 `TagsViewModel.cs` `private bool HasSelection()`(line 333)前加占位:

```csharp
    // 占位:Task 7 替换为真实实现。签名固定,供 NewAsync/EditAsync 调用。
    private async Task<TagEditResult?> EditTagAsync(Tag? existing)
    {
        var taskTags = TaskScope is null ? null : await LoadTaskTagsAsync(TaskScope);
        IReadOnlyCollection<Formula>? existingFormulas = null;
        if (existing is not null && existing.IsVirtual && TaskScope is not null)
            existingFormulas = await LoadTaskFormulasAsync(TaskScope);

        return _editor.Edit(AvailableGroups, existing, GroupFilter,
            taskId => _taskById.TryGetValue(taskId, out var t) ? t : null,
            taskTags, existingFormulas);
    }

    private async Task<IReadOnlyCollection<Tag>> LoadTaskTagsAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Tags.AsNoTracking().Where(t => t.TaskId == taskId).ToListAsync();
    }

    private async Task<IReadOnlyCollection<Formula>> LoadTaskFormulasAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Formulas.AsNoTracking()
            .Include(f => f.Inputs)
            .Where(f => f.TaskId == taskId)
            .ToListAsync();
    }

    private async Task PersistNewAsync(TagEditResult result) => throw new NotImplementedException();
    private async Task PersistEditAsync(Tag existing, TagEditResult result) => throw new NotImplementedException();
```

> 注:`EditTagAsync`/`LoadTaskTagsAsync`/`LoadTaskFormulasAsync` 是本 task 真实现(供后续复用);`PersistNewAsync`/`PersistEditAsync` 占位 `NotImplementedException`,Task 7 替换。这意味着本 task 提交后 App 能编译但"新建/编辑保存"会抛异常 —— 但真实路径在 Task 7 完成前不会被测试触发(本 plan 的测试只覆盖 VM 单测 + Task 7 完成后真机)。若需保持可运行,可接受。

- [ ] **Step 8: 运行测试确认真实路径契约测试通过**

Run: `dotnet test tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~TagEditorViewModelTests`
Expected: 全部 PASS(含新 `ToResult_RealTag_NoFormula` + 原有 4 个)。

- [ ] **Step 9: 验证整体编译**

Run: `dotnet build src/Dc.App/Dc.App.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64`
Expected: BUILD SUCCEEDED。

- [ ] **Step 10: 提交**

```bash
git add src/Dc.App/Services/ITagEditorDialog.cs src/Dc.App/Services/TagEditorDialog.cs src/Dc.App/ViewModels/TagEditorViewModel.cs src/Dc.App/ViewModels/TagsViewModel.cs tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs
git commit -m "feat(app): ITagEditorDialog 改返回 TagEditResult + 接通真实路径"
```

---

## Task 3: 表达式变量提取 ExtractAliases(纯函数,先测)

**Files:**
- Modify: `src/Dc.App/ViewModels/TagEditorViewModel.cs`
- Test: `tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs`

**Interfaces:**
- Produces: `TagEditorViewModel.ExtractAliases(string) -> IReadOnlyList<string>`(static)、`static readonly HashSet<string> BuiltinNames`。

- [ ] **Step 1: 写失败测试 — 提取、去重保序、排除内置**

在 `TagEditorViewModelTests.cs` 末尾加:

```csharp
    [Theory]
    [InlineData("T * 1.8 + 32", new[] { "T" })]
    [InlineData("T * 1.8 + P / (T + 273.15)", new[] { "T", "P" })]          // 去重保序
    [InlineData("SQRT(T) + SIN(P) + PI + E", new[] { "T", "P" })]           // 排除函数+常量
    [InlineData("AVG(A, B, C) + SUM(X, Y)", new[] { "A", "B", "C", "X", "Y" })]
    [InlineData("123 + 4.5", new string[0])]                                 // 纯数字无变量
    public void ExtractAliases_ReturnsDedupedOrdered_ExcludingBuiltins(string expr, string[] expected)
    {
        Assert.Equal(expected, TagEditorViewModel.ExtractAliases(expr));
    }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~ExtractAliases`
Expected: FAIL(`ExtractAliases` 未定义)。

- [ ] **Step 3: 实现 ExtractAliases + BuiltinNames**

在 `TagEditorViewModel` 类内(`Validate()` 之前)加:

```csharp
    // 与 Infrastructure FormulaBuiltins 内容一致(那里 internal static,App 引不到)。
    // 语义正确性最终由 IFormulaValidator.Validate(内部用真实 FormulaBuiltins)兜底。
    private static readonly HashSet<string> BuiltinNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SQRT","ABS","SIN","COS","TAN","ASIN","ACOS","ATAN","EXP","LOG","LOG10",
        "FLOOR","CEILING","POW","MIN","MAX","ROUND","IF","AVG","SUM","PI","E"
    };

    /// <summary>
    /// 扫描表达式标识符,排除内置函数/常量,去重保序(首次出现顺序)。
    /// 仅用于生成输入映射行 UI;最终校验由 IFormulaValidator.Validate 兜底。
    /// </summary>
    public static IReadOnlyList<string> ExtractAliases(string expression)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (Match m in Regex.Matches(expression ?? string.Empty, @"[A-Za-z_][A-Za-z0-9_]*"))
        {
            var name = m.Value;
            if (BuiltinNames.Contains(name)) continue;
            if (seen.Add(name)) ordered.Add(name);
        }
        return ordered;
    }
```

文件顶部加 `using System.Text.RegularExpressions;`。

- [ ] **Step 4: 运行测试通过**

Run: `dotnet test tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~ExtractAliases`
Expected: 5/5 PASS。

- [ ] **Step 5: 提交**

```bash
git add src/Dc.App/ViewModels/TagEditorViewModel.cs tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs
git commit -m "feat(app): 表达式变量提取 ExtractAliases(去重保序/排除内置)"
```

---

## Task 4: 虚拟模式 VM 状态 + InputBindings 重算 + ToResult + Validate 扩展

**Files:**
- Modify: `src/Dc.App/ViewModels/TagEditorViewModel.cs`
- Test: `tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs`

**Interfaces:**
- Consumes: `IFormulaValidator.Validate(expr, aliasToDataType, out error)`;`TagEditResult`(Task 1);`ExtractAliases`(Task 3);`Formula`/`FormulaInput` 实体。
- Produces: 完整 `TagEditorViewModel` 虚拟模式能力(供 Task 5 XAML 绑定、Task 7 持久化)。

> 决策(spec 已定,本 task 落地):
> - `AvailableInputTags` 不在构造时静态算,而是暴露一个方法 `RefreshInputTagsFor(Group?)`,在分组选定/切换时由 TagsViewModel... 不,编辑器内分组一旦选定 TaskId 即定;VM 自己在 `OnSelectedGroupRowChanged` 里按 `Group.TaskId` 过滤 `_taskTags`。独立页(无 taskTags 传入)时 `AvailableInputTags` 为空 → 虚拟模式不可用(无法选输入)。
> - `IsVirtual` 切换、`Expression` 变化触发 `RebuildInputBindings()`:保留仍存在别名的已选 Tag,移除消失别名,追加新别名(SelectedTag=null)。

- [ ] **Step 1: 写失败测试 — 虚拟模式提取输入 + ToResult**

在 `TagEditorViewModelTests.cs` 末尾加辅助 + 测试:

```csharp
    private static Tag RealTag(string id, string item, string taskId = "t1", int dataType = 5)
        => new() { Id = id, Item = item, DataType = dataType, TaskId = taskId, IsVirtual = false };

    [Fact]
    public void Virtual_ExpressionExtractsInputs_AndToResultBuildsFormula()
    {
        var g = Grp("g1", "温度组");
        var realT = RealTag("rt1", "Random");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { realT });

        vm.IsVirtual = true;
        vm.FormulaName = "Sum";
        vm.Expression = "T * 2";
        // 提取出 T 行
        Assert.Single(vm.InputBindings);
        Assert.Equal("T", vm.InputBindings[0].Alias);
        Assert.Null(vm.InputBindings[0].SelectedTag);

        // 选 T
        vm.InputBindings[0].SelectedTag = realT;
        var result = vm.ToResult();

        Assert.True(result.Tag.IsVirtual);
        Assert.Equal("Sum", result.Tag.Item);
        Assert.NotNull(result.Formula);
        Assert.Equal("T * 2", result.Formula!.Expression);
        Assert.Equal("Sum", result.Formula.Name);
        Assert.Equal(result.Tag.Id, result.Formula.OutputTagId);
        Assert.Equal("t1", result.Formula.TaskId);
        Assert.Single(result.Inputs);
        Assert.Equal("T", result.Inputs[0].Alias);
        Assert.Equal("rt1", result.Inputs[0].SourceTagId);
    }

    [Fact]
    public void Virtual_ExpressionChange_PreservesSelectedKeepsNewNull()
    {
        var g = Grp("g1", "温度组");
        var realT = RealTag("rt1", "Random");
        var realP = RealTag("rt2", "Counter");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { realT, realP });

        vm.IsVirtual = true;
        vm.Expression = "T";
        vm.InputBindings[0].SelectedTag = realT;

        vm.Expression = "T + P";
        Assert.Equal(2, vm.InputBindings.Count);
        Assert.Equal("T", vm.InputBindings[0].Alias);
        Assert.Same(realT, vm.InputBindings[0].SelectedTag);   // 保留已选
        Assert.Equal("P", vm.InputBindings[1].Alias);
        Assert.Null(vm.InputBindings[1].SelectedTag);           // 新增空
    }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~Virtual_`
Expected: FAIL(无 `IsVirtual`/`FormulaName`/`Expression`/`InputBindings`)。

- [ ] **Step 3: 加嵌套类 InputBindingRow**

在 `src/Dc.App/ViewModels/TagEditorViewModel.cs` 文件末尾(`GroupRow` 类之后)加:

```csharp
/// <summary>
/// 输入映射行:从表达式提取的别名(只读)+ 用户选的同任务真实 Tag。
/// </summary>
public sealed class InputBindingRow : ObservableObject
{
    public string Alias { get; }
    [ObservableProperty] private Tag? _selectedTag;

    public InputBindingRow(string alias) => Alias = alias;
}
```

- [ ] **Step 4: 加 VM 虚拟模式字段/属性 + 构造回填**

在 `TagEditorViewModel` 字段区(现有 `[ObservableProperty] private OpcDataTypeOption _dataType ...` 后)加:

```csharp
    [ObservableProperty] private bool _isVirtual;
    [ObservableProperty] private string _scaleFactor = string.Empty;
    [ObservableProperty] private string _offset = string.Empty;
    [ObservableProperty] private string _formulaName = string.Empty;
    [ObservableProperty] private string _expression = string.Empty;
    [ObservableProperty] private string _outputUnit = string.Empty;

    public ObservableCollection<InputBindingRow> InputBindings { get; } = new();
    public ObservableCollection<Tag> AvailableInputTags { get; } = new();
```

构造体内(现有 `_selectedGroupRow = selected;` line 64 之后、`ShowGroupSelector = selected is null;` 之前)加回填 + 初始刷新:

```csharp
        // 编辑已存在虚拟 Tag:回填公式字段并预选输入。
        if (existing is not null && existing.IsVirtual && _existingFormulas is not null)
        {
            var f = _existingFormulas.FirstOrDefault(x => x.OutputTagId == existing.Id);
            if (f is not null)
            {
                _isVirtual = true;
                _formulaName = f.Name;
                _expression = f.Expression;
                _outputUnit = f.OutputUnit ?? string.Empty;
            }
        }
```

构造体最末尾(`ShowGroupSelector = selected is null;` 之后)加:

```csharp
        RefreshAvailableInputTags();
        if (_isVirtual) RebuildInputBindings();
```

- [ ] **Step 5: 实现 partial On* 钩子 + RefreshAvailableInputTags + RebuildInputBindings**

在 `TagEditorViewModel` 内(`Browse()` 命令之后)加:

```csharp
    partial void OnSelectedGroupRowChanged(GroupRow? value)
    {
        // 分组定 → TaskId 定 → 刷新可选输入 Tag(同任务真实,排除自身虚拟)。
        OnPropertyChanged(nameof(Group));
        RefreshAvailableInputTags();
    }

    partial void OnExpressionChanged(string value)
    {
        if (_isVirtual) RebuildInputBindings();
    }

    private void RefreshAvailableInputTags()
    {
        AvailableInputTags.Clear();
        if (_taskTags is null || Group is null) return;
        foreach (var t in _taskTags.Where(t => !t.IsVirtual && t.Id != OriginalId))
            AvailableInputTags.Add(t);
    }

    // 表达式变化:保留仍存在别名的已选 Tag,移除消失别名,追加新别名(null)。
    private void RebuildInputBindings()
    {
        var aliases = ExtractAliases(_expression);
        var prevByAlias = InputBindings.ToDictionary(r => r.Alias, r => r.SelectedTag, StringComparer.OrdinalIgnoreCase);
        InputBindings.Clear();
        foreach (var alias in aliases)
        {
            var row = new InputBindingRow(alias);
            if (prevByAlias.TryGetValue(alias, out var sel)) row.SelectedTag = sel;
            InputBindings.Add(row);
        }
    }
```

> 注:`OnSelectedGroupRowChanged` 原本无 partial 钩子(原 VM 没用)。`Group` 计算属性依赖 `SelectedGroupRow`,分组切换后输入 Tag 范围变,需刷新。`OriginalId` 编辑时为 existing.Id、新建为 null,`t.Id != OriginalId` 排除自身。

- [ ] **Step 6: 实现 ToResult() 完整版(替换 Task 2 占位)**

替换 Task 2 加的占位 `ToResult()` 为:

```csharp
    public TagEditResult ToResult()
    {
        var tag = new Tag
        {
            Id = OriginalId ?? string.Empty,
            Item = _isVirtual ? _formulaName.Trim() : Item.Trim(),
            DataType = DataType.Code,
            GroupId = Group!.Id,
            TaskId = Group!.TaskId,
            IsVirtual = _isVirtual,
            ScaleFactor = _isVirtual ? null : ParseDouble(_scaleFactor),
            Offset = _isVirtual ? null : ParseDouble(_offset)
        };

        if (!_isVirtual)
            return new TagEditResult(tag, null, Array.Empty<FormulaInput>());

        var formula = new Formula
        {
            Id = string.Empty, // 调用方生成
            Name = _formulaName.Trim(),
            Expression = _expression,
            OutputTagId = tag.Id, // 调用方在持久化时回填真实 Id
            OutputUnit = string.IsNullOrWhiteSpace(_outputUnit) ? null : _outputUnit.Trim(),
            TaskId = Group!.TaskId
        };
        var inputs = InputBindings
            .Where(r => r.SelectedTag is not null)
            .Select(r => new FormulaInput
            {
                Id = string.Empty,
                FormulaId = string.Empty, // 调用方回填
                Alias = r.Alias,
                SourceTagId = r.SelectedTag!.Id
            })
            .ToList();
        return new TagEditResult(tag, formula, inputs);
    }

    private static double? ParseDouble(string s)
        => double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
```

文件顶部加 `using System.Globalization;`。

- [ ] **Step 7: 扩展 Validate()**

替换现有 `Validate()`(line 86-92)为:

```csharp
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Group is null) errors.Add("必须选择所属分组");

        if (!_isVirtual)
        {
            if (string.IsNullOrWhiteSpace(Item)) errors.Add("Item 不能为空");
            if (!string.IsNullOrWhiteSpace(_scaleFactor)
                && !double.TryParse(_scaleFactor.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                errors.Add("缩放系数必须是数字");
            if (!string.IsNullOrWhiteSpace(_offset)
                && !double.TryParse(_offset.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                errors.Add("偏移量必须是数字");
            return errors;
        }

        // 虚拟模式
        if (string.IsNullOrWhiteSpace(_formulaName)) errors.Add("公式名不能为空");
        else if (_taskTags is not null)
        {
            // 任务内唯一(排除自身):比对其余虚拟 Tag 的 Item(虚拟 Tag Item=公式名)
            var dup = _taskTags.Any(t => t.Id != OriginalId
                && t.IsVirtual
                && string.Equals(t.Item, _formulaName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (dup) errors.Add("公式名在任务内已存在");
        }
        if (string.IsNullOrWhiteSpace(_expression)) errors.Add("表达式不能为空");

        // 每个提取出的变量必须选了 Tag
        foreach (var row in InputBindings.Where(r => r.SelectedTag is null))
            errors.Add($"变量 {row.Alias} 未选择输入测点");

        // 类型可数值化 + 表达式语法
        var aliasToDataType = InputBindings
            .Where(r => r.SelectedTag is not null)
            .ToDictionary(r => r.Alias, r => r.SelectedTag!.DataType);
        if (_formulaValidator is not null
            && !_formulaValidator.Validate(_expression, aliasToDataType, out var ferr))
            errors.Add(ferr!);

        return errors;
    }
```

- [ ] **Step 8: 运行测试通过**

Run: `dotnet test tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~TagEditorViewModelTests`
Expected: 全部 PASS(含两个新 Virtual_ 测试 + Task 2/3 测试 + 原 4 个)。

- [ ] **Step 9: 加 Validate 校验测试**

在 `TagEditorViewModelTests.cs` 末尾加:

```csharp
    private static IFormulaValidator Validator() => new FormulaValidator();

    [Fact]
    public void Validate_RealTag_BadScaleNumber_HasError()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            formulaValidator: Validator());
        vm.Item = "x";
        vm.ScaleFactor = "abc";
        Assert.Contains(vm.Validate(), e => e.Contains("缩放"));
    }

    [Fact]
    public void Validate_RealTag_EmptyScale_NullScale()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g);
        vm.Item = "x";
        Assert.Null(vm.ToResult().Tag.ScaleFactor);
    }

    [Fact]
    public void Validate_Virtual_MissingName_HasError()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { RealTag("rt1", "Random") },
            formulaValidator: Validator());
        vm.IsVirtual = true;
        vm.Expression = "T";
        Assert.Contains(vm.Validate(), e => e.Contains("公式名"));
    }

    [Fact]
    public void Validate_Virtual_DuplicateName_HasError()
    {
        var g = Grp("g1", "温度组");
        var existingVirtual = new Tag { Id = "rv1", Item = "Sum", IsVirtual = true, TaskId = "t1" };
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { existingVirtual, RealTag("rt1", "Random") },
            formulaValidator: Validator());
        vm.IsVirtual = true;
        vm.FormulaName = "Sum";   // 与已有虚拟同名
        vm.Expression = "T";
        vm.InputBindings[0].SelectedTag = RealTag("rt1", "Random");
        Assert.Contains(vm.Validate(), e => e.Contains("已存在"));
    }

    [Fact]
    public void Validate_Virtual_UnselectedInput_HasError()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { RealTag("rt1", "Random") },
            formulaValidator: Validator());
        vm.IsVirtual = true;
        vm.FormulaName = "Doubled";
        vm.Expression = "T";   // T 行未选
        Assert.Contains(vm.Validate(), e => e.Contains("未选择输入"));
    }

    [Fact]
    public void Validate_Virtual_StringInputTag_HasError()
    {
        var g = Grp("g1", "温度组");
        var strTag = RealTag("rs1", "Name", dataType: 8); // String
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { strTag },
            formulaValidator: Validator());
        vm.IsVirtual = true;
        vm.FormulaName = "Doubled";
        vm.Expression = "T";
        vm.InputBindings[0].SelectedTag = strTag;
        Assert.Contains(vm.Validate(), e => e.Contains("数值化"));
    }

    [Fact]
    public void Validate_Virtual_Valid_NoErrors()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { RealTag("rt1", "Random") },
            formulaValidator: Validator());
        vm.IsVirtual = true;
        vm.FormulaName = "Doubled";
        vm.Expression = "T * 2";
        vm.InputBindings[0].SelectedTag = RealTag("rt1", "Random");
        Assert.Empty(vm.Validate());
    }
```

(`FormulaValidator` 在 `Dc.Infrastructure.Orchestration`;测试文件顶部需 `using Dc.Infrastructure.Orchestration;`。)

- [ ] **Step 10: 运行全部 VM 测试通过**

Run: `dotnet test tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~TagEditorViewModelTests`
Expected: 全部 PASS。

- [ ] **Step 11: 提交**

```bash
git add src/Dc.App/ViewModels/TagEditorViewModel.cs tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs
git commit -m "feat(app): 虚拟模式 VM 状态/输入映射重算/ToResult/Validate"
```

---

## Task 5: TagEditorWindow XAML — 开关 + 真实/虚拟互斥面板

**Files:**
- Modify: `src/Dc.App/Views/TagEditorWindow.xaml`
- Modify: `src/Dc.App/Views/TagEditorWindow.xaml.cs`(无逻辑变化,确认 OnSaveClick 仍兼容;若分组选择器移位需确认绑定名不变)

**Interfaces:**
- Consumes: `IsVirtual`/`ScaleFactor`/`Offset`/`FormulaName`/`Expression`/`OutputUnit`/`InputBindings`/`AvailableInputTags`/`AvailableGroups`/`SelectedGroupRow`(Task 4)。

> 现有 XAML 结构(见 spec §4):顶部标题栏 / 底部按钮栏 / 中间 `<StackPanel Margin="20,18">` 含一个 3 行 2 列 Grid(Item/数据类型/分组)。本 task 重构中间区:开关在最上,真实面板(IsVirtual==false)与虚拟面板(IsVirtual==true)互斥,分组选择器在两种模式下共享(最下)。卡片宽 460→520。

- [ ] **Step 1: 改 XAML 中间区 + 卡片宽**

把 `src/Dc.App/Views/TagEditorWindow.xaml` 的 `Width="460"`(line 13)改为 `Width="520"`。

把整个中间 `<StackPanel Margin="20,18"> ... </StackPanel>`(line 37-76)替换为:

```xml
            <StackPanel Margin="20,18">
                <!-- 虚拟开关(所有模式可见) -->
                <CheckBox Content="虚拟测点(公式计算)" Margin="0,0,0,12"
                          IsChecked="{Binding IsVirtual}" />

                <!-- 真实面板:缩放字段 -->
                <StackPanel Visibility="{Binding IsVirtual, Converter={StaticResource BoolToVisInverse}}">
                    <Grid Margin="0,0,0,8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="96" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Text="Item:"
                                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                        <DockPanel Grid.Column="1">
                            <Button DockPanel.Dock="Right" Content="浏览…" Width="64" Margin="6,0,0,0"
                                    Command="{Binding BrowseCommand}" IsEnabled="{Binding CanBrowse}" />
                            <TextBox controls:Placeholder.Text="opc 节点 ItemId"
                                     Text="{Binding Item, UpdateSourceTrigger=PropertyChanged}" />
                        </DockPanel>
                    </Grid>
                    <Grid Margin="0,0,0,8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="96" />
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="96" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Text="缩放系数:"
                                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                        <TextBox Grid.Column="1" controls:Placeholder.Text="留空=不缩放"
                                 Text="{Binding ScaleFactor, UpdateSourceTrigger=PropertyChanged}" />
                        <TextBlock Grid.Column="2" Text="偏移量:" Margin="12,0,0,0"
                                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                        <TextBox Grid.Column="3" controls:Placeholder.Text="留空=不缩放"
                                 Text="{Binding Offset, UpdateSourceTrigger=PropertyChanged}" />
                    </Grid>
                    <Grid Margin="0,0,0,4">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="96" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Text="数据类型:"
                                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                        <ComboBox Grid.Column="1"
                                  ItemsSource="{Binding DataTypeOptions}" SelectedItem="{Binding DataType}" />
                    </Grid>
                </StackPanel>

                <!-- 虚拟面板:公式字段 + 输入映射 -->
                <StackPanel Visibility="{Binding IsVirtual, Converter={StaticResource BoolToVis}}">
                    <Grid Margin="0,0,0,8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="96" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Text="名称:"
                                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                        <TextBox Grid.Column="1" controls:Placeholder.Text="任务内唯一,作为虚拟测点标识"
                                 Text="{Binding FormulaName, UpdateSourceTrigger=PropertyChanged}" />
                    </Grid>
                    <Grid Margin="0,0,0,8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="96" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Text="表达式:"
                                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Top" Margin="0,6,0,0" />
                        <TextBox Grid.Column="1" MinHeight="56" AcceptsReturn="True" TextWrapping="Wrap"
                                 VerticalScrollBarVisibility="Auto"
                                 controls:Placeholder.Text="如 T * 1.8 + 32"
                                 Text="{Binding Expression, UpdateSourceTrigger=PropertyChanged}" />
                    </Grid>
                    <Grid Margin="0,0,0,8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="96" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Text="输出单位:"
                                   Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                        <TextBox Grid.Column="1" controls:Placeholder.Text="可选"
                                 Text="{Binding OutputUnit, UpdateSourceTrigger=PropertyChanged}" />
                    </Grid>
                    <TextBlock Text="输入映射:" Margin="0,4,0,4"
                               Foreground="{DynamicResource TextFillColorSecondaryBrush}" />
                    <ItemsControl ItemsSource="{Binding InputBindings}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <Grid Margin="0,0,0,6">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="60" />
                                        <ColumnDefinition Width="*" />
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Grid.Column="0" Text="{Binding Alias}" VerticalAlignment="Center"
                                               Foreground="{DynamicResource TextFillColorSecondaryBrush}" />
                                    <ComboBox Grid.Column="1" Margin="6,0,0,0"
                                              ItemsSource="{Binding DataContext.AvailableInputTags, RelativeSource={RelativeSource AncestorType=views:TagEditorWindow}}"
                                              SelectedItem="{Binding SelectedTag}">
                                        <ComboBox.ItemTemplate>
                                            <DataTemplate>
                                                <TextBlock Text="{Binding Item}" />
                                            </DataTemplate>
                                        </ComboBox.ItemTemplate>
                                    </ComboBox>
                                </Grid>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </StackPanel>

                <!-- 分组选择器(两种模式共享) -->
                <Grid Margin="0,8,0,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="96" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="所属分组:"
                               Visibility="{Binding ShowGroupSelector, Converter={StaticResource BoolToVis}}"
                               Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                    <ComboBox Grid.Column="1"
                              Visibility="{Binding ShowGroupSelector, Converter={StaticResource BoolToVis}}"
                              ItemsSource="{Binding AvailableGroups}" SelectedItem="{Binding SelectedGroupRow}">
                        <ComboBox.ItemTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Display}" />
                            </DataTemplate>
                        </ComboBox.ItemTemplate>
                    </ComboBox>
                </Grid>
            </StackPanel>
```

- [ ] **Step 2: 新增反相可见性转换器 BoolToVisInverse**

现有 `BoolToVis` 是内置 `BooleanToVisibilityConverter`(App.xaml:21),无反相能力。真实面板需 `IsVirtual==false` 时可见 → 新建反相转换器 `src/Dc.App/Views/Converters/InverseBooleanToVisibilityConverter.cs`(沿用 `CountToVisibilityConverter` 风格):

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dc.App.Views.Converters;

/// <summary>
/// 反相布尔→可见性:false→Visible,true→Collapsed。用于"IsVirtual==false 时显示真实面板"。
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

在 `src/Dc.App/App.xaml` 的 `<BooleanToVisibilityConverter x:Key="BoolToVis" />`(line 21)后加:

```xml
            <conv:InverseBooleanToVisibilityConverter x:Key="BoolToVisInverse" />
```

(`conv:` 命名空间已声明于 App.xaml:11。)

- [ ] **Step 3: 验证编译**

Run: `dotnet build src/Dc.App/Dc.App.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64`
Expected: BUILD SUCCEEDED。

- [ ] **Step 4: 提交**

```bash
git add src/Dc.App/Views/TagEditorWindow.xaml src/Dc.App/Converters/ src/Dc.App/App.xaml
git commit -m "feat(app): Tag 编辑器开关+真实/虚拟互斥面板+缩放/公式字段"
```

(只 add 实际改动文件;若未改 Converters/App.xaml 则去掉。)

---

## Task 6: TagsViewModel 持久化 — NewAsync/EditAsync/DeleteAsync + 引用完整性 + 级联删 + 重启提示

**Files:**
- Modify: `src/Dc.App/ViewModels/TagsViewModel.cs`(替换 Task 2 占位的 `PersistNewAsync`/`PersistEditAsync`;改 `DeleteAsync`)

**Interfaces:**
- Consumes: `TagEditResult`(Task 1)、`IFormulaValidator`(已注册)、`UlidGenerator.NewId()`、现有 `TryHotAddAsync`/`TryHotRemoveAsync`/`ToRow`/`IsTaskRunning`/`MessageDialog`。`DcDbContext.Formulas`/`FormulaInputs` DbSet(引擎已建)。
- Produces: 完整 New/Edit/Delete 持久化与引用完整性。

> 关键 spec 决策:删被引用真实 Tag→拦截;删虚拟 Tag→级联删公式;虚拟新建/公式变更不热同步,任务运行中提示重启。

- [ ] **Step 1: 实现 PersistNewAsync(替换占位)**

替换 `TagsViewModel.cs` 中 `private async Task PersistNewAsync(TagEditResult result) => throw new NotImplementedException();` 为:

```csharp
    private async Task PersistNewAsync(TagEditResult result)
    {
        var tag = result.Tag;
        tag.Id = UlidGenerator.NewId();
        Formula? formula = null;
        if (result.Formula is not null)
        {
            formula = result.Formula;
            formula.Id = UlidGenerator.NewId();
            formula.OutputTagId = tag.Id;
            foreach (var inp in result.Inputs)
            {
                inp.Id = UlidGenerator.NewId();
                inp.FormulaId = formula.Id;
                formula.Inputs.Add(inp);
            }
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        try
        {
            db.Tags.Add(tag);
            if (formula is not null) db.Formulas.Add(formula); // EF 级联加 Inputs
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            MessageDialog.Show("错误", $"保存失败:{ex.InnerException?.Message ?? ex.Message}", MessageDialogKind.Error);
            return;
        }

        Tags.Add(ToRow(tag));

        // 热同步:真实 Tag 走现有路径;虚拟 Tag 不订阅(Q8),运行中提示重启。
        if (tag.IsVirtual)
        {
            if (IsTaskRunning(tag.TaskId))
                MessageDialog.Show("提示", "虚拟测点已保存,重启任务后生效。", MessageDialogKind.Info);
        }
        else
        {
            await TryHotAddAsync(tag);
        }
    }
```

- [ ] **Step 2: 实现 PersistEditAsync(替换占位)**

替换 `private async Task PersistEditAsync(Tag existing, TagEditResult result) => throw new NotImplementedException();` 为:

```csharp
    private async Task PersistEditAsync(Tag existing, TagEditResult result)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Tags.FirstOrDefaultAsync(t => t.Id == existing.Id);
        if (entity is null) return;

        var oldItem = entity.Item;
        var oldTaskId = entity.TaskId;
        var wasVirtual = entity.IsVirtual;

        entity.Item = result.Tag.Item;
        entity.DataType = result.Tag.DataType;
        entity.GroupId = result.Tag.GroupId;
        entity.TaskId = result.Tag.TaskId;
        entity.IsVirtual = result.Tag.IsVirtual;
        entity.ScaleFactor = result.Tag.ScaleFactor;
        entity.Offset = result.Tag.Offset;

        // 公式变更:删旧(若曾虚拟),加新(若现虚拟)。
        if (wasVirtual)
        {
            var oldFormulas = await db.Formulas.Include(f => f.Inputs)
                .Where(f => f.OutputTagId == entity.Id).ToListAsync();
            if (oldFormulas.Count > 0) db.Formulas.RemoveRange(oldFormulas); // 级联删 Inputs
        }
        if (result.Formula is not null)
        {
            var f = result.Formula;
            f.Id = UlidGenerator.NewId();
            f.OutputTagId = entity.Id;
            foreach (var inp in result.Inputs)
            {
                inp.Id = UlidGenerator.NewId();
                inp.FormulaId = f.Id;
                f.Inputs.Add(inp);
            }
            db.Formulas.Add(f);
        }

        await db.SaveChangesAsync();

        // 热同步:真实 Tag 的 Item/Task 变更走现有路径;虚拟/公式变更不热同步。
        var running = IsTaskRunning(entity.TaskId) || IsTaskRunning(oldTaskId);
        if (!entity.IsVirtual && (oldItem != entity.Item || oldTaskId != entity.TaskId))
        {
            await TryHotRemoveAsync(oldTaskId, oldItem);
            await TryHotAddAsync(entity);
        }
        else if (entity.IsVirtual && running)
        {
            MessageDialog.Show("提示", "虚拟测点/公式已保存,重启任务后生效。", MessageDialogKind.Info);
        }

        var idx = Tags.IndexOf(SelectedTag);
        if (idx >= 0)
        {
            var row = ToRow(entity);
            Tags[idx] = row;
            SelectedTag = row;
        }
    }
```

- [ ] **Step 3: 改 DeleteAsync — 引用完整性拦截 + 虚拟级联删**

替换现有 `DeleteAsync`(line 195-209)为:

```csharp
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var row = SelectedTag;
        if (row is null) return;
        var tag = row.Tag;

        // 引用完整性(Q5):真实 Tag 被公式引用 → 拦截。
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var referencingFormulas = await checkDb.FormulaInputs
            .Where(i => i.SourceTagId == tag.Id)
            .Join(checkDb.Formulas, i => i.FormulaId, f => f.Id, (i, f) => f.Name)
            .Distinct().ToListAsync();
        if (referencingFormulas.Count > 0)
        {
            MessageDialog.Show("无法删除",
                $"该测点被公式 {string.Join(", ", referencingFormulas)} 引用,请先修改公式或删除对应虚拟测点。",
                MessageDialogKind.Warning);
            return;
        }

        var confirm = MessageDialog.Confirm("删除确认", $"确定删除 Tag {tag.Item}？", MessageDialogKind.Warning);
        if (!confirm) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        // 虚拟 Tag:级联删其 Formula+Inputs。
        if (tag.IsVirtual)
        {
            var ownFormulas = await db.Formulas.Include(f => f.Inputs)
                .Where(f => f.OutputTagId == tag.Id).ToListAsync();
            if (ownFormulas.Count > 0) db.Formulas.RemoveRange(ownFormulas);
        }
        await db.Tags.Where(t => t.Id == tag.Id).ExecuteDeleteAsync();
        await db.SaveChangesAsync();

        if (!tag.IsVirtual) await TryHotRemoveAsync(tag.TaskId, tag.Item);
        Tags.Remove(row);
    }
```

- [ ] **Step 4: 验证编译 + 跑全部 App 测试**

Run: `dotnet build src/Dc.App/Dc.App.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 && dotnet test tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64`
Expected: BUILD SUCCEEDED;全部测试 PASS(VM 测试 + Workspace 测试,无 `NotImplementedException` 相关)。

- [ ] **Step 5: 提交**

```bash
git add src/Dc.App/ViewModels/TagsViewModel.cs
git commit -m "feat(app): Tag 新建/编辑/删除持久化+引用完整性拦截+虚拟级联删+重启提示"
```

---

## Task 7: 真机 + UIA 验证

**Files:**
- 无代码改动;验证 `.test/` 脚本与手工 UI。

> 沿用既有 UIA 驱动惯例(`.test/ui_engine_drive.ps1` / `ui_read_cells.ps1`)与 Prosys(`opc.tcp://DESKTOP-KONUSAK:53530/OPCUA/SimulationServer`,Random=`ns=3;i=1002`,Counter=`ns=3;i=1001`)。

- [ ] **Step 1: 构建运行时 App**

Run: `dotnet build src/Dc.App/Dc.App.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64`
Expected: BUILD SUCCEEDED。

- [ ] **Step 2: 确认运行时 appsettings 含 Prosys 开关**

确认 `src/Dc.App/bin/x64/Debug/net8.0-windows/appsettings.json` 的 `OpcUa` 块含 `AutoAcceptUntrustedCertificates:true`、`UseSecurity:false`(dev;见 [[prosys-ua-test-server]])。

- [ ] **Step 3: 启动 App + Prosys,UIA 驱动新建虚拟测点**

手工/UIA:导航「采集任务」→选已有 UA 任务(含真实 Random + Counter Tag)→Tag 页签→新建→勾"虚拟测点(公式计算)"→名称 `Sum`→表达式 `Random + Counter`→输入映射两行分别选 Random/Counter→选分组→保存。

- [ ] **Step 4: 启动任务,实时数据验证 Sum 行**

启动该任务→实时数据→用 `.test/ui_read_cells.ps1` 读 DataGrid 行,确认出现 `Sum` 行且值≈Random+Counter。

- [ ] **Step 5: 验证引用完整性拦截**

在 Tag 页选中被 `Sum` 引用的真实 Random Tag→删除→确认弹出"被公式 Sum 引用,请先修改公式或删除对应虚拟测点",删除被阻止。

- [ ] **Step 6: 验证虚拟级联删 + 重启提示**

删除虚拟 `Sum` Tag→确认删除成功且无残留公式(可选:重开编辑器确认 Sum 不在)。新建/编辑虚拟测点时若任务运行中,确认提示"重启任务后生效"。

- [ ] **Step 7: 记录验证结果到 progress ledger,提交(无代码则跳过提交)**

若验证全通过,在 `.superpowers/sdd/progress.md` 追加一行记录真机验证结果。

---

## Self-Review(写完后自查)

**1. Spec 覆盖:**
- §1 TagEditResult + ITagEditorDialog 签名 → Task 1(TagEditResult)+ Task 2(接口)。✅
- §2 VM 状态/模式切换/构造/ToResult → Task 4。✅
- §3 表达式变量提取 → Task 3。✅
- §4 XAML 布局 → Task 5。✅
- §5 校验 → Task 4 Step 7 + Step 9。✅
- §6 TagsViewModel 创建/编辑/删除 + 引用完整性 + 级联删 → Task 6。✅
- §7 测试(修 stale FakeGroupPanel + VM 单测 + 真机) → Task 1(stale)+ Task 4(VM 单测)+ Task 7(真机)。✅
- §依赖 IFormulaValidator 注册 → Task 1。✅

**2. Placeholder 扫描:** 无 TBD/TODO;Task 5 Step 2 对 `BoolToVisInverse` 的处理是"读现有 converter 再决定"——这是必要的运行时探查,非占位,已给出两条具体路径(加 Inverse 属性 / 新建 converter)。

**3. 类型一致性:** `TagEditResult(Tag, Formula?, IReadOnlyList<FormulaInput>)` 在 Task 1 定义,Task 2/4/6 使用一致;`ToResult()` 返回类型一致;`ExtractAliases` 返回 `IReadOnlyList<string>` 在 Task 3/4 一致;`InputBindingRow.Alias/SelectedTag` 在 Task 4 定义、Task 5 XAML 绑定一致;`IFormulaValidator.Validate(expr, dict, out error)` 签名在 Task 4 Step 7 与实际一致。✅
