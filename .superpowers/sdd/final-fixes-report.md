# Final formula fixes report

## Changes

- Fix 1 (`src/Dc.Infrastructure/Orchestration/TagValueTransform.cs`): ready-gate now treats any quality with high bits `0xC0` as Good via `(quality & 0xC0) == 0xC0`, so Good substatus values such as `0xC8` make formula inputs ready.
- Fix 2 (`src/Dc.Infrastructure/Orchestration/TagValueTransform.cs`): scaling NaN/Infinity now sets output quality to the worse of raw quality and Uncertain; Bad raw quality remains Bad.
- Fix 3 (`src/Dc.Infrastructure/Orchestration/FormulaBuiltins.cs`, `FormulaValidator.cs`, `TagValueTransform.cs`): validator and runtime now share a single builtin registration source for the spec builtin functions and constants.
- Fix 4 (`src/Dc.Infrastructure/Orchestration/FormulaValidator.cs`): numeric type codes aligned to app codes `{0, 11, 2, 3, 4, 5, 16, 17, 18, 19, 20, 21}`; String `8` and DateTime `7` remain rejected.
- Fix 5 (`src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs`): transform is constructed before subscriber/publisher/CTS allocation, so formula parse failures cannot leave allocated startup resources.

## Tests added/adjusted

- `tests/Dc.Infrastructure.Tests/Orchestration/FormulaValidatorTests.cs`
  - switched numeric fixture code from old `6` to app `5`
  - added DateTime rejected (`7`), Boolean accepted (`11`), Int8 accepted (`16`), builtin validation, and full spec builtin conditional/aggregate coverage
- `tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformTests.cs`
  - added Good substatus ready-gate coverage with `0xC8`
  - added Bad raw quality + NaN scaling regression
- `tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs`
  - added invalid formula startup regression asserting throw, no subscriber/publisher allocation, and no half-started running task

## Red run before fixes

Command:

```bash
dotnet test E:/CursorProjects/wpf-opc-client/tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FormulaValidatorTests|FullyQualifiedName~TagValueTransformTests|FullyQualifiedName~TaskOrchestratorTests"
```

Observed expected failures:

```text
失败!  - 失败:     6，通过:    42，已跳过:     0，总计:    48，持续时间: 3 s - Dc.Infrastructure.Tests.dll (net8.0)
```

Failures covered Boolean/Int8 validation, builtin validation, Bad+NaN quality, Good substatus ready-gate, and invalid-formula startup allocation ordering.

## DynamicExpresso AVG/SUM adaptation

Initial registration with `Func<double[], double>` compiled and registered, but parsing `AVG(A, B)` failed:

```text
表达式无效：Argument list incompatible with delegate expression (at index 6).
```

Adaptation used an internal `params` delegate:

```csharp
private delegate double VariadicDouble(params double[] args);
interp.SetFunction("AVG", new VariadicDouble(args => args.Average()));
interp.SetFunction("SUM", new VariadicDouble(args => args.Sum()));
```

After this adaptation, `AVG(A, B)` / `SUM(A, B)` validation passed.

## Covering test runs

Targeted command after fixes:

```bash
dotnet test E:/CursorProjects/wpf-opc-client/tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FormulaValidatorTests|FullyQualifiedName~TagValueTransformTests|FullyQualifiedName~TaskOrchestratorTests|FullyQualifiedName~FormulaApiSpikeTests"
```

Output:

```text
已通过! - 失败:     0，通过:    51，已跳过:     0，总计:    51，持续时间: 3 s - Dc.Infrastructure.Tests.dll (net8.0)
```

Full project command:

```bash
dotnet test E:/CursorProjects/wpf-opc-client/tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj
```

Output:

```text
已通过! - 失败:     0，通过:   139，已跳过:     0，总计:   139，持续时间: 12 s - Dc.Infrastructure.Tests.dll (net8.0)
```

Note: the test project build still emits pre-existing warning `EF1002` from `src/Dc.Infrastructure/Persistence/DbSchemaInitializer.cs`; tests pass.
