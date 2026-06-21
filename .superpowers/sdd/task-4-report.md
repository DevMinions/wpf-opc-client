# Task 4 Report: 虚拟模式 VM 状态 + InputBindings 重算 + ToResult + Validate 扩展

## Files changed

- `E:/CursorProjects/wpf-opc-client/src/Dc.App/ViewModels/TagEditorViewModel.cs`
  - Added virtual/scaling/formula state: `IsVirtual`, `ScaleFactor`, `Offset`, `FormulaName`, `Expression`, `OutputUnit`.
  - Added `InputBindings` and `AvailableInputTags` collections.
  - Added `RefreshAvailableInputTags()` and `RebuildInputBindings()` with expression alias rebuild preserving selections.
  - Added group/expression partial hooks.
  - Replaced minimal `ToResult()` with real/virtual result creation including `Formula` and `FormulaInput` rows.
  - Extended `Validate()` for scale numbers and virtual formula validation.
  - Added `InputBindingRow` in the same file after `GroupRow`.
- `E:/CursorProjects/wpf-opc-client/tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs`
  - Added Step 1 virtual expression/input/result tests.
  - Added Step 9 validation tests.
  - Added `RealTag()` and `Validator()` helpers.

## TDD command log

### Step 2 RED: virtual mode tests fail before implementation

Command:

```bash
dotnet test E:/CursorProjects/wpf-opc-client/tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~Virtual_
```

Output summary:

```text
Exit code 1
... Dc.App -> E:\CursorProjects\wpf-opc-client\src\Dc.App\bin\x64\Debug\net8.0-windows\Dc.App.dll
E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\ViewModels\TagEditorViewModelTests.cs(97,12): error CS1061: “TagEditorViewModel”未包含“IsVirtual”的定义 ...
E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\ViewModels\TagEditorViewModelTests.cs(98,12): error CS1061: “TagEditorViewModel”未包含“FormulaName”的定义 ...
E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\ViewModels\TagEditorViewModelTests.cs(99,12): error CS1061: “TagEditorViewModel”未包含“Expression”的定义 ...
E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\ViewModels\TagEditorViewModelTests.cs(101,26): error CS1061: “TagEditorViewModel”未包含“InputBindings”的定义 ...
...
```

Expected failure observed: missing virtual-mode properties/collections.

### Step 8 GREEN: VM tests pass after virtual ToResult/input implementation

Command:

```bash
dotnet test E:/CursorProjects/wpf-opc-client/tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~TagEditorViewModelTests
```

Output summary:

```text
已通过! - 失败:     0，通过:    12，已跳过:     0，总计:    12，持续时间: 38 ms - Dc.App.Tests.dll (net8.0)
```

### Step 9 RED: validation tests fail before Validate implementation

Command:

```bash
dotnet test E:/CursorProjects/wpf-opc-client/tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~TagEditorViewModelTests
```

Output summary:

```text
Exit code 1
[xUnit.net 00:00:00.16]     Dc.App.Tests.ViewModels.TagEditorViewModelTests.Validate_Virtual_MissingName_HasError [FAIL]
  Assert.Contains() Failure: Filter not matched in collection
  Collection: ["Item 不能为空"]
[xUnit.net 00:00:00.18]     Dc.App.Tests.ViewModels.TagEditorViewModelTests.Validate_Virtual_UnselectedInput_HasError [FAIL]
[xUnit.net 00:00:00.19]     Dc.App.Tests.ViewModels.TagEditorViewModelTests.Validate_Virtual_DuplicateName_HasError [FAIL]
[xUnit.net 00:00:00.19]     Dc.App.Tests.ViewModels.TagEditorViewModelTests.Validate_Virtual_Valid_NoErrors [FAIL]
[xUnit.net 00:00:00.19]     Dc.App.Tests.ViewModels.TagEditorViewModelTests.Validate_RealTag_BadScaleNumber_HasError [FAIL]
[xUnit.net 00:00:00.19]     Dc.App.Tests.ViewModels.TagEditorViewModelTests.Validate_Virtual_StringInputTag_HasError [FAIL]
失败!  - 失败:     6，通过:    13，已跳过:     0，总计:    19，持续时间: 36 ms - Dc.App.Tests.dll (net8.0)
```

Expected failure observed: existing minimal validation only checked real `Item` and group.

### Step 10 GREEN/final test: all TagEditorViewModel tests pass

Command:

```bash
dotnet test E:/CursorProjects/wpf-opc-client/tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~TagEditorViewModelTests
```

Output:

```text
正在确定要还原的项目…
所有项目均是最新的，无法还原。
Dc.Opc.Abstractions -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Abstractions\bin\x64\Debug\net8.0\Dc.Opc.Abstractions.dll
Dc.Domain -> E:\CursorProjects\wpf-opc-client\src\Dc.Domain\bin\x64\Debug\net8.0\Dc.Domain.dll
Technosoftware.OpcRcw -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\OpcRcw\bin\x64\Debug\net8.0-windows\Technosoftware.OpcRcw.dll
Technosoftware.DaAeHdaClient -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\DaAeHdaClient\bin\x64\Debug\net8.0-windows\Technosoftware.DaAeHdaClient.dll
Dc.Opc.Ua -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Ua\bin\x64\Debug\net8.0\Dc.Opc.Ua.dll
Dc.Infrastructure -> E:\CursorProjects\wpf-opc-client\src\Dc.Infrastructure\bin\x64\Debug\net8.0\Dc.Infrastructure.dll
Technosoftware.DaAeHdaClient.Com -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\DaAeHdaClient.Com\bin\x64\Debug\net8.0-windows\Technosoftware.DaAeHdaClient.Com.dll
Dc.Opc.Da -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Da\bin\x64\Debug\net8.0-windows\Dc.Opc.Da.dll
Dc.Opc.Ae -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Ae\bin\x64\Debug\net8.0-windows\Dc.Opc.Ae.dll
Dc.App -> E:\CursorProjects\wpf-opc-client\src\Dc.App\bin\x64\Debug\net8.0-windows\Dc.App.dll
Dc.App.Tests -> E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\bin\x64\Debug\net8.0-windows\Dc.App.Tests.dll
E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\bin\x64\Debug\net8.0-windows\Dc.App.Tests.dll (.NETCoreApp,Version=v8.0)的测试运行
VSTest 版本 18.0.1 (x64)

正在启动测试执行，请稍候...
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:    19，已跳过:     0，总计:    19，持续时间: 49 ms - Dc.App.Tests.dll (net8.0)
```

## Build command log

Command:

```bash
dotnet build E:/CursorProjects/wpf-opc-client/src/Dc.App/Dc.App.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64
```

Output:

```text
正在确定要还原的项目…
所有项目均是最新的，无法还原。
Dc.Domain -> E:\CursorProjects\wpf-opc-client\src\Dc.Domain\bin\x64\Debug\net8.0\Dc.Domain.dll
Dc.Opc.Abstractions -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Abstractions\bin\x64\Debug\net8.0\Dc.Opc.Abstractions.dll
Technosoftware.OpcRcw -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\OpcRcw\bin\x64\Debug\net8.0-windows\Technosoftware.OpcRcw.dll
Technosoftware.DaAeHdaClient -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\DaAeHdaClient\bin\x64\Debug\net8.0-windows\Technosoftware.DaAeHdaClient.dll
Dc.Opc.Ua -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Ua\bin\x64\Debug\net8.0\Dc.Opc.Ua.dll
Dc.Infrastructure -> E:\CursorProjects\wpf-opc-client\src\Dc.Infrastructure\bin\x64\Debug\net8.0\Dc.Infrastructure.dll
Technosoftware.DaAeHdaClient.Com -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\DaAeHdaClient.Com\bin\x64\Debug\net8.0-windows\Technosoftware.DaAeHdaClient.Com.dll
Dc.Opc.Da -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Da\bin\x64\Debug\net8.0-windows\Dc.Opc.Da.dll
Dc.Opc.Ae -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Ae\bin\x64\Debug\net8.0-windows\Dc.Opc.Ae.dll
Dc.App -> E:\CursorProjects\wpf-opc-client\src\Dc.App\bin\x64\Debug\net8.0-windows\Dc.App.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:01.60
```

## Self-review

- Confirmed TDD RED/GREEN flow for Step 1 virtual input/result behavior and Step 9 validation behavior.
- `InputBindings` rebuild preserves selected tags for aliases that remain and initializes new aliases with `null`.
- Real `ToResult()` returns no formula/inputs and parses scale/offset with invariant culture; blank scale/offset become `null`.
- Virtual `ToResult()` sets `Tag.Item` from formula name, clears scaling, creates a `Formula`, and maps selected input rows to `FormulaInput`.
- Validation covers group requirement, real item and numeric scale/offset, virtual formula name required/unique, expression required, unselected aliases, and `IFormulaValidator` backstop.
- Build verification is clean: `Dc.App.csproj` build reports 0 warnings and 0 errors.
- Did not run full `dotnet build Dc.sln` because the task brief says it fails on a pre-existing vendor error and explicitly says not to run it.

## Concerns / notes

- `AvailableInputTags` follows the brief’s exact filtering snippet: real tags only and self-exclusion via `OriginalId`; this assumes the supplied `taskTags` collection is already task-scoped by the caller.
- `InputBindingRow` is in the same file after `GroupRow`; it is marked `partial` so CommunityToolkit.Mvvm can generate the `[ObservableProperty]` property successfully.

---

## Review findings fix: virtual Tag edit input preselection and IsVirtual rebuild

### What changed

- `E:/CursorProjects/wpf-opc-client/tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs`
  - Added `Edit_ExistingVirtualTag_PreselectsInputsFromFormula`.
  - Added `ToggleIsVirtual_AfterExpression_RebuildsInputBindings`.
- `E:/CursorProjects/wpf-opc-client/src/Dc.App/ViewModels/TagEditorViewModel.cs`
  - Existing virtual Tag constructor path now stores the matched `Formula`, refreshes `AvailableInputTags`, then creates `InputBindings` from `Formula.Inputs` and preselects `SelectedTag` by `SourceTagId`.
  - That edit-existing-virtual path does not call `RebuildInputBindings()`, avoiding wiped preselections.
  - Added `OnIsVirtualChanged(bool value)` to rebuild from the current expression when switched on and clear rows when switched off.

### TDD red check

Command:

```bash
dotnet test E:/CursorProjects/wpf-opc-client/tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~TagEditorViewModelTests
```

Output:

```text
Exit code 1
  正在确定要还原的项目…
  所有项目均是最新的，无法还原。
  Dc.Domain -> E:\CursorProjects\wpf-opc-client\src\Dc.Domain\bin\x64\Debug\net8.0\Dc.Domain.dll
  Dc.Opc.Abstractions -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Abstractions\bin\x64\Debug\net8.0\Dc.Opc.Abstractions.dll
  Technosoftware.DaAeHdaClient -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\DaAeHdaClient\bin\x64\Debug\net8.0-windows\Technosoftware.DaAeHdaClient.dll
  Technosoftware.OpcRcw -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\OpcRcw\bin\x64\Debug\net8.0-windows\Technosoftware.OpcRcw.dll
  Dc.Opc.Ua -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Ua\bin\x64\Debug\net8.0\Dc.Opc.Ua.dll
  Dc.Infrastructure -> E:\CursorProjects\wpf-opc-client\src\Dc.Infrastructure\bin\x64\Debug\net8.0\Dc.Infrastructure.dll
  Technosoftware.DaAeHdaClient.Com -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\DaAeHdaClient.Com\bin\x64\Debug\net8.0-windows\Technosoftware.DaAeHdaClient.Com.dll
  Dc.Opc.Da -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Da\bin\x64\Debug\net8.0-windows\Dc.Opc.Da.dll
  Dc.Opc.Ae -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Ae\bin\x64\Debug\net8.0-windows\Dc.Opc.Ae.dll
  Dc.App -> E:\CursorProjects\wpf-opc-client\src\Dc.App\bin\x64\Debug\net8.0-windows\Dc.App.dll
E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\ViewModels\Workspace\TaskWorkspaceViewModelTests.cs(53,30): warning CS0067: 从不使用事件“TaskWorkspaceViewModelTests.FakeTagPanel.NavigateToGroupsRequested” [E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\Dc.App.Tests.csproj]
E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\ViewModels\Workspace\TaskWorkspaceViewModelTests.cs(67,30): warning CS0067: 从不使用事件“TaskWorkspaceViewModelTests.FakeGroupPanel.NavigateToTasksRequested” [E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\Dc.App.Tests.csproj]
  Dc.App.Tests -> E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\bin\x64\Debug\net8.0-windows\Dc.App.Tests.dll
E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\bin\x64\Debug\net8.0-windows\Dc.App.Tests.dll (.NETCoreApp,Version=v8.0)的测试运行
VSTest 版本 18.0.1 (x64)

正在启动测试执行，请稍候...
总共 1 个测试文件与指定模式相匹配。
[xUnit.net 00:00:00.18]     Dc.App.Tests.ViewModels.TagEditorViewModelTests.ToggleIsVirtual_AfterExpression_RebuildsInputBindings [FAIL]
[xUnit.net 00:00:00.18]     Dc.App.Tests.ViewModels.TagEditorViewModelTests.Edit_ExistingVirtualTag_PreselectsInputsFromFormula [FAIL]
  失败 Dc.App.Tests.ViewModels.TagEditorViewModelTests.ToggleIsVirtual_AfterExpression_RebuildsInputBindings [1 ms]
  错误消息:
   Assert.Single() Failure: The collection was empty
  堆栈跟踪:
     at Dc.App.Tests.ViewModels.TagEditorViewModelTests.ToggleIsVirtual_AfterExpression_RebuildsInputBindings() in E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\ViewModels\TagEditorViewModelTests.cs:line 179
   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments, Signature sig, Boolean isConstructor)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
  失败 Dc.App.Tests.ViewModels.TagEditorViewModelTests.Edit_ExistingVirtualTag_PreselectsInputsFromFormula [< 1 ms]
  错误消息:
   Assert.NotNull() Failure: Value is null
  堆栈跟踪:
     at Dc.App.Tests.ViewModels.TagEditorViewModelTests.Edit_ExistingVirtualTag_PreselectsInputsFromFormula() in E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\ViewModels\TagEditorViewModelTests.cs:line 164
   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments, Signature sig, Boolean isConstructor)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

失败!  - 失败:     2，通过:    19，已跳过:     0，总计:    21，持续时间: 52 ms - Dc.App.Tests.dll (net8.0)
```

### Verification command 1: TagEditorViewModel tests

Command:

```bash
dotnet test E:/CursorProjects/wpf-opc-client/tests/Dc.App.Tests/Dc.App.Tests.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64 --filter FullyQualifiedName~TagEditorViewModelTests
```

Output:

```text
  正在确定要还原的项目…
  所有项目均是最新的，无法还原。
  Dc.Opc.Abstractions -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Abstractions\bin\x64\Debug\net8.0\Dc.Opc.Abstractions.dll
  Dc.Domain -> E:\CursorProjects\wpf-opc-client\src\Dc.Domain\bin\x64\Debug\net8.0\Dc.Domain.dll
  Technosoftware.OpcRcw -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\OpcRcw\bin\x64\Debug\net8.0-windows\Technosoftware.OpcRcw.dll
  Dc.Opc.Ua -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Ua\bin\x64\Debug\net8.0\Dc.Opc.Ua.dll
  Technosoftware.DaAeHdaClient -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\DaAeHdaClient\bin\x64\Debug\net8.0-windows\Technosoftware.DaAeHdaClient.dll
  Dc.Infrastructure -> E:\CursorProjects\wpf-opc-client\src\Dc.Infrastructure\bin\x64\Debug\net8.0\Dc.Infrastructure.dll
  Technosoftware.DaAeHdaClient.Com -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\DaAeHdaClient.Com\bin\x64\Debug\net8.0-windows\Technosoftware.DaAeHdaClient.Com.dll
  Dc.Opc.Da -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Da\bin\x64\Debug\net8.0-windows\Dc.Opc.Da.dll
  Dc.Opc.Ae -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Ae\bin\x64\Debug\net8.0-windows\Dc.Opc.Ae.dll
  Dc.App -> E:\CursorProjects\wpf-opc-client\src\Dc.App\bin\x64\Debug\net8.0-windows\Dc.App.dll
  Dc.App.Tests -> E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\bin\x64\Debug\net8.0-windows\Dc.App.Tests.dll
E:\CursorProjects\wpf-opc-client\tests\Dc.App.Tests\bin\x64\Debug\net8.0-windows\Dc.App.Tests.dll (.NETCoreApp,Version=v8.0)的测试运行
VSTest 版本 18.0.1 (x64)

正在启动测试执行，请稍候...
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:    21，已跳过:     0，总计:    21，持续时间: 48 ms - Dc.App.Tests.dll (net8.0)
```

### Verification command 2: Dc.App build

Command:

```bash
dotnet build E:/CursorProjects/wpf-opc-client/src/Dc.App/Dc.App.csproj -p:CustomTestTarget=net8.0-windows -p:Platform=x64
```

Output:

```text
  正在确定要还原的项目…
  所有项目均是最新的，无法还原。
  Dc.Domain -> E:\CursorProjects\wpf-opc-client\src\Dc.Domain\bin\x64\Debug\net8.0\Dc.Domain.dll
  Technosoftware.OpcRcw -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\OpcRcw\bin\x64\Debug\net8.0-windows\Technosoftware.OpcRcw.dll
  Dc.Opc.Abstractions -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Abstractions\bin\x64\Debug\net8.0\Dc.Opc.Abstractions.dll
  Technosoftware.DaAeHdaClient -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\DaAeHdaClient\bin\x64\Debug\net8.0-windows\Technosoftware.DaAeHdaClient.dll
  Dc.Opc.Ua -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Ua\bin\x64\Debug\net8.0\Dc.Opc.Ua.dll
  Technosoftware.DaAeHdaClient.Com -> E:\CursorProjects\wpf-opc-client\vendor\ClassicClient\src\Technosoftware\DaAeHdaClient.Com\bin\x64\Debug\net8.0-windows\Technosoftware.DaAeHdaClient.Com.dll
  Dc.Infrastructure -> E:\CursorProjects\wpf-opc-client\src\Dc.Infrastructure\bin\x64\Debug\net8.0\Dc.Infrastructure.dll
  Dc.Opc.Ae -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Ae\bin\x64\Debug\net8.0-windows\Dc.Opc.Ae.dll
  Dc.Opc.Da -> E:\CursorProjects\wpf-opc-client\src\Dc.Opc.Da\bin\x64\Debug\net8.0-windows\Dc.Opc.Da.dll
  Dc.App -> E:\CursorProjects\wpf-opc-client\src\Dc.App\bin\x64\Debug\net8.0-windows\Dc.App.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:01.53
```

### Self-review

- The two new tests were added before production changes and failed for the expected missing behaviors.
- Constructor ordering now refreshes `AvailableInputTags` before formula input preselection.
- Existing virtual edit path builds rows from persisted `Formula.Inputs`, preserving aliases and existing selected tags by `SourceTagId` instead of re-extracting from expression and wiping selections.
- `IsVirtual` change behavior matches the review request: switch on rebuilds from current `Expression`, switch off clears rows.
- Scope limited to the two review findings plus the requested report update.

### Concerns

- None.
