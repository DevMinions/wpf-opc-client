# 虚拟测点与公式引擎 + Tag 缩放（核心引擎）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在采集 pipeline 中加入 Tag 缩放（原始值→工程量）与公式虚拟测点（同任务真实 Tag 的工程量经 DynamicExpresso 表达式求值），两者由独立的 `ITagValueTransform` 组件承载，编排器在每个 task 运行时持有。

**Architecture:** 真实 Tag 原始值经 `ITagValueTransform.Apply` 产出"缩放后工程量值 + 触发算出的虚拟值"列表，编排器循环发布/上抛。公式采用事件驱动 + 就绪门控（所有输入至少到过一次 Good 才产出，之后任一输入变化即算）+ 质量传播（输出质量 = 输入最差）。无公式无缩放时返回 `NoOpTransform` 零开销。本计划只做核心引擎（domain + transform + orchestrator + launcher + DI + 测试），UI 编辑器为后续计划。

**Tech Stack:** .NET 8, C#, EF Core (SQLite, `EnsureCreated`+`EnsureColumn` 无迁移), DynamicExpresso（表达式求值）, xUnit, CommunityToolkit.Mvvm（仅后续 UI 用）。

## Global Constraints

- .NET 8 SDK；遵循现有 snake_case 命名约定（`UseSnakeCaseNamingConvention`）。
- 数据库建库走 `DbSchemaInitializer.EnsureCreated` + `EnsureColumn`/`EnsureTable`（**不用** EF 迁移）；新表/新列对旧库兼容。
- 表名前缀 `dc_`；实体继承 `EntityBase`（`Id`/`CreatedAt`/`UpdatedAt`，ULID 主键）。
- 真实 Tag 的 `Item` 是 OPC 节点；虚拟 Tag 的 `Item` = `Formula.Name`（任务内唯一），虚拟 Tag **不进订阅器**。
- 现有 `TaskStartRequest` 是位置 record，新增字段必须带默认值，保证所有现有 5 参调用编译通过。
- `TaskOrchestrator` 构造函数新增参数必须可选，保证现有测试 `new TaskOrchestrator(factories, pubFactory, opts)` 编译通过。
- 公式输入仅同任务真实 Tag，数据类型可数值化（String 拒绝）。
- 频繁提交，每个任务结束 commit；提交信息用约定式前缀（`✨`/`🐛`/`📝`/`✅` 风格，对齐现有历史）。
- 测试放 `tests/Dc.Infrastructure.Tests/Orchestration/`（transform/launcher）与扩展 `TaskOrchestratorTests`；用现有 `FakeOpcSubscriber`/`FakePublisher`。

---

## 文件结构

**Create:**
- `src/Dc.Domain/Entities/Formula.cs` — 公式定义实体。
- `src/Dc.Domain/Entities/FormulaInput.cs` — 别名→输入 Tag 映射实体。
- `src/Dc.Infrastructure/Orchestration/TransformConfig.cs` — transform 静态配置快照（纯 record）。
- `src/Dc.Infrastructure/Orchestration/IFormulaValidator.cs` + `FormulaValidator.cs` — 表达式校验（DynamicExpresso）。
- `src/Dc.Infrastructure/Orchestration/ITagValueTransform.cs` — transform 接口。
- `src/Dc.Infrastructure/Orchestration/NoOpTransform.cs` — 无公式无缩放零开销实现。
- `src/Dc.Infrastructure/Orchestration/TagValueTransform.cs` — 缩放 + 公式就绪状态机实现。
- `src/Dc.Infrastructure/Orchestration/ITagValueTransformFactory.cs` + `TagValueTransformFactory.cs` — 按 TransformConfig 构建 transform。
- 测试：`TagValueTransformTests.cs`、`FormulaValidatorTests.cs`、`TagValueTransformFactoryTests.cs`。

**Modify:**
- `src/Dc.Domain/Entities/Tag.cs` — 加 `ScaleFactor`/`Offset`/`IsVirtual`。
- `src/Dc.Infrastructure/Persistence/DcDbContext.cs` — 加 `Formulas`/`FormulaInputs` DbSet + 映射。
- `src/Dc.Infrastructure/Persistence/DbSchemaInitializer.cs` — `EnsureColumn` 新列 + `EnsureTable` 新表。
- `src/Dc.Infrastructure/Orchestration/TaskStartRequest.cs` — 加 `TransformConfig?`。
- `src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs` — 注入 factory、建 transform、pipeline Apply、Add/Remove 联动。
- `src/Dc.Infrastructure/Orchestration/DbTaskLauncher.cs` — 组装 TransformConfig、过滤虚拟 Tag。
- `src/Dc.Infrastructure/Dc.Infrastructure.csproj` — 加 DynamicExpresso 包引用。
- `src/Dc.App/Composition/ServiceRegistration.cs` — 注册 `ITagValueTransformFactory`。
- `tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs` — 既有 `Request` helper 适配；新增 transform 集成用例。

---

### Task 1: 领域实体与持久化（Tag 扩展 + Formula + FormulaInput）

**Files:**
- Modify: `src/Dc.Domain/Entities/Tag.cs`
- Create: `src/Dc.Domain/Entities/Formula.cs`
- Create: `src/Dc.Domain/Entities/FormulaInput.cs`
- Modify: `src/Dc.Infrastructure/Persistence/DcDbContext.cs:19-39`
- Modify: `src/Dc.Infrastructure/Persistence/DbSchemaInitializer.cs:10-16`
- Test: `tests/Dc.Infrastructure.Tests/DcDbContextTests.cs`（已有文件，新增用例）

**Interfaces:**
- Produces: `Tag.ScaleFactor`/`Tag.Offset`/`Tag.IsVirtual`；`Formula`（`Id`,`Name`,`Expression`,`OutputTagId`,`OutputUnit?`,`TaskId`）；`FormulaInput`（`Id`,`FormulaId`,`Alias`,`SourceTagId`）；`DcDbContext.Formulas`/`FormulaInputs`。

- [ ] **Step 1: 写失败测试（持久化往返）**

在 `tests/Dc.Infrastructure.Tests/DcDbContextTests.cs` 末尾加（先看该文件已有 `using` 与建库辅助方式，沿用其模式；若它用 InMemory 或共享 Sqlite，沿用）：

```csharp
[Fact]
public async Task Formula_And_Inputs_Roundtrip()
{
    await using var db = CreateDb(); // 复用该文件已有的建库辅助方法

    var tag = new Tag { Id = "tag1", Item = "ns=2;s=T", DataType = 6, TaskId = "t1", GroupId = "g1", IsVirtual = false, ScaleFactor = 0.1, Offset = -5 };
    db.Tags.Add(tag);

    var virt = new Tag { Id = "vtag1", Item = "补偿流量", DataType = 6, TaskId = "t1", GroupId = "g1", IsVirtual = true };
    db.Tags.Add(virt);

    var f = new Formula { Id = "f1", Name = "补偿流量", Expression = "T * 1.8 + 32", OutputTagId = "vtag1", OutputUnit = "F", TaskId = "t1" };
    f.Inputs = new List<FormulaInput>
    {
        new() { Id = "fi1", FormulaId = "f1", Alias = "T", SourceTagId = "tag1" }
    };
    db.Formulas.Add(f);
    await db.SaveChangesAsync();

    await using var db2 = CreateDb();
    var loaded = await db2.Formulas.Include(x => x.Inputs).SingleAsync(x => x.Id == "f1");
    Assert.Equal("T * 1.8 + 32", loaded.Expression);
    Assert.Equal("vtag1", loaded.OutputTagId);
    Assert.Single(loaded.Inputs);
    Assert.Equal("T", loaded.Inputs[0].Alias);
    Assert.Equal("tag1", loaded.Inputs[0].SourceTagId);

    var tagBack = await db2.Tags.SingleAsync(x => x.Id == "tag1");
    Assert.Equal(0.1, tagBack.ScaleFactor);
    Assert.Equal(-5, tagBack.Offset);
    Assert.False(tagBack.IsVirtual);

    var virtBack = await db2.Tags.SingleAsync(x => x.Id == "vtag1");
    Assert.True(virtBack.IsVirtual);
}
```

> 若 `CreateDb()` 在该文件中命名不同，照搬该文件现有用例调用的建库方法名。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Formula_And_Inputs_Roundtrip"`
Expected: FAIL（`Formula`/`FormulaInput` 类型不存在，`Formulas` DbSet 不存在）。

- [ ] **Step 3: 扩展 Tag 实体**

`src/Dc.Domain/Entities/Tag.cs` 全文替换为：

```csharp
namespace Dc.Domain.Entities;

public class Tag : EntityBase
{
    public string Item { get; set; } = string.Empty;
    public int DataType { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;

    // 真实 Tag 的工程量映射；null 表示不缩放。虚拟 Tag 忽略。
    public double? ScaleFactor { get; set; }
    public double? Offset { get; set; }

    // true = 虚拟测点（公式产出），不进订阅器；Item = 公式名（任务内唯一）。
    public bool IsVirtual { get; set; }
}
```

- [ ] **Step 4: 新建 Formula 实体**

`src/Dc.Domain/Entities/Formula.cs`：

```csharp
using System.Text.Json.Serialization;

namespace Dc.Domain.Entities;

public class Formula : EntityBase
{
    public string Name { get; set; } = string.Empty;          // 任务内唯一；同时作为虚拟 Tag 的 Item
    public string Expression { get; set; } = string.Empty;     // DynamicExpresso 表达式
    public string OutputTagId { get; set; } = string.Empty;    // 产出的虚拟 Tag Id（一对一）
    public string? OutputUnit { get; set; }
    public string TaskId { get; set; } = string.Empty;

    [JsonIgnore]
    public List<FormulaInput> Inputs { get; set; } = new();
}
```

- [ ] **Step 5: 新建 FormulaInput 实体**

`src/Dc.Domain/Entities/FormulaInput.cs`：

```csharp
namespace Dc.Domain.Entities;

public class FormulaInput : EntityBase
{
    public string FormulaId { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;       // 表达式里的变量名，如 "T"
    public string SourceTagId { get; set; } = string.Empty; // 同任务真实 Tag Id
}
```

- [ ] **Step 6: DcDbContext 加映射**

在 `DcDbContext.OnModelCreating` 的 `modelBuilder.Entity<CollectorTask>(...)` 之后追加：

```csharp
modelBuilder.Entity<Formula>(e =>
{
    e.ToTable("dc_formulas");
    e.HasKey(x => x.Id);
    e.HasIndex(x => new { x.TaskId, x.Name }).IsUnique().HasDatabaseName("udx_formula_name");
    e.HasMany(x => x.Inputs).WithOne().HasForeignKey(i => i.FormulaId).OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<FormulaInput>(e =>
{
    e.ToTable("dc_formula_inputs");
    e.HasKey(x => x.Id);
    e.HasIndex(x => new { x.FormulaId, x.Alias }).IsUnique().HasDatabaseName("udx_formula_input_alias");
});
```

并在类顶部 DbSet 区加：

```csharp
public DbSet<Formula> Formulas => Set<Formula>();
public DbSet<FormulaInput> FormulaInputs => Set<FormulaInput>();
```

- [ ] **Step 7: DbSchemaInitializer 旧库兼容**

在 `DbSchemaInitializer.EnsureCreated` 中，既有 `EnsureColumn(...)` 调用之后追加：

```csharp
EnsureColumn(db, "dc_tags", "scale_factor", "scale_factor REAL NULL");
EnsureColumn(db, "dc_tags", "offset", "offset REAL NULL");
EnsureColumn(db, "dc_tags", "is_virtual", "is_virtual INTEGER NOT NULL DEFAULT 0");
EnsureTable(db, "dc_formulas", """
    CREATE TABLE IF NOT EXISTS dc_formulas (
        id TEXT NOT NULL PRIMARY KEY,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        name TEXT NOT NULL,
        expression TEXT NOT NULL,
        output_tag_id TEXT NOT NULL,
        output_unit TEXT NULL,
        task_id TEXT NOT NULL
    )
    """);
EnsureTable(db, "dc_formula_inputs", """
    CREATE TABLE IF NOT EXISTS dc_formula_inputs (
        id TEXT NOT NULL PRIMARY KEY,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        formula_id TEXT NOT NULL,
        alias TEXT NOT NULL,
        source_tag_id TEXT NOT NULL
    )
    """);
EnsureIndex(db, "udx_formula_name", "CREATE UNIQUE INDEX IF NOT EXISTS udx_formula_name ON dc_formulas (task_id, name)");
EnsureIndex(db, "udx_formula_input_alias", "CREATE UNIQUE INDEX IF NOT EXISTS udx_formula_input_alias ON dc_formula_inputs (formula_id, alias)");
```

并在该类底部加两个私有 helper（紧挨现有 `ColumnExists` 之后）：

```csharp
private static void EnsureTable(DcDbContext db, string table, string createSql)
{
    if (TableExists(db, table)) return;
    db.Database.ExecuteSqlRaw(createSql);
}

private static bool TableExists(DcDbContext db, string table)
{
    var conn = db.Database.GetDbConnection();
    var opened = false;
    if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t";
        var p = cmd.CreateParameter();
        p.ParameterName = "$t";
        p.Value = table;
        cmd.Parameters.Add(p);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
    finally { if (opened) conn.Close(); }
}

private static void EnsureIndex(DcDbContext db, string indexName, string createSql)
{
    var conn = db.Database.GetDbConnection();
    var opened = false;
    if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$n";
        var p = cmd.CreateParameter();
        p.ParameterName = "$n";
        p.Value = indexName;
        cmd.Parameters.Add(p);
        if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) return;
        db.Database.ExecuteSqlRaw(createSql);
    }
    finally { if (opened) conn.Close(); }
}
```

> `EnsureTable`/`EnsureIndex` 用 `sqlite_master` 检存在性，避免对已存在表/索引重复建（SQLite 的 `IF NOT EXISTS` 已足够，但显式检查与现有 `EnsureColumn` 风格一致，且 `EnsureIndex` 需先检存在再建以避开 `ExecuteSqlRaw` 对已存在唯一索引的报错）。

- [ ] **Step 8: 运行测试确认通过**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Formula_And_Inputs_Roundtrip"`
Expected: PASS。

- [ ] **Step 9: 跑全量持久化测试回归**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DcDbContextTests|FullyQualifiedName~DbSchemaInitializerTests"`
Expected: PASS（既有用例不受影响）。

- [ ] **Step 10: Commit**

```bash
git add src/Dc.Domain/Entities/Tag.cs src/Dc.Domain/Entities/Formula.cs src/Dc.Domain/Entities/FormulaInput.cs src/Dc.Infrastructure/Persistence/DcDbContext.cs src/Dc.Infrastructure/Persistence/DbSchemaInitializer.cs tests/Dc.Infrastructure.Tests/DcDbContextTests.cs
git commit -m "✨ feat(domain): Tag 缩放字段 + Formula/FormulaInput 实体与持久化"
```

---

### Task 2: 引入 DynamicExpresso 包 + API 探针

**Files:**
- Modify: `src/Dc.Infrastructure/Dc.Infrastructure.csproj`
- Test: `tests/Dc.Infrastructure.Tests/Orchestration/FormulaValidatorTests.cs`（本任务只写一个探针用例，正式校验在 Task 3）

**Interfaces:**
- Produces: `Dc.Infrastructure` 引用 `DynamicExpresso`。

- [ ] **Step 1: 加包引用**

在 `src/Dc.Infrastructure/Dc.Infrastructure.csproj` 的 `<ItemGroup>` 中加：

```xml
<PackageReference Include="DynamicExpresso.Core" Version="2.17.2" />
```

> 版本以 NuGet 最新稳定 2.x 为准；若还原失败，运行 `dotnet add src/Dc.Infrastructure/Dc.Infrastructure.csproj package DynamicExpresso.Core` 取最新稳定版并替换上方版本号。

- [ ] **Step 2: 还原并确认包可用**

Run: `dotnet restore src/Dc.sln`
Expected: 成功，无 NU1605/NU1603 致命错误。

- [ ] **Step 3: 写 API 探针测试**

`tests/Dc.Infrastructure.Tests/Orchestration/FormulaApiSpikeTests.cs`：

```csharp
using DynamicExpresso;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

// 确认 DynamicExpresso API 形态（Parse/Lambda.Invoke + SetFunction + 三元），
// 供后续 FormulaValidator/TagValueTransform 依赖。探针用例，验证后保留。
public class FormulaApiSpikeTests
{
    [Fact]
    public void Parse_And_Invoke_Caches_Lambda()
    {
        var interp = new Interpreter();
        var lambda = interp.Parse("T * 1.8 + 32", new Parameter("T", typeof(double)));
        Assert.Equal(212.0, (double)lambda.Invoke(100.0));
        Assert.Equal(32.0, (double)lambda.Invoke(0.0));
    }

    [Fact]
    public void SetFunction_Registers_Custom_Function()
    {
        var interp = new Interpreter();
        interp.SetFunction("SQRT", new Func<double, double>(Math.Sqrt));
        Assert.Equal(3.0, interp.Eval<double>("SQRT(9)"));
    }

    [Fact]
    public void Ternary_Is_Supported()
    {
        var interp = new Interpreter();
        Assert.Equal(1.0, interp.Eval<double>("T > 0 ? 1 : 0", new Parameter("T", 5.0)));
    }
}
```

- [ ] **Step 4: 运行探针**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FormulaApiSpikeTests"`
Expected: PASS。

> 若 `Parse`/`Lambda.Invoke`/`SetFunction` 任一签名不符，按编译器提示修正调用方式（核心 API 在 DynamicExpresso 2.x 稳定，此处仅兜底确认）。

- [ ] **Step 5: Commit**

```bash
git add src/Dc.Infrastructure/Dc.Infrastructure.csproj tests/Dc.Infrastructure.Tests/Orchestration/FormulaApiSpikeTests.cs
git commit -m "✅ chore(infra): 引入 DynamicExpresso + API 探针"
```

---

### Task 3: IFormulaValidator + 实现

**Files:**
- Create: `src/Dc.Infrastructure/Orchestration/IFormulaValidator.cs`
- Create: `src/Dc.Infrastructure/Orchestration/FormulaValidator.cs`
- Create: `src/Dc.Infrastructure/Orchestration/FormulaValidationResult.cs`
- Test: `tests/Dc.Infrastructure.Tests/Orchestration/FormulaValidatorTests.cs`

**Interfaces:**
- Consumes: DynamicExpresso（Task 2）。
- Produces: `IFormulaValidator.Validate(string expression, IReadOnlyDictionary<string,int> aliasToDataType, out string? error)`；后续 UI（Task 后续计划）与 transform 构建共用。

- [ ] **Step 1: 写失败测试**

`tests/Dc.Infrastructure.Tests/Orchestration/FormulaValidatorTests.cs`：

```csharp
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class FormulaValidatorTests
{
    private readonly FormulaValidator _v = new();

    // 数据类型码沿用 OpcDataTypeOption：6=Double 等。校验器只关心"是否可数值化"。
    private static readonly int Numeric = 6;
    private static readonly int StringType = 8; // 假定 8=String；校验器按"非数值型即拒绝"判

    [Fact]
    public void Valid_Expression_Passes()
    {
        var ok = _v.Validate("T * 1.8 + 32", new() { ["T"] = Numeric }, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void Undefined_Variable_Fails()
    {
        var ok = _v.Validate("T + P", new() { ["T"] = Numeric }, out var err); // P 未声明
        Assert.False(ok);
        Assert.Contains("P", err!);
    }

    [Fact]
    public void String_Input_Rejected()
    {
        var ok = _v.Validate("T + 1", new() { ["T"] = StringType }, out var err);
        Assert.False(ok);
        Assert.Contains("数值", err!);
    }

    [Fact]
    public void Syntax_Error_Fails()
    {
        var ok = _v.Validate("T +", new() { ["T"] = Numeric }, out var err);
        Assert.False(ok);
        Assert.NotNull(err);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FormulaValidatorTests"`
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 实现结果类型与接口**

`src/Dc.Infrastructure/Orchestration/FormulaValidationResult.cs`：

```csharp
namespace Dc.Infrastructure.Orchestration;

public readonly record struct FormulaValidationResult(bool IsValid, string? Error)
{
    public static FormulaValidationResult Ok() => new(true, null);
    public static FormulaValidationResult Fail(string error) => new(false, error);
}
```

`src/Dc.Infrastructure/Orchestration/IFormulaValidator.cs`：

```csharp
namespace Dc.Infrastructure.Orchestration;

public interface IFormulaValidator
{
    // aliasToDataType: 表达式里每个变量名 → 该输入 Tag 的数据类型码。
    // 非数值类型码 → 拒绝。返回是否合法 + 错误信息。
    bool Validate(string expression, IReadOnlyDictionary<string, int> aliasToDataType, out string? error);
}
```

- [ ] **Step 4: 实现 FormulaValidator**

`src/Dc.Infrastructure/Orchestration/FormulaValidator.cs`：

```csharp
using DynamicExpresso;

namespace Dc.Infrastructure.Orchestration;

public sealed class FormulaValidator : IFormulaValidator
{
    // 可数值化的数据类型码集合。调用方约定：Double=6, Float=5, Int32=3/4, Int16=1/2, Bool=0。
    // 不在此集合（如 String）→ 拒绝作为公式输入。
    private static readonly HashSet<int> NumericTypeCodes = new() { 0, 1, 2, 3, 4, 5, 6 };

    public bool Validate(string expression, IReadOnlyDictionary<string, int> aliasToDataType, out string? error)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "表达式不能为空";
            return false;
        }

        foreach (var (alias, code) in aliasToDataType)
        {
            if (!NumericTypeCodes.Contains(code))
            {
                error = $"输入 '{alias}' 的数据类型不可数值化，不能用于公式";
                return false;
            }
        }

        try
        {
            var interp = new Interpreter();
            var parameters = aliasToDataType
                .Select(kv => new Parameter(kv.Key, typeof(double)))
                .ToArray();
            interp.Parse(expression, parameters); // 语法/未定义变量在此抛
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"表达式无效：{ex.Message}";
            return false;
        }
    }
}
```

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FormulaValidatorTests"`
Expected: PASS。

> 若 `StringType=8` 不符合实际 OpcDataTypeOption 码，调整测试常量使"非 NumericTypeCodes 集合内"成立即可（校验逻辑以集合成员判定，与具体码无关）。

- [ ] **Step 6: Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/IFormulaValidator.cs src/Dc.Infrastructure/Orchestration/FormulaValidator.cs src/Dc.Infrastructure/Orchestration/FormulaValidationResult.cs tests/Dc.Infrastructure.Tests/Orchestration/FormulaValidatorTests.cs
git commit -m "✨ feat(formula): IFormulaValidator 表达式校验（DynamicExpresso）"
```

---

### Task 4: TransformConfig + ITagValueTransform + NoOpTransform

**Files:**
- Create: `src/Dc.Infrastructure/Orchestration/TransformConfig.cs`
- Create: `src/Dc.Infrastructure/Orchestration/ITagValueTransform.cs`
- Create: `src/Dc.Infrastructure/Orchestration/NoOpTransform.cs`
- Test: `tests/Dc.Infrastructure.Tests/Orchestration/NoOpTransformTests.cs`

**Interfaces:**
- Consumes: `TagValue`（Dc.Opc.Abstractions）、`TagDescriptor`。
- Produces: `ITagValueTransform.Apply(TagValue)→IReadOnlyList<TagValue>`；`OnTagsAdded/OnTagsRemoved(IEnumerable<TagDescriptor>)`；`NoOpTransform.Instance`。

- [ ] **Step 1: 写失败测试**

`tests/Dc.Infrastructure.Tests/Orchestration/NoOpTransformTests.cs`：

```csharp
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class NoOpTransformTests
{
    [Fact]
    public void Apply_ReturnsSingleElement_Unchanged()
    {
        var t = NoOpTransform.Instance;
        var v = new TagValue("A", 42.0, 0xC0, DateTimeOffset.UtcNow);
        var outp = t.Apply(v);
        Assert.Single(outp);
        Assert.Equal(v, outp[0]);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~NoOpTransformTests"`
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 实现 TransformConfig**

`src/Dc.Infrastructure/Orchestration/TransformConfig.cs`：

```csharp
namespace Dc.Infrastructure.Orchestration;

// 从 DB 加载的静态快照，task 启动时一次性构建，运行期不变（热加 Tag 不带缩放/公式信息）。
public sealed record ScaleConfig(double? ScaleFactor, double? Offset);

public sealed record FormulaInputConfig(string Alias, string SourceTagId);

public sealed record FormulaConfig(
    string FormulaId,
    string OutputItem,                 // 虚拟 Tag 的 Item（= Formula.Name），产出 TagValue 用它
    string Expression,
    IReadOnlyList<FormulaInputConfig> Inputs);

public sealed record TransformConfig(
    IReadOnlyDictionary<string, ScaleConfig> ScaleByTagId,  // 真实 TagId → 缩放
    IReadOnlyDictionary<string, string> ItemByTagId,        // 真实 TagId → Item（含热加前的真实 Tag）
    IReadOnlyList<FormulaConfig> Formulas);
```

- [ ] **Step 4: 实现 ITagValueTransform**

`src/Dc.Infrastructure/Orchestration/ITagValueTransform.cs`：

```csharp
using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

public interface ITagValueTransform
{
    // 处理一个真实 Tag 的原始值。返回该批应发布/上抛的所有值：
    // 顺序为先缩放后真值，再触发的虚拟值（若有）。空集合表示该真值被丢弃。
    // 仅接收真实 Tag 的原始值（编排器保证虚拟值不回流进 Apply）。
    IReadOnlyList<TagValue> Apply(TagValue raw);

    // 热加真实 Tag 时调用（虚拟 Tag 不走此路径）。
    void OnTagsAdded(IEnumerable<TagDescriptor> tags);

    // 热删真实 Tag 时调用；若被某公式引用，该公式转 Failed 停止产出。
    void OnTagsRemoved(IEnumerable<TagDescriptor> tags);
}
```

- [ ] **Step 5: 实现 NoOpTransform**

`src/Dc.Infrastructure/Orchestration/NoOpTransform.cs`：

```csharp
using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

// 无公式且无缩放时使用，零额外开销：直接返回单元素数组透传真值。
public sealed class NoOpTransform : ITagValueTransform
{
    public static readonly NoOpTransform Instance = new();
    private NoOpTransform() { }

    public IReadOnlyList<TagValue> Apply(TagValue raw) => new[] { raw };
    public void OnTagsAdded(IEnumerable<TagDescriptor> tags) { }
    public void OnTagsRemoved(IEnumerable<TagDescriptor> tags) { }
}
```

- [ ] **Step 6: 运行确认通过**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~NoOpTransformTests"`
Expected: PASS。

- [ ] **Step 7: Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/TransformConfig.cs src/Dc.Infrastructure/Orchestration/ITagValueTransform.cs src/Dc.Infrastructure/Orchestration/NoOpTransform.cs tests/Dc.Infrastructure.Tests/Orchestration/NoOpTransformTests.cs
git commit -m "✨ feat(transform): TransformConfig + ITagValueTransform + NoOpTransform"
```

---

### Task 5: TagValueTransform — 缩放

**Files:**
- Create: `src/Dc.Infrastructure/Orchestration/TagValueTransform.cs`
- Test: `tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformTests.cs`

**Interfaces:**
- Consumes: `TransformConfig`、`TagValue`。
- Produces: `TagValueTransform`（本任务只实现缩放路径，公式路径在 Task 6 接续）。

- [ ] **Step 1: 写失败测试（缩放）**

`tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformTests.cs`：

```csharp
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class TagValueTransformTests
{
    private static TagValue V(string item, object? val, ushort q = 0xC0) =>
        new(item, val, q, DateTimeOffset.UtcNow);

    private static TagValueTransform BuildScaleOnly(
        IReadOnlyDictionary<string, ScaleConfig> scale,
        IReadOnlyDictionary<string, string> itemByTagId) =>
        new(new TransformConfig(scale, itemByTagId, Array.Empty<FormulaConfig>()));

    [Fact]
    public void Apply_Scales_RealTag_EngineeringValue()
    {
        var cfg = BuildScaleOnly(
            new() { ["t1"] = new(0.1, 0) },
            new() { ["t1"] = "A" });
        var outp = cfg.Apply(V("A", 255.0));
        Assert.Single(outp);
        Assert.Equal(25.5, outp[0].Value);
        Assert.Equal("A", outp[0].Item);
        Assert.Equal(0xC0, outp[0].Quality);
    }

    [Fact]
    public void Apply_NoScale_PassesThrough()
    {
        var cfg = BuildScaleOnly(new() { ["t1"] = new(null, null) }, new() { ["t1"] = "A" });
        var outp = cfg.Apply(V("A", 42.0));
        Assert.Equal(42.0, outp[0].Value);
    }

    [Fact]
    public void Apply_NonNumeric_Passthrough_NoScale()
    {
        var cfg = BuildScaleOnly(new() { ["t1"] = new(2.0, 0) }, new() { ["t1"] = "A" });
        var outp = cfg.Apply(V("A", "hello")); // String 不可缩放
        Assert.Equal("hello", outp[0].Value);
    }

    [Fact]
    public void Apply_NaN_Result_MarkedUncertain()
    {
        var cfg = BuildScaleOnly(new() { ["t1"] = new(0.0, 0) }, new() { ["t1"] = "A" }); // 0 * x = 0 不 NaN
        // 构造 NaN：用 double.NaN 原值
        var outp = cfg.Apply(V("A", double.NaN));
        // NaN 透传为值，质量降为 Uncertain
        Assert.Equal(0x40, outp[0].Quality);
    }

    [Fact]
    public void Apply_UnknownItem_PassesThrough()
    {
        var cfg = BuildScaleOnly(new() { ["t1"] = new(2.0, 0) }, new() { ["t1"] = "A" });
        var outp = cfg.Apply(V("UNKNOWN", 1.0));
        Assert.Single(outp);
        Assert.Equal(1.0, outp[0].Value);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TagValueTransformTests"`
Expected: FAIL（`TagValueTransform` 不存在）。

- [ ] **Step 3: 实现 TagValueTransform（缩放部分）**

`src/Dc.Infrastructure/Orchestration/TagValueTransform.cs`：

```csharp
using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

public sealed class TagValueTransform : ITagValueTransform
{
    private readonly IReadOnlyDictionary<string, ScaleConfig> _scaleByTagId;
    private readonly Dictionary<string, string> _tagIdByItem;        // 反查：Item → TagId
    private readonly Dictionary<string, string> _itemByTagId;
    private readonly IReadOnlyList<FormulaConfig> _formulas;
    private readonly Dictionary<string, FormulaRuntime> _formulaById;
    private readonly Dictionary<string, List<(string formulaId, string alias)>> _inputsByTagId; // 真实 TagId → 引用它的公式

    public TagValueTransform(TransformConfig config)
    {
        _scaleByTagId = config.ScaleByTagId;
        _itemByTagId = new Dictionary<string, string>(config.ItemByTagId);
        _tagIdByItem = config.ItemByTagId.ToDictionary(kv => kv.Value, kv => kv.Key);
        _formulas = config.Formulas;
        _inputsByTagId = new Dictionary<string, List<(string, string)>>();

        _formulaById = new Dictionary<string, FormulaRuntime>();
        foreach (var f in _formulas)
        {
            var rt = new FormulaRuntime(f);
            _formulaById[f.FormulaId] = rt;
            foreach (var inp in f.Inputs)
            {
                if (!_inputsByTagId.TryGetValue(inp.SourceTagId, out var list))
                {
                    list = new List<(string, string)>();
                    _inputsByTagId[inp.SourceTagId] = list;
                }
                list.Add((f.FormulaId, inp.Alias));
            }
        }
        // 公式求值器在 Task 6 接续构建。
    }

    public IReadOnlyList<TagValue> Apply(TagValue raw)
    {
        // 解析真实 TagId
        if (!_tagIdByItem.TryGetValue(raw.Item, out var tagId))
        {
            return new[] { raw }; // 未知 Item（热加未登记）→ 透传
        }

        // 1) 缩放产出工程量
        var engineering = ApplyScale(raw, tagId);

        var outputs = new List<TagValue> { engineering };

        // 2) 公式求值（Task 6 接续）
        EvaluateFormulas(engineering, tagId, outputs);

        return outputs;
    }

    private TagValue ApplyScale(TagValue raw, string tagId)
    {
        if (!_scaleByTagId.TryGetValue(tagId, out var sc)
            || (sc.ScaleFactor is null && sc.Offset is null))
        {
            return raw; // 无缩放配置
        }

        if (!TryToDouble(raw.Value, out var num))
        {
            return raw; // 非数值型，原值透传
        }

        var scaled = num * (sc.ScaleFactor ?? 1.0) + (sc.Offset ?? 0.0);
        var q = raw.Quality;
        if (double.IsNaN(scaled) || double.IsInfinity(scaled))
        {
            q = 0x40; // Uncertain
        }
        return raw with { Value = scaled, Quality = q };
    }

    private static bool TryToDouble(object? v, out double d)
    {
        d = 0;
        if (v is null) return false;
        try
        {
            d = v switch
            {
                double dd => dd,
                float f => f,
                int i => i,
                long l => l,
                short s => s,
                ushort us => us,
                uint ui => ui,
                ulong ul => ul,
                bool b => b ? 1.0 : 0.0,
                _ => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EvaluateFormulas(TagValue engineering, string sourceTagId, List<TagValue> outputs)
    {
        // Task 6 实现。
    }

    public void OnTagsAdded(IEnumerable<TagDescriptor> tags)
    {
        foreach (var t in tags)
        {
            _tagIdByItem[t.Item] = t.Id;
            _itemByTagId[t.Id] = t.Item;
            // 热加 Tag 不带缩放/公式信息：不登记 ScaleByTagId，默认不缩放。
        }
    }

    public void OnTagsRemoved(IEnumerable<TagDescriptor> tags)
    {
        // Task 7 实现（标记依赖公式 Failed）。
        foreach (var t in tags)
        {
            _tagIdByItem.Remove(t.Item);
            _itemByTagId.Remove(t.Id);
        }
    }

    // Task 6 引入的公式运行时状态。
    private sealed class FormulaRuntime
    {
        public FormulaConfig Config { get; }
        public bool IsReady { get; set; }
        public bool IsFailed { get; set; }
        public Dictionary<string, (double value, ushort quality, bool seenGood)> Inputs { get; } = new();
        public FormulaRuntime(FormulaConfig c) { Config = c; }
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TagValueTransformTests"`
Expected: 5 个用例 PASS（NaN 用例：`0 * NaN = NaN` → Uncertain；其余按实现）。

> 检查 NaN 用例：`ScaleFactor=0.0`，`0.0 * NaN = NaN` → 降 Uncertain ✓。若 `double.NaN * 0` 在 .NET 为 NaN（确为 NaN），通过。

- [ ] **Step 5: Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/TagValueTransform.cs tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformTests.cs
git commit -m "✨ feat(transform): TagValueTransform 缩放路径"
```

---

### Task 6: TagValueTransform — 公式就绪门控 + 求值 + 质量传播

**Files:**
- Modify: `src/Dc.Infrastructure/Orchestration/TagValueTransform.cs`
- Modify: `tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformTests.cs`

**Interfaces:**
- Consumes: DynamicExpresso（Task 2）。
- Produces: `TagValueTransform.EvaluateFormulas` 完成；产出虚拟 `TagValue`。

- [ ] **Step 1: 写失败测试（公式就绪/求值/质量）**

在 `TagValueTransformTests.cs` 追加：

```csharp
private static TagValueTransform BuildWithFormula(
    IReadOnlyDictionary<string, ScaleConfig> scale,
    IReadOnlyDictionary<string, string> itemByTagId,
    params FormulaConfig[] formulas) =>
    new(new TransformConfig(scale, itemByTagId, formulas));

private static FormulaConfig Formula(string id, string outItem, string expr, params (string alias, string tagId)[] inputs) =>
    new(id, outItem, expr, inputs.Select(i => new FormulaInputConfig(i.alias, i.tagId)).ToArray());

[Fact]
public void Formula_NotReady_WhenInputOnlyBad_NoOutput()
{
    var t = BuildWithFormula(new() { ["t1"] = new(null, null) }, new() { ["t1"] = "A" },
        Formula("f1", "OUT", "A * 2", ("A", "t1")));
    var outp = t.Apply(V("A", 10.0, 0x00)); // Bad
    Assert.Single(outp); // 仅真值，无虚拟
    Assert.Equal("A", outp[0].Item);
}

[Fact]
public void Formula_Ready_AfterGoodInput_ProducesVirtual()
{
    var t = BuildWithFormula(new() { ["t1"] = new(null, null) }, new() { ["t1"] = "A" },
        Formula("f1", "OUT", "A * 2", ("A", "t1")));
    var outp = t.Apply(V("A", 10.0, 0xC0)); // Good → 就绪 + 立即算
    Assert.Equal(2, outp.Count);
    Assert.Equal("A", outp[0].Item);
    Assert.Equal("OUT", outp[1].Item);
    Assert.Equal(20.0, outp[1].Value);
    Assert.Equal(0xC0, outp[1].Quality);
}

[Fact]
public void Formula_MultiInput_NotReadyUntilAllGood()
{
    var t = BuildWithFormula(
        new() { ["t1"] = new(null, null), ["t2"] = new(null, null) },
        new() { ["t1"] = "A", ["t2"] = "B" },
        Formula("f1", "OUT", "A + B", ("A", "t1"), ("B", "t2")));

    var o1 = t.Apply(V("A", 1.0, 0xC0));
    Assert.Single(o1); // 仅 A，未就绪

    var o2 = t.Apply(V("B", 2.0, 0xC0));
    Assert.Equal(2, o2.Count); // B 真值 + 虚拟
    Assert.Equal("OUT", o2[1].Item);
    Assert.Equal(3.0, o2[1].Value);
}

[Fact]
public void Formula_QualityPropagation_BadInputMakesVirtualBad()
{
    var t = BuildWithFormula(
        new() { ["t1"] = new(null, null), ["t2"] = new(null, null) },
        new() { ["t1"] = "A", ["t2"] = "B" },
        Formula("f1", "OUT", "A + B", ("A", "t1"), ("B", "t2")));

    t.Apply(V("A", 1.0, 0xC0)); // 就绪
    var o = t.Apply(V("B", 2.0, 0x00)); // B=Bad
    Assert.Equal(2, o.Count);
    Assert.Equal(0x00, o[1].Quality); // 虚拟值 Bad
}

[Fact]
public void Formula_QualityPropagation_UncertainInputMakesVirtualUncertain()
{
    var t = BuildWithFormula(
        new() { ["t1"] = new(null, null), ["t2"] = new(null, null) },
        new() { ["t1"] = "A", ["t2"] = "B" },
        Formula("f1", "OUT", "A + B", ("A", "t1"), ("B", "t2")));

    t.Apply(V("A", 1.0, 0xC0));
    var o = t.Apply(V("B", 2.0, 0x40)); // Uncertain
    Assert.Equal(0x40, o[1].Quality);
}

[Fact]
public void Formula_EvalException_NoOutput_StaysReady()
{
    var t = BuildWithFormula(
        new() { ["t1"] = new(null, null), ["t2"] = new(null, null) },
        new() { ["t1"] = "A", ["t2"] = "B" },
        Formula("f1", "OUT", "A / B", ("A", "t1"), ("B", "t2")));

    t.Apply(V("A", 1.0, 0xC0));
    var o1 = t.Apply(V("B", 1.0, 0xC0)); // 1/1=1
    Assert.Equal(1.0, o1[1].Value);

    // B=0 → 除零。DynamicExpresso 抛或返 Inf；我们捕获异常/Inf 均不产出虚拟值。
    var o2 = t.Apply(V("B", 0.0, 0xC0));
    Assert.Single(o2); // 仅真值 B，无虚拟（异常路径不产出）

    // 恢复后仍可算 → 状态保持 Ready
    var o3 = t.Apply(V("B", 2.0, 0xC0));
    Assert.Equal(2, o3.Count);
    Assert.Equal(0.5, o3[1].Value);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TagValueTransformTests"`
Expected: 新增公式用例 FAIL（`EvaluateFormulas` 空实现，不产出虚拟值）。

- [ ] **Step 3: 实现公式求值**

在 `TagValueTransform.cs` 顶部加 `using DynamicExpresso;` 与 `using System.Globalization;`。

替换 `EvaluateFormulas` 与 `FormulaRuntime`，并加字段缓存 Lambda。把构造函数中为每个公式构建 `Interpreter`+`Lambda`：

替换构造函数中 `foreach (var f in _formulas)` 块为：

```csharp
        foreach (var f in _formulas)
        {
            var rt = new FormulaRuntime(f);
            _formulaById[f.FormulaId] = rt;
            foreach (var inp in f.Inputs)
            {
                if (!_inputsByTagId.TryGetValue(inp.SourceTagId, out var list))
                {
                    list = new List<(string, string)>();
                    _inputsByTagId[inp.SourceTagId] = list;
                }
                list.Add((f.FormulaId, inp.Alias));
            }
        }
```

（公式 Lambda 在 `FormulaRuntime` 构造时构建，见下。）

替换 `EvaluateFormulas` 方法体为：

```csharp
    private void EvaluateFormulas(TagValue engineering, string sourceTagId, List<TagValue> outputs)
    {
        if (!_inputsByTagId.TryGetValue(sourceTagId, out var refs)) return;

        foreach (var (formulaId, alias) in refs)
        {
            var rt = _formulaById[formulaId];
            if (rt.IsFailed) continue;

            // 更新输入槽（用工程量值 + 质量）
            if (TryToDouble(engineering.Value, out var num))
            {
                rt.Inputs[alias] = (num, engineering.Quality, engineering.Quality is 0xC0 || rt.Inputs.GetValueOrDefault(alias).seenGood);
                // seenGood: 本次 Good 则 true；否则保留历史
                if (engineering.Quality == 0xC0) rt.Inputs[alias] = (num, engineering.Quality, true);
                else rt.Inputs[alias] = (num, engineering.Quality, rt.Inputs.GetValueOrDefault(alias).seenGood);
            }
            else
            {
                // 不可数值化 → 视该输入 Bad
                rt.Inputs[alias] = (0, 0x00, rt.Inputs.GetValueOrDefault(alias).seenGood);
            }

            // 就绪门控：所有输入 seenGood
            if (!rt.IsReady)
            {
                if (rt.Config.Inputs.All(i => rt.Inputs.TryGetValue(i.Alias, out var s) && s.seenGood))
                    rt.IsReady = true;
                else
                    continue;
            }

            // 求值
            var virtualValue = TryEvaluate(rt);
            if (virtualValue is null) continue; // 异常/Inf → 不产出

            var quality = WorstQuality(rt);
            outputs.Add(new TagValue(rt.Config.OutputItem, virtualValue, quality, engineering.Timestamp));
        }
    }

    private static double? TryEvaluate(FormulaRuntime rt)
    {
        try
        {
            var args = rt.Config.Inputs
                .Select(i => (object)rt.Inputs[i.Alias].value)
                .ToArray();
            var result = rt.Lambda.Invoke(args);
            var d = Convert.ToDouble(result, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return null;
            return d;
        }
        catch
        {
            return null; // 节流日志在 Task（可选）后续；此处静默不产出
        }
    }

    private static ushort WorstQuality(FormulaRuntime rt)
    {
        ushort worst = 0xC0;
        foreach (var i in rt.Config.Inputs)
        {
            var q = rt.Inputs[i.Alias].quality;
            // 取最差：Bad(0x00) > Uncertain(0x40) > Good(0xC0)（按高 2 位：00 < 01 < 11）
            if ((q & 0xC0) < (worst & 0xC0)) worst = q;
        }
        return worst;
    }
```

> 注意 `seenGood` 更新块有两行赋值，第一行为冗余兜底；为清晰起见，删掉第一行 `rt.Inputs[alias] = ...`，只保留下面带 `if/else` 的两行。最终该方法体如下（替换上面"更新输入槽"段落）：

```csharp
            // 更新输入槽（用工程量值 + 质量）
            if (TryToDouble(engineering.Value, out var num))
            {
                var prevSeenGood = rt.Inputs.GetValueOrDefault(alias).seenGood;
                var seenGood = engineering.Quality == 0xC0 || prevSeenGood;
                rt.Inputs[alias] = (num, engineering.Quality, seenGood);
            }
            else
            {
                var prevSeenGood = rt.Inputs.GetValueOrDefault(alias).seenGood;
                rt.Inputs[alias] = (0, 0x00, prevSeenGood);
            }
```

替换 `FormulaRuntime` 内部类为：

```csharp
    private sealed class FormulaRuntime
    {
        public FormulaConfig Config { get; }
        public Lambda Lambda { get; }
        public bool IsReady { get; set; }
        public bool IsFailed { get; set; }
        public Dictionary<string, (double value, ushort quality, bool seenGood)> Inputs { get; } = new();

        public FormulaRuntime(FormulaConfig c)
        {
            Config = c;
            var interp = new Interpreter();
            RegisterBuiltins(interp);
            var parameters = c.Inputs
                .Select(i => new Parameter(i.Alias, typeof(double)))
                .ToArray();
            Lambda = interp.Parse(c.Expression, parameters);
        }

        private static void RegisterBuiltins(Interpreter interp)
        {
            interp.SetFunction("SQRT", new Func<double, double>(Math.Sqrt));
            interp.SetFunction("ABS", new Func<double, double>(Math.Abs));
            interp.SetFunction("SIN", new Func<double, double>(Math.Sin));
            interp.SetFunction("COS", new Func<double, double>(Math.Cos));
            interp.SetFunction("TAN", new Func<double, double>(Math.Tan));
            interp.SetFunction("EXP", new Func<double, double>(Math.Exp));
            interp.SetFunction("LOG", new Func<double, double>(Math.Log));
            interp.SetFunction("LOG10", new Func<double, double>(Math.Log10));
            interp.SetFunction("FLOOR", new Func<double, double>(Math.Floor));
            interp.SetFunction("CEILING", new Func<double, double>(Math.Ceiling));
            interp.SetFunction("POW", new Func<double, double, double>(Math.Pow));
            interp.SetFunction("MIN", new Func<double, double, double>(Math.Min));
            interp.SetFunction("MAX", new Func<double, double, double>(Math.Max));
            interp.SetFunction("ROUND", new Func<double, double, double>((v, d) => Math.Round(v, (int)d)));
            interp.SetVariable("PI", Math.PI);
            interp.SetVariable("E", Math.E);
        }
    }
```

> `TagValueTransform` 类需加 `using DynamicExpresso;` 与 `using System.Globalization;`。`FormulaRuntime` 是嵌套私有类，`Parameter`/`Lambda` 来自 DynamicExpresso。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TagValueTransformTests"`
Expected: 全部 PASS。

> 若 `Lambda.Invoke(object[])` 返回 `object` 需拆箱，`Convert.ToDouble` 已处理。若 DynamicExpresso 的 `Parse` 要求参数类型以 `typeof(double)` 传入（已如此），通过。

- [ ] **Step 5: Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/TagValueTransform.cs tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformTests.cs
git commit -m "✨ feat(transform): 公式就绪门控 + 求值 + 质量传播"
```

---

### Task 7: TagValueTransform — OnTagsRemoved 标记 Failed

**Files:**
- Modify: `src/Dc.Infrastructure/Orchestration/TagValueTransform.cs`
- Modify: `tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformTests.cs`

**Interfaces:**
- Produces: `OnTagsRemoved` 把引用被删输入的公式置 `IsFailed`，停止产出。

- [ ] **Step 1: 写失败测试**

在 `TagValueTransformTests.cs` 追加：

```csharp
[Fact]
public void OnTagsRemoved_MarksDependentFormulaFailed_StopsOutput()
{
    var t = BuildWithFormula(
        new() { ["t1"] = new(null, null), ["t2"] = new(null, null) },
        new() { ["t1"] = "A", ["t2"] = "B" },
        Formula("f1", "OUT", "A + B", ("A", "t1"), ("B", "t2")));

    t.Apply(V("A", 1.0, 0xC0));
    t.Apply(V("B", 2.0, 0xC0)); // 就绪，产出 OUT=3

    // 热删 B
    t.OnTagsRemoved(new[] { new TagDescriptor("t2", "B", 6) });

    // 再来 A 值，公式应 Failed 不再产出
    var o = t.Apply(V("A", 5.0, 0xC0));
    Assert.Single(o); // 仅真值 A
    Assert.Equal("A", o[0].Item);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~OnTagsRemoved_MarksDependentFormulaFailed"`
Expected: FAIL（当前 `OnTagsRemoved` 只清映射，未标记 Failed；虚拟值仍产出）。

- [ ] **Step 3: 实现 OnTagsRemoved 标记 Failed**

替换 `TagValueTransform.OnTagsRemoved` 方法体为：

```csharp
    public void OnTagsRemoved(IEnumerable<TagDescriptor> tags)
    {
        foreach (var t in tags)
        {
            _tagIdByItem.Remove(t.Item);
            _itemByTagId.Remove(t.Id);

            // 被删 Tag 是某公式输入 → 该公式 Failed，停止产出
            if (_inputsByTagId.TryGetValue(t.Id, out var refs))
            {
                foreach (var (formulaId, _) in refs)
                {
                    if (_formulaById.TryGetValue(formulaId, out var rt))
                        rt.IsFailed = true;
                }
            }
        }
    }
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TagValueTransformTests"`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/TagValueTransform.cs tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformTests.cs
git commit -m "✨ feat(transform): OnTagsRemoved 标记依赖公式 Failed"
```

---

### Task 8: ITagValueTransformFactory + 实现

**Files:**
- Create: `src/Dc.Infrastructure/Orchestration/ITagValueTransformFactory.cs`
- Create: `src/Dc.Infrastructure/Orchestration/TagValueTransformFactory.cs`
- Test: `tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformFactoryTests.cs`

**Interfaces:**
- Consumes: `TransformConfig`、`TagValueTransform`、`NoOpTransform`。
- Produces: `ITagValueTransformFactory.Create(string taskId, TransformConfig config)→ITagValueTransform`；无公式无缩放返回 `NoOpTransform.Instance`。

- [ ] **Step 1: 写失败测试**

`tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformFactoryTests.cs`：

```csharp
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class TagValueTransformFactoryTests
{
    private readonly TagValueTransformFactory _f = new();

    [Fact]
    public void Create_NoFormulaNoScale_ReturnsNoOp()
    {
        var cfg = new TransformConfig(
            new() { ["t1"] = new ScaleConfig(null, null) },
            new() { ["t1"] = "A" },
            Array.Empty<FormulaConfig>());
        var t = _f.Create("t1", cfg);
        Assert.Same(NoOpTransform.Instance, t);
    }

    [Fact]
    public void Create_WithScale_ReturnsRealTransform()
    {
        var cfg = new TransformConfig(
            new() { ["t1"] = new ScaleConfig(2.0, 0) },
            new() { ["t1"] = "A" },
            Array.Empty<FormulaConfig>());
        var t = _f.Create("t1", cfg);
        Assert.IsType<TagValueTransform>(t);
    }

    [Fact]
    public void Create_WithFormula_ReturnsRealTransform()
    {
        var cfg = new TransformConfig(
            new() { ["t1"] = new ScaleConfig(null, null) },
            new() { ["t1"] = "A" },
            new[] { new FormulaConfig("f1", "OUT", "A*2", new[] { new FormulaInputConfig("A", "t1") }) });
        var t = _f.Create("t1", cfg);
        Assert.IsType<TagValueTransform>(t);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TagValueTransformFactoryTests"`
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 实现接口与工厂**

`src/Dc.Infrastructure/Orchestration/ITagValueTransformFactory.cs`：

```csharp
namespace Dc.Infrastructure.Orchestration;

public interface ITagValueTransformFactory
{
    // 无公式且无缩放时返回 NoOpTransform.Instance 以零开销。
    ITagValueTransform Create(string taskId, TransformConfig config);
}
```

`src/Dc.Infrastructure/Orchestration/TagValueTransformFactory.cs`：

```csharp
namespace Dc.Infrastructure.Orchestration;

public sealed class TagValueTransformFactory : ITagValueTransformFactory
{
    public ITagValueTransform Create(string taskId, TransformConfig config)
    {
        bool hasScale = config.ScaleByTagId.Values
            .Any(s => s.ScaleFactor is not null || s.Offset is not null);
        bool hasFormula = config.Formulas.Count > 0;

        if (!hasScale && !hasFormula)
            return NoOpTransform.Instance;

        return new TagValueTransform(config);
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TagValueTransformFactoryTests"`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/ITagValueTransformFactory.cs src/Dc.Infrastructure/Orchestration/TagValueTransformFactory.cs tests/Dc.Infrastructure.Tests/Orchestration/TagValueTransformFactoryTests.cs
git commit -m "✨ feat(transform): ITagValueTransformFactory（NoOp 零开销短路）"
```

---

### Task 9: TaskStartRequest.TransformConfig + TaskOrchestrator 集成

**Files:**
- Modify: `src/Dc.Infrastructure/Orchestration/TaskStartRequest.cs`
- Modify: `src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs`
- Modify: `tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs`

**Interfaces:**
- Consumes: `ITagValueTransformFactory`、`TransformConfig`、`ITagValueTransform`。
- Produces: `TaskStartRequest.TransformConfig`；编排器 pipeline 经 `Apply` 循环发布；Add/Remove 联动 transform；factory 可选（null→NoOp）。

- [ ] **Step 1: 写失败测试（虚拟 Tag 不订阅 + 缩放发布 + 移除输入停虚拟）**

在 `TaskOrchestratorTests.cs` 顶部 `using` 区加 `using Dc.Infrastructure.Orchestration;`（已有则跳过）。新增工厂构造 helper 与用例：

```csharp
    private static TaskStartRequest RequestWithTransform(
        string taskId, TransformConfig cfg, params TagDescriptor[] tags) =>
        new(taskId, OpcProtocol.Da,
            new OpcConnectionOptions { ServerUri = "opc.tcp://localhost:4840" },
            "127.0.0.1:5000",
            tags.Length == 0 ? Array.Empty<TagDescriptor>() : tags,
            cfg);

    private static TaskOrchestrator BuildWithTransformFactory(
        out FakeOpcSubscriberFactory daFactory, out FakePublisherFactory pubFactory)
    {
        daFactory = new FakeOpcSubscriberFactory(OpcProtocol.Da);
        pubFactory = new FakePublisherFactory();
        return new TaskOrchestrator(
            new[] { (IOpcSubscriberFactory)daFactory },
            pubFactory,
            new TagValueTransformFactory());
    }

    [Fact]
    public async Task StartAsync_WithScale_PublishesEngineeringValue()
    {
        var (orch, daFactory, pubFactory) = (BuildWithTransformFactory(out var da, out var pub), da, pub);
        await using var _ = orch;

        var cfg = new TransformConfig(
            new() { ["t1"] = new ScaleConfig(0.1, 0) },
            new() { ["t1"] = "A" },
            Array.Empty<FormulaConfig>());
        await orch.StartAsync(RequestWithTransform("t1", cfg, new TagDescriptor("t1", "A", 6)));
        var sub = daFactory.Created.First();
        var pub = pubFactory.Created.First().Publisher;

        sub.EmitValue(new TagValue("A", 255.0, 0xC0, DateTimeOffset.UtcNow));

        await WaitForAsync(() => pub.Published.Count >= 1);
        Assert.Equal(25.5, pub.Published[0].Value);
    }

    [Fact]
    public async Task StartAsync_VirtualTagNotInSubscriberList()
    {
        // transform 配置带公式，但订阅列表只含真实 Tag（编排器不过滤——过滤在 DbTaskLauncher；
        // 此用例验证：传给订阅器的 Tags 即 request.Tags，虚拟 Tag 由调用方不传入）。
        var (orch, daFactory, pubFactory) = (BuildWithTransformFactory(out var da, out var pub), da, pub);
        await using var _ = orch;

        var cfg = new TransformConfig(
            new() { ["t1"] = new ScaleConfig(null, null) },
            new() { ["t1"] = "A" },
            new[] { new FormulaConfig("f1", "OUT", "A*2", new[] { new FormulaInputConfig("A", "t1") }) });
        // 仅真实 Tag "A" 传入订阅
        await orch.StartAsync(RequestWithTransform("t1", cfg, new TagDescriptor("t1", "A", 6)));
        var sub = daFactory.Created.First();
        var pub = pubFactory.Created.First().Publisher;

        Assert.Single(sub.Subscribed); // 虚拟 OUT 未订阅
        Assert.Equal("A", sub.Subscribed[0].Item);

        sub.EmitValue(new TagValue("A", 10.0, 0xC0, DateTimeOffset.UtcNow));
        await WaitForAsync(() => pub.Published.Count >= 2);
        Assert.Contains(pub.Published, v => v.Item == "A" && (double)v.Value! == 10.0);
        Assert.Contains(pub.Published, v => v.Item == "OUT" && (double)v.Value! == 20.0);
    }

    [Fact]
    public async Task RemoveTagsAsync_StopsVirtualOutput_WhenInputRemoved()
    {
        var (orch, daFactory, pubFactory) = (BuildWithTransformFactory(out var da, out var pub), da, pub);
        await using var _ = orch;

        var cfg = new TransformConfig(
            new() { ["t1"] = new ScaleConfig(null, null), ["t2"] = new ScaleConfig(null, null) },
            new() { ["t1"] = "A", ["t2"] = "B" },
            new[] { new FormulaConfig("f1", "OUT", "A+B",
                new[] { new FormulaInputConfig("A", "t1"), new FormulaInputConfig("B", "t2") }) });
        await orch.StartAsync(RequestWithTransform("t1", cfg,
            new TagDescriptor("t1", "A", 6), new TagDescriptor("t2", "B", 6)));
        var sub = daFactory.Created.First();
        var pub = pubFactory.Created.First().Publisher;

        sub.EmitValue(new TagValue("A", 1.0, 0xC0, DateTimeOffset.UtcNow));
        sub.EmitValue(new TagValue("B", 2.0, 0xC0, DateTimeOffset.UtcNow));
        await WaitForAsync(() => pub.Published.Any(v => v.Item == "OUT"));

        var outCountBefore = pub.Published.Count(v => v.Item == "OUT");

        // 移除输入 B
        await orch.RemoveTagsAsync("t1", new[] { "B" });

        sub.EmitValue(new TagValue("A", 5.0, 0xC0, DateTimeOffset.UtcNow));
        await Task.Delay(100);
        var outCountAfter = pub.Published.Count(v => v.Item == "OUT");
        Assert.Equal(outCountBefore, outCountAfter); // 移除后不再产出 OUT
    }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~StartAsync_WithScale_PublishesEngineeringValue|FullyQualifiedName~StartAsync_VirtualTagNotInSubscriberList|FullyQualifiedName~RemoveTagsAsync_StopsVirtualOutput"`
Expected: FAIL（`TaskStartRequest` 无 `TransformConfig` 参数；`TaskOrchestrator` 构造不接受 factory）。

- [ ] **Step 3: 扩展 TaskStartRequest**

`src/Dc.Infrastructure/Orchestration/TaskStartRequest.cs` 全文替换为：

```csharp
using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Orchestration;

public sealed record TaskStartRequest(
    string TaskId,
    OpcProtocol Protocol,
    OpcConnectionOptions OpcOptions,
    string PublisherAddress,
    IReadOnlyCollection<TagDescriptor> Tags,
    TransformConfig? TransformConfig = null);
```

- [ ] **Step 4: TaskOrchestrator 构造函数加 factory 参数**

在 `TaskOrchestrator.cs` 加字段与构造参数。把：

```csharp
    private readonly IReadOnlyDictionary<OpcProtocol, IOpcSubscriberFactory> _factories;
    private readonly IPublisherFactory _publisherFactory;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<TaskOrchestrator>? _logger;
```

改为（加一行 `_transformFactory`）：

```csharp
    private readonly IReadOnlyDictionary<OpcProtocol, IOpcSubscriberFactory> _factories;
    private readonly IPublisherFactory _publisherFactory;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<TaskOrchestrator>? _logger;
    private readonly ITagValueTransformFactory? _transformFactory;
```

构造函数签名与赋值改为：

```csharp
    public TaskOrchestrator(
        IEnumerable<IOpcSubscriberFactory> factories,
        IPublisherFactory publisherFactory,
        OrchestratorOptions? options = null,
        ILogger<TaskOrchestrator>? logger = null,
        ITagValueTransformFactory? transformFactory = null)
    {
        _factories = factories.ToDictionary(f => f.Protocol);
        _publisherFactory = publisherFactory;
        _options = options ?? new OrchestratorOptions();
        _logger = logger;
        _transformFactory = transformFactory;
        _watchdogTask = Task.Run(WatchdogLoopAsync);
    }
```

- [ ] **Step 5: TaskRuntime 加 Transform 字段**

在 `TaskRuntime` 内部类加：

```csharp
        public required ITagValueTransform Transform { get; init; }
```

- [ ] **Step 6: StartUnlockedAsync 构建 transform**

在 `StartUnlockedAsync` 中，`var runtime = new TaskRuntime { ... }` 初始化块内加 `Transform` 构建。在 `var runtime = new TaskRuntime` 之前加：

```csharp
        ITagValueTransform transform =
            (request.TransformConfig is not null && _transformFactory is not null)
                ? _transformFactory.Create(request.TaskId, request.TransformConfig)
                : NoOpTransform.Instance;
```

并在 `new TaskRuntime { ... }` 块内加一行 `Transform = transform,`（与 `Tags = request.Tags.ToDictionary(...)` 同级）。

- [ ] **Step 7: pipeline 改 Apply 循环发布**

把 `RunPipelineAsync` 中真值 handler：

```csharp
        var valuesTask = ConsumeAsync(rt.Subscriber.TagValues, async v =>
        {
            Interlocked.Increment(ref rt.ValueCount);
            rt.LastValueAt = DateTimeOffset.UtcNow;
            TagValueReceived?.Invoke(rt.TaskId, v);
            try { await rt.Publisher.PublishAsync(v, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { Interlocked.Increment(ref rt.PublishErrorCount); return; }
            Interlocked.Increment(ref rt.PublishSuccessCount);
        }, ct);
```

替换为：

```csharp
        var valuesTask = ConsumeAsync(rt.Subscriber.TagValues, async v =>
        {
            Interlocked.Increment(ref rt.ValueCount);
            rt.LastValueAt = DateTimeOffset.UtcNow;
            var outputs = rt.Transform.Apply(v);
            foreach (var o in outputs)
            {
                TagValueReceived?.Invoke(rt.TaskId, o);
                try { await rt.Publisher.PublishAsync(o, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { Interlocked.Increment(ref rt.PublishErrorCount); continue; }
                Interlocked.Increment(ref rt.PublishSuccessCount);
            }
        }, ct);
```

- [ ] **Step 8: AddTagsAsync / RemoveTagsAsync 联动 transform**

`AddTagsAsync`：在 `foreach (var t in added) rt.Tags[t.Item] = t;` 之后加：

```csharp
            rt.Transform.OnTagsAdded(added);
```

`RemoveTagsAsync`：把

```csharp
            var present = tagItems.Where(rt.Tags.ContainsKey).ToArray();
            if (present.Length == 0) return true;
            await rt.Subscriber.UnsubscribeAsync(present, ct).ConfigureAwait(false);
            foreach (var item in present) rt.Tags.Remove(item);
            return true;
```

改为（解析描述符传给 transform）：

```csharp
            var present = tagItems.Where(rt.Tags.ContainsKey).ToArray();
            if (present.Length == 0) return true;
            var presentDescs = present.Select(i => rt.Tags[i]).ToArray();
            await rt.Subscriber.UnsubscribeAsync(present, ct).ConfigureAwait(false);
            rt.Transform.OnTagsRemoved(presentDescs);
            foreach (var item in present) rt.Tags.Remove(item);
            return true;
```

- [ ] **Step 9: 运行新用例 + 既有回归**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TaskOrchestratorTests"`
Expected: 全部 PASS（既有用例因默认 NoOp 透传，`StartAsync_TaskValuesFlowToPublisher` 仍 `v` 透传通过；新用例通过）。

> 若 `StartAsync_TaskValuesFlowToPublisher` 断言 `pub.Published.First()` equals 原值——NoOp 透传同一 `TagValue`，通过。

- [ ] **Step 10: Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/TaskStartRequest.cs src/Dc.Infrastructure/Orchestration/TaskOrchestrator.cs tests/Dc.Infrastructure.Tests/Orchestration/TaskOrchestratorTests.cs
git commit -m "✨ feat(orchestrator): pipeline 接入 ITagValueTransform（缩放+公式）"
```

---

### Task 10: DbTaskLauncher 组装 TransformConfig + 过滤虚拟 Tag

**Files:**
- Modify: `src/Dc.Infrastructure/Orchestration/DbTaskLauncher.cs`
- Modify: `tests/Dc.Infrastructure.Tests/Orchestration/DbTaskLauncherTests.cs`

**Interfaces:**
- Consumes: `Formula`/`FormulaInput`（Task 1）、`TransformConfig`。
- Produces: `ToStartRequest` 组装 `TransformConfig`；订阅 `Tags` 仅真实 Tag。

- [ ] **Step 1: 写失败测试**

`tests/Dc.Infrastructure.Tests/Orchestration/DbTaskLauncherTests.cs`（若不存在则新建；先读该文件确认既有用例风格）：

```csharp
using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class DbTaskLauncherTests
{
    private static CollectorTask TaskWithTags(params Tag[] tags)
    {
        var t = new CollectorTask { Id = "t1", Type = 1, Node = "opc.tcp://localhost:4840", TcpAddress = "127.0.0.1:5000" };
        t.Tags = tags.ToList();
        foreach (var tag in tags) tag.TaskId = t.Id;
        return t;
    }

    [Fact]
    public void ToStartRequest_ExcludesVirtualTags_FromSubscriber()
    {
        var real = new Tag { Id = "r1", Item = "A", DataType = 6, IsVirtual = false };
        var virt = new Tag { Id = "v1", Item = "OUT", DataType = 6, IsVirtual = true };
        var task = TaskWithTags(real, virt);

        var req = DbTaskLauncher.ToStartRequest(task);

        Assert.Single(req.Tags);
        Assert.Equal("A", req.Tags.Single().Item);
    }

    [Fact]
    public void ToStartRequest_BuildsTransformConfig_WithFormulas()
    {
        var real = new Tag { Id = "r1", Item = "A", DataType = 6, IsVirtual = false, ScaleFactor = 0.1 };
        var virt = new Tag { Id = "v1", Item = "OUT", DataType = 6, IsVirtual = true };
        var task = TaskWithTags(real, virt);

        var formula = new Formula
        {
            Id = "f1", Name = "OUT", Expression = "A*2", OutputTagId = "v1", TaskId = "t1",
            Inputs = new() { new() { Id = "fi1", FormulaId = "f1", Alias = "A", SourceTagId = "r1" } }
        };

        var req = DbTaskLauncher.ToStartRequest(task, new[] { formula });

        Assert.NotNull(req.TransformConfig);
        Assert.Single(req.TransformConfig!.Formulas);
        Assert.Equal("OUT", req.TransformConfig.Formulas[0].OutputItem);
        Assert.Equal(0.1, req.TransformConfig.ScaleByTagId["r1"].ScaleFactor);
    }

    [Fact]
    public void ToStartRequest_NoFormulas_NullTransformConfig()
    {
        var real = new Tag { Id = "r1", Item = "A", DataType = 6, IsVirtual = false };
        var task = TaskWithTags(real);
        var req = DbTaskLauncher.ToStartRequest(task);
        Assert.Null(req.TransformConfig);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DbTaskLauncherTests"`
Expected: FAIL（`ToStartRequest` 无公式重载，不组装 TransformConfig）。

- [ ] **Step 3: 改 DbTaskLauncher.ToStartRequest**

`src/Dc.Infrastructure/Orchestration/DbTaskLauncher.cs`：把两个 `ToStartRequest` 重载改为接收可选公式并组装。替换现有两个重载为：

```csharp
    public static TaskStartRequest ToStartRequest(CollectorTask task) =>
        ToStartRequest(task, task.Tags.ToList(), formulas: null);

    public static TaskStartRequest ToStartRequest(CollectorTask task, IReadOnlyCollection<TagDescriptor> tags) =>
        ToStartRequest(task, tags, formulas: null);

    // formulas 由调用方按 TaskId 查出后传入（保持 ToStartRequest 纯映射，不在此查库）。
    public static TaskStartRequest ToStartRequest(
        CollectorTask task,
        IReadOnlyCollection<TagDescriptor> tags,
        IReadOnlyCollection<Formula>? formulas)
    {
        var protocol = (OpcProtocol)task.Type;

        var serverUri = task.Node;
        var serverProgId = task.Server;
        if (protocol == OpcProtocol.Ua && IsUaUrl(task.Server))
        {
            serverUri = task.Server;
            serverProgId = null;
        }

        // 订阅列表仅含真实 Tag（虚拟 Tag 不进订阅器）。
        // 注：tags 形参是 TagDescriptor（无 IsVirtual）；过滤在实体→Descriptor 映射处完成。
        // 此重载接收的是已映射的 Descriptor，调用方负责只传真实 Tag。
        var transform = BuildTransformConfig(task, formulas);

        return new TaskStartRequest(
            task.Id,
            protocol,
            new OpcConnectionOptions
            {
                ServerUri = serverUri,
                ServerProgId = serverProgId,
                ServerClsid = task.Clsid,
                SamplingInterval = TimeSpan.FromMilliseconds(Math.Max(task.Interval, 1)),
                DeadbandPercent = task.Deviation
            },
            task.TcpAddress,
            tags,
            transform);
    }
```

并把无参 `ToStartRequest(CollectorTask task)` 改为按实体过滤虚拟 Tag（因为它直接用 `task.Tags`）：

```csharp
    public static TaskStartRequest ToStartRequest(CollectorTask task)
    {
        // 仅真实 Tag 进订阅；虚拟 Tag 由 TransformConfig 承载其产出。
        var realTagDescs = task.Tags
            .Where(t => !t.IsVirtual)
            .Select(t => new TagDescriptor(t.Id, t.Item, t.DataType))
            .ToList();
        return ToStartRequest(task, realTagDescs, formulas: null);
    }
```

> 同时删去旧的 `ToStartRequest(CollectorTask task)` 与 `ToStartRequest(CollectorTask task, IReadOnlyCollection<TagDescriptor> tags)` 的原始实现，避免重复定义。最终保留：无参实体重载（过滤虚拟）+ Descriptor 重载（委托给三参）+ 三参重载（实际映射）。

加 `BuildTransformConfig` 私有方法：

```csharp
    // 从 task 的 Tag（含虚拟）+ Formula 组装 TransformConfig。无公式无缩放返回 null（→ 编排器用 NoOp）。
    private static TransformConfig? BuildTransformConfig(CollectorTask task, IReadOnlyCollection<Formula>? formulas)
    {
        if (formulas is null || formulas.Count == 0)
        {
            // 仍可能有缩放：若任一真实 Tag 有 ScaleFactor/Offset，需建 config。
            bool anyScale = task.Tags.Any(t => !t.IsVirtual && (t.ScaleFactor is not null || t.Offset is not null));
            if (!anyScale) return null;
        }

        var scaleByTagId = task.Tags
            .Where(t => !t.IsVirtual)
            .ToDictionary(t => t.Id, t => new ScaleConfig(t.ScaleFactor, t.Offset));
        var itemByTagId = task.Tags
            .Where(t => !t.IsVirtual)
            .ToDictionary(t => t.Id, t => t.Item);

        var formulaConfigs = (formulas ?? Array.Empty<Formula>())
            .Select(f => new FormulaConfig(
                f.Id,
                f.Name,                 // 虚拟 Tag 的 Item = 公式名
                f.Expression,
                f.Inputs.Select(i => new FormulaInputConfig(i.Alias, i.SourceTagId)).ToList()))
            .ToList();

        return new TransformConfig(scaleByTagId, itemByTagId, formulaConfigs);
    }
```

- [ ] **Step 4: StartAllAsync 加载公式并传入**

`StartAllAsync` 当前 `Include(t => t.Tags)`。在加载 tasks 之后、循环启动之前，查出本批任务的公式。把：

```csharp
        var tasks = await db.Tasks.AsNoTracking()
            .Include(t => t.Tags)
            .ToListAsync(ct).ConfigureAwait(false);
```

改为：

```csharp
        var tasks = await db.Tasks.AsNoTracking()
            .Include(t => t.Tags)
            .ToListAsync(ct).ConfigureAwait(false);

        var taskIds = tasks.Select(t => t.Id).ToList();
        var formulas = await db.Formulas.AsNoTracking()
            .Include(f => f.Inputs)
            .Where(f => taskIds.Contains(f.TaskId))
            .ToListAsync(ct).ConfigureAwait(false);
        var formulasByTask = formulas.ToLookup(f => f.TaskId);
```

并把循环内的 `await _orchestrator.StartAsync(ToStartRequest(task), ct)` 改为：

```csharp
                await _orchestrator.StartAsync(
                    ToStartRequest(task, formulasByTask[task.Id].ToList()), ct).ConfigureAwait(false);
```

> 注意：`ToStartRequest(task, List<Formula>)` 这个两参重载签名是 `(CollectorTask, IReadOnlyCollection<TagDescriptor>, IReadOnlyCollection<Formula>?)`——上面调用传的是 `(task, formulas)`，类型不符。需新增一个 `(CollectorTask, IReadOnlyCollection<Formula>)` 便捷重载，或在调用处显式构造真实 Tag Descriptor。为简洁，新增重载：

```csharp
    public static TaskStartRequest ToStartRequest(CollectorTask task, IReadOnlyCollection<Formula> formulas)
    {
        var realTagDescs = task.Tags
            .Where(t => !t.IsVirtual)
            .Select(t => new TagDescriptor(t.Id, t.Item, t.DataType))
            .ToList();
        return ToStartRequest(task, realTagDescs, formulas);
    }
```

最终 `ToStartRequest` 公开重载为：无参实体、`(task, TagDescriptor[])`、`(task, Formula[])`、四参内部 `(task, TagDescriptor[], Formula?)`。确保无重复签名。

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~DbTaskLauncherTests"`
Expected: PASS。

- [ ] **Step 6: 跑 OrchestratorEndToEndTests 回归（确认启动路径未破）**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~OrchestratorEndToEndTests"`
Expected: PASS。

- [ ] **Step 7: Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/DbTaskLauncher.cs tests/Dc.Infrastructure.Tests/Orchestration/DbTaskLauncherTests.cs
git commit -m "✨ feat(launcher): 组装 TransformConfig + 订阅列表过滤虚拟 Tag"
```

---

### Task 11: DI 注册 ITagValueTransformFactory

**Files:**
- Modify: `src/Dc.App/Composition/ServiceRegistration.cs`
- Modify: `src/Dc.App/Composition/ServiceRegistration.cs` 中 `TaskOrchestrator` 单例注册块

**Interfaces:**
- Consumes: `TagValueTransformFactory`、`TaskOrchestrator`。
- Produces: 容器可解析 `ITagValueTransformFactory`；`TaskOrchestrator` 注入之。

- [ ] **Step 1: 注册工厂 + 注入编排器**

在 `ServiceRegistration.AddDcApp` 中，`services.AddSingleton<TaskOrchestrator>(...)` 之前加：

```csharp
        services.AddSingleton<ITagValueTransformFactory, TagValueTransformFactory>();
```

并把 `TaskOrchestrator` 单例注册块：

```csharp
        services.AddSingleton<TaskOrchestrator>(sp => new TaskOrchestrator(
            sp.GetServices<IOpcSubscriberFactory>(),
            sp.GetRequiredService<IPublisherFactory>(),
            sp.GetRequiredService<OrchestratorOptions>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<TaskOrchestrator>>()));
```

改为：

```csharp
        services.AddSingleton<TaskOrchestrator>(sp => new TaskOrchestrator(
            sp.GetServices<IOpcSubscriberFactory>(),
            sp.GetRequiredService<IPublisherFactory>(),
            sp.GetRequiredService<OrchestratorOptions>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<TaskOrchestrator>>(),
            sp.GetService<ITagValueTransformFactory>()));
```

- [ ] **Step 2: 检查 Cli 是否也直接构造 TaskOrchestrator**

Run: `grep -rn "new TaskOrchestrator" src/`
Expected: 列出 `ServiceRegistration.cs` 与可能的 `src/Dc.Cli/Program.cs`。若 Cli 也直接 `new TaskOrchestrator(...)`，给它同样加 `ITagValueTransformFactory`（Cli 用 DI 则无需；若手动构造则传 `new TagValueTransformFactory()`）。

> 若 Cli 走 `ServiceRegistration.AddDcApp` 或独立 DI，确认 `ITagValueTransformFactory` 已注册即可。手动构造处补 `new TagValueTransformFactory()` 实参。

- [ ] **Step 3: 编译全解决方案**

Run: `dotnet build src/Dc.sln`
Expected: 成功，0 error。

- [ ] **Step 4: 跑全量测试回归**

Run: `dotnet test src/Dc.sln`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/Dc.App/Composition/ServiceRegistration.cs
git commit -m "✨ feat(di): 注册 ITagValueTransformFactory 并注入编排器"
```

---

### Task 12: 端到端集成测试（缩放 + 公式 → broker）

**Files:**
- Modify: `tests/Dc.Infrastructure.Tests/Orchestration/OrchestratorEndToEndTests.cs`

**Interfaces:**
- Consumes: 全部前序任务。

- [ ] **Step 1: 先读该文件确认既有端到端用例风格**

Run: `sed -n '1,60p' tests/Dc.Infrastructure.Tests/Orchestration/OrchestratorEndToEndTests.cs`
（了解它如何构造 orchestrator + Fake subscriber + 验证 published。）

- [ ] **Step 2: 写端到端用例**

在该文件加（沿用其既有构造 helper；若既有 helper 不带 transform factory，按 Task 9 的 `BuildWithTransformFactory` 模式构造）：

```csharp
    [Fact]
    public async Task EndToEnd_ScaleAndFormula_PublishesEngineeringAndVirtual()
    {
        var daFactory = new FakeOpcSubscriberFactory(OpcProtocol.Da);
        var pubFactory = new FakePublisherFactory();
        await using var orch = new TaskOrchestrator(
            new[] { (IOpcSubscriberFactory)daFactory },
            pubFactory,
            new TagValueTransformFactory());

        var cfg = new TransformConfig(
            new()
            {
                ["t1"] = new ScaleConfig(0.1, 0),   // A: 0.1x
                ["t2"] = new ScaleConfig(1.0, 0)    // B: 不变（显式 1.0 触发缩放路径）
            },
            new() { ["t1"] = "A", ["t2"] = "B" },
            new[] { new FormulaConfig("f1", "OUT", "A + B",
                new[] { new FormulaInputConfig("A", "t1"), new FormulaInputConfig("B", "t2") }) });

        await orch.StartAsync(new TaskStartRequest("e2e", OpcProtocol.Da,
            new OpcConnectionOptions { ServerUri = "opc.tcp://localhost:4840" },
            "127.0.0.1:5000",
            new[] { new TagDescriptor("t1", "A", 6), new TagDescriptor("t2", "B", 6) },
            cfg));

        var sub = daFactory.Created.First();
        var pub = pubFactory.Created.First().Publisher;

        sub.EmitValue(new TagValue("A", 100.0, 0xC0, DateTimeOffset.UtcNow)); // 工程量 10
        sub.EmitValue(new TagValue("B", 5.0, 0xC0, DateTimeOffset.UtcNow));   // 工程量 5 → OUT=15

        await WaitForAsync(() => pub.Published.Any(v => v.Item == "OUT"), TimeSpan.FromSeconds(3));

        // 真值缩放：A=100→10
        Assert.Contains(pub.Published, v => v.Item == "A" && (double)v.Value! == 10.0);
        // 虚拟值：OUT = 10 + 5 = 15
        var virt = pub.Published.Last(v => v.Item == "OUT");
        Assert.Equal(15.0, virt.Value);
        Assert.Equal(0xC0, virt.Quality);
    }
```

> 若该文件的 `WaitForAsync` 签名/位置不同，照搬其既有等待辅助。

- [ ] **Step 3: 运行确认通过**

Run: `dotnet test tests/Dc.Infrastructure.Tests/Dc.Infrastructure.Tests.csproj --filter "FullyQualifiedName~EndToEnd_ScaleAndFormula_PublishesEngineeringAndVirtual"`
Expected: PASS。

- [ ] **Step 4: 全量回归**

Run: `dotnet test src/Dc.sln`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add tests/Dc.Infrastructure.Tests/Orchestration/OrchestratorEndToEndTests.cs
git commit -m "✅ test(e2e): 缩放+公式端到端 → broker 收工程量与虚拟值"
```

---

## 自检（plan 作者已执行）

**Spec 覆盖：**
- §1 领域/持久化 → Task 1 ✓
- §2 ITagValueTransform 接口/工厂/NoOp → Task 4, 8 ✓
- §3 编排器集成（TransformConfig、pipeline Apply、Add/Remove 联动、重启复用 transform）→ Task 9 ✓（重启路径保留 transform：构造时建、`RestartIfStaleAsync` 不重建，未改该路径即天然复用 ✓）
- §4 公式求值/就绪/质量传播 → Task 6 ✓；内置函数注册 ✓；求值异常节流——本计划静默不产出（无节流日志），spec 允许"节流"为优化项，v1 静默可接受，后续可加 ✓
- §5 UI 编辑器 → **本计划不覆盖**，留作后续 UI 计划（见下）
- §6 错误处理/测试/边界 → Task 5,6,7,9,12 ✓；YAGNI 清单（跨任务、虚拟缩放、热编辑、循环依赖、内置公式库、虚拟计数）均未做 ✓
- DynamicExpresso 依赖 → Task 2 ✓

**未覆盖（明确）：** UI（Tag 编辑器公式模式、TagsViewModel 展示、引用完整性）——下一个计划。核心引擎在无 UI 时可通过 DB 直接插 Formula 行 + 跑 orchestrator/Cli 验证，自洽可测。

**类型一致性：** `ITagValueTransform.Apply/OnTagsAdded/OnTagsRemoved`、`TransformConfig(ScaleByTagId, ItemByTagId, Formulas)`、`FormulaConfig(FormulaId, OutputItem, Expression, Inputs)`、`FormulaInputConfig(Alias, SourceTagId)`、`ScaleConfig(ScaleFactor, Offset)`、`TaskStartRequest.TransformConfig`、`TaskOrchestrator` 构造第 5 参 `ITagValueTransformFactory?`——跨任务一致 ✓。

---

## 执行交接

计划已保存到 `docs/superpowers/plans/2026-06-21-virtual-tag-formula-core.md`。两种执行方式：

**1. Subagent 驱动（推荐）** — 每个 task 派发独立 subagent，task 间评审，快速迭代。

**2. 内联执行** — 在本会话用 executing-plans 逐 task 执行，带检查点。

选哪种？
