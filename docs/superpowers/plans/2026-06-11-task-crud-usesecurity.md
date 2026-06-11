# 任务 CRUD 补全 + UseSecurity 可配 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 补齐任务 CRUD 写路径（编辑持久化、安全级联删除）并把 OPC UA `UseSecurity` 做成 per-task 可配（默认 true）。

**架构：** 核心 DB 写逻辑下沉到 `Dc.Infrastructure/Persistence/TaskStore`（跨平台、可 Linux 测、合分层），App 的 `DbWorkspaceTaskSource` 委托过去。UseSecurity 走 Domain→schema 迁移→`DbTaskLauncher` 映射→编辑器复选框全链路。编辑/删除命令落 `TaskWorkspaceViewModel`，UI 用 `Click` 后置（与现有 启动/停止 一致）。

**技术栈：** .NET 8 / C#、EF Core 8（`ExecuteDeleteAsync`、`UseSnakeCaseNamingConvention`）、SQLite、xUnit、WPF-UI、CommunityToolkit.Mvvm。

---

## 关键事实（已核实，实现时遵守）

- **测试项目分裂**：`Dc.Integration.Tests` = `net8.0` 跨平台（Linux 可跑）；`Dc.App.Tests` = `net8.0-windows`+`UseWPF`（**Linux 跑不了**，office/家里 Windows 跑）。→ 风险高的 DB 逻辑放 Infrastructure 在 Linux 测；薄 VM/编辑器编排在 Dc.App.Tests（Windows）测 + 家里活体复验。
- **列名映射**：`DcDbContextFactory` 启用 `UseSnakeCaseNamingConvention()`，新属性 `UseSecurity` 自动映射 `use_security` 列，**无需** 改 `DcDbContext.OnModelCreating`。
- **EnsureCreated 不迁移旧表**：新库 `EnsureCreated` 按模型建好列；旧库靠 `DbSchemaInitializer.EnsureColumn`（`pragma_table_info` 查列 + `ALTER TABLE ADD COLUMN`）补列。
- **编辑器 `ToEntity()` 不带 `CreatedAt`**（只填 Id/Server/Node/Clsid/Type/Interval/Deviation/TcpAddress），`Id=OriginalId??""`（编辑保留 Id、新建空）。→ 更新**禁止**用 `db.Tasks.Update(task)`（会把 created_at 覆盖成 default）；必须 load-then-copy。
- **UI 按钮用 `Click=` 后置**（`OnStart`/`OnNewTask` → `Vm.XxxAsync()`），不是 Command 绑定。启动/停止/重启是**每行**按钮（DataTemplate `Grid.Column=1`），用 `SelectedTask`。
- **EF 关系全 `OnDelete(NoAction)` + `ForeignKeys=false`**：草率删留孤儿。级联删除靠应用层事务显式删。
- `OpcConnectionOptions.UseSecurity`（bool，默认 true）已存在。`OpcProtocol`：Ua/Da/Ae，`(byte)`：Ua=2。

## 文件结构

**Domain**
- 修改 `src/Dc.Domain/Entities/CollectorTask.cs` — 加 `bool UseSecurity = true`。

**Infrastructure（跨平台核心逻辑）**
- 创建 `src/Dc.Infrastructure/Persistence/TaskStore.cs` — 静态 `UpdateAsync` / `DeleteCascadeAsync`（DRY 单一来源，仿 `DbTaskLauncher` 静态助手先例）。
- 修改 `src/Dc.Infrastructure/Persistence/DbSchemaInitializer.cs` — 加 `use_security` 列迁移。
- 修改 `src/Dc.Infrastructure/Orchestration/DbTaskLauncher.cs:40` — 映射 `UseSecurity = task.UseSecurity`。

**App（Windows）**
- 修改 `src/Dc.App/ViewModels/Workspace/IWorkspaceTaskSource.cs` — 加 `UpdateTaskAsync` / `DeleteTaskCascadeAsync`。
- 修改 `src/Dc.App/ViewModels/Workspace/DbWorkspaceTaskSource.cs` — 实现委托 `TaskStore`。
- 创建 `src/Dc.App/Services/IConfirmDialog.cs` + `WpfConfirmDialog.cs` — 薄确认框。
- 修改 `src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs` — `EditSelectedAsync` / `DeleteSelectedAsync` + 注入 `IConfirmDialog` + Config 编辑收敛单一来源。
- 修改 `src/Dc.App/ViewModels/Workspace/WorkspaceConfigViewModel.cs` — `Edited` 事件改传编辑后实体。
- 修改 `src/Dc.App/ViewModels/TaskEditorViewModel.cs` — 加 `UseSecurity` + `IsUaProtocol`。
- 修改 `src/Dc.App/Views/TaskEditorWindow.xaml` — UseSecurity 复选框（仅 UA 可见）。
- 修改 `src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml` — 每行加 编辑/删除 按钮。
- 修改 `src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml.cs` — `OnEdit` / `OnDelete` 后置。
- 修改 `src/Dc.App/Composition/ServiceRegistration.cs` — 注册 `IConfirmDialog`、传入 VM。

**测试**
- 创建 `tests/Dc.Integration.Tests/Persistence/TaskStoreTests.cs`（Linux）。
- 创建 `tests/Dc.Integration.Tests/Persistence/SchemaUseSecurityTests.cs`（Linux）。
- 创建 `tests/Dc.Integration.Tests/Orchestration/DbTaskLauncherUseSecurityTests.cs`（Linux）。
- 修改 `tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs`（Windows）。

**验证命令（Linux）**：`export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj`
**Windows 测试 + 活体**：家里/office 经 dc-remote 跑 `Dc.App.Tests` + 手动复验。

---

### 任务 1：CollectorTask.UseSecurity + schema 迁移

**文件：**
- 修改：`src/Dc.Domain/Entities/CollectorTask.cs`
- 修改：`src/Dc.Infrastructure/Persistence/DbSchemaInitializer.cs:11-12`
- 测试：`tests/Dc.Integration.Tests/Persistence/SchemaUseSecurityTests.cs`（创建）

- [ ] **步骤 1：写失败测试**

创建 `tests/Dc.Integration.Tests/Persistence/SchemaUseSecurityTests.cs`：

```csharp
using Dc.Domain.Entities;
using Dc.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dc.Integration.Tests.Persistence;

public class SchemaUseSecurityTests
{
    private static DbContextOptions<DcDbContext> Options(string path) =>
        new DbContextOptionsBuilder<DcDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = false }.ToString())
            .UseSnakeCaseNamingConvention()
            .Options;

    [Fact]
    public async Task NewDb_UseSecurity_RoundTrips_DefaultTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-sec-{Guid.NewGuid():N}.db");
        try
        {
            await using (var db = new DcDbContext(Options(path)))
            {
                DbSchemaInitializer.EnsureCreated(db);
                db.Tasks.Add(new CollectorTask { Id = "t1", Server = "s", Node = "n", Type = 2 }); // 默认 UseSecurity
                db.Tasks.Add(new CollectorTask { Id = "t2", Server = "s", Node = "n", Type = 2, UseSecurity = false });
                await db.SaveChangesAsync();
            }
            await using (var db = new DcDbContext(Options(path)))
            {
                Assert.True((await db.Tasks.FindAsync("t1"))!.UseSecurity);
                Assert.False((await db.Tasks.FindAsync("t2"))!.UseSecurity);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task OldDb_WithoutColumn_GetsColumnDefaultTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-sec-old-{Guid.NewGuid():N}.db");
        try
        {
            // 模拟旧库：手建无 use_security 列的 dc_tasks，塞一行
            await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await conn.OpenAsync();
                var cmd = conn.CreateCommand();
                cmd.CommandText = @"CREATE TABLE dc_tasks (id TEXT PRIMARY KEY, server TEXT, node TEXT, clsid TEXT,
                    type INTEGER, interval INTEGER, deviation INTEGER, tcp_address TEXT, created_at TEXT, updated_at TEXT);
                    INSERT INTO dc_tasks (id, server, node, type) VALUES ('old1', 's', 'n', 2);";
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var db = new DcDbContext(Options(path)))
                DbSchemaInitializer.EnsureCreated(db); // 应补列默认 1
            await using (var db = new DcDbContext(Options(path)))
                Assert.True((await db.Tasks.FindAsync("old1"))!.UseSecurity);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **步骤 2：跑测试确认失败**

运行：`export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter SchemaUseSecurityTests`
预期：编译失败 `CollectorTask` 无 `UseSecurity`。

- [ ] **步骤 3：加 Domain 字段**

`src/Dc.Domain/Entities/CollectorTask.cs` 在 `TcpAddress` 后加：

```csharp
    public string TcpAddress { get; set; } = string.Empty;

    // OPC UA 安全连接开关。true → SelectEndpoint 选最高安全策略（需双向证书信任）；
    // false → None 端点直连。默认 true（产线安全优先，CLAUDE.md 约束）。仅 UA 生效。
    public bool UseSecurity { get; set; } = true;
```

- [ ] **步骤 4：加 schema 迁移列**

`src/Dc.Infrastructure/Persistence/DbSchemaInitializer.cs` 的 `EnsureCreated` 内，现有两行 `EnsureColumn` 后加：

```csharp
        EnsureColumn(db, "dc_tasks", "use_security", "use_security INTEGER NOT NULL DEFAULT 1");
```

- [ ] **步骤 5：跑测试确认通过**

运行：同步骤 2。预期：2 passed。

- [ ] **步骤 6：Commit**

```bash
git add src/Dc.Domain/Entities/CollectorTask.cs src/Dc.Infrastructure/Persistence/DbSchemaInitializer.cs tests/Dc.Integration.Tests/Persistence/SchemaUseSecurityTests.cs
git commit -m "✨ feat(task): CollectorTask.UseSecurity 字段 + 旧库迁移列（默认 true）"
```

---

### 任务 2：DbTaskLauncher 映射 UseSecurity

**文件：**
- 修改：`src/Dc.Infrastructure/Orchestration/DbTaskLauncher.cs:35-42`
- 测试：`tests/Dc.Integration.Tests/Orchestration/DbTaskLauncherUseSecurityTests.cs`（创建）

- [ ] **步骤 1：写失败测试**

创建 `tests/Dc.Integration.Tests/Orchestration/DbTaskLauncherUseSecurityTests.cs`：

```csharp
using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;
using Xunit;

namespace Dc.Integration.Tests.Orchestration;

public class DbTaskLauncherUseSecurityTests
{
    private static CollectorTask Task(bool useSecurity) => new()
    {
        Id = "t1", Server = "s", Node = "opc.tcp://x:1/", Type = 2,
        Interval = 1000, Deviation = 0, TcpAddress = "127.0.0.1:5000", UseSecurity = useSecurity
    };

    [Fact]
    public void ToStartRequest_MapsUseSecurity_True()
        => Assert.True(DbTaskLauncher.ToStartRequest(Task(true)).Options.UseSecurity);

    [Fact]
    public void ToStartRequest_MapsUseSecurity_False()
        => Assert.False(DbTaskLauncher.ToStartRequest(Task(false)).Options.UseSecurity);
}
```

注意：核实 `TaskStartRequest` 暴露 `Options`（`OpcConnectionOptions`）的属性名——读 `DbTaskLauncher.ToStartRequest` 的 `new(...)` 构造与 `TaskStartRequest` 定义确认（构造第 3 个参数是 `OpcConnectionOptions`）。若属性名不是 `Options`，按实际改断言。

- [ ] **步骤 2：跑测试确认失败**

运行：`~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter DbTaskLauncherUseSecurityTests`
预期：`UseSecurity` 为默认 true → True 测试通过、False 测试**失败**（当前硬编码默认 true，未映射）。

- [ ] **步骤 3：加映射**

`src/Dc.Infrastructure/Orchestration/DbTaskLauncher.cs` 的 `OpcConnectionOptions` 初始化块（约 35-42 行），在 `ServerClsid` 与 `SamplingInterval` 之间加：

```csharp
            ServerClsid = task.Clsid,
            UseSecurity = task.UseSecurity,
            SamplingInterval = TimeSpan.FromMilliseconds(Math.Max(task.Interval, 1)),
```

- [ ] **步骤 4：跑测试确认通过**

运行：同步骤 2。预期：2 passed。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.Infrastructure/Orchestration/DbTaskLauncher.cs tests/Dc.Integration.Tests/Orchestration/DbTaskLauncherUseSecurityTests.cs
git commit -m "✨ feat(task): DbTaskLauncher 映射 per-task UseSecurity"
```

---

### 任务 3：TaskStore.UpdateAsync + DeleteCascadeAsync（核心）

**文件：**
- 创建：`src/Dc.Infrastructure/Persistence/TaskStore.cs`
- 测试：`tests/Dc.Integration.Tests/Persistence/TaskStoreTests.cs`（创建）

- [ ] **步骤 1：写失败测试**

创建 `tests/Dc.Integration.Tests/Persistence/TaskStoreTests.cs`：

```csharp
using Dc.Domain.Entities;
using Dc.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dc.Integration.Tests.Persistence;

public class TaskStoreTests
{
    private static DbContextOptions<DcDbContext> Options(string path) =>
        new DbContextOptionsBuilder<DcDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = false }.ToString())
            .UseSnakeCaseNamingConvention()
            .Options;

    private static string Seed(out DbContextOptions<DcDbContext> opts)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-store-{Guid.NewGuid():N}.db");
        opts = Options(path);
        using var db = new DcDbContext(opts);
        DbSchemaInitializer.EnsureCreated(db);
        db.Tasks.Add(new CollectorTask { Id = "task1", Server = "炉温", Node = "opc.tcp://x", Type = 2, Interval = 1000 });
        db.Groups.Add(new Group { Id = "g1", Name = "组A", TaskId = "task1" });
        db.Groups.Add(new Group { Id = "g2", Name = "组B", TaskId = "task1" });
        db.Tags.Add(new Tag { Id = "tag1", Item = "i1", TaskId = "task1", GroupId = "g1" });
        db.Tags.Add(new Tag { Id = "tag2", Item = "i2", TaskId = "task1", GroupId = "g2" });
        db.Tags.Add(new Tag { Id = "tag3", Item = "i3", TaskId = "task1", GroupId = "" });
        // 另一任务的子项，必须保留不受影响
        db.Tasks.Add(new CollectorTask { Id = "other", Server = "s", Node = "n", Type = 2 });
        db.Groups.Add(new Group { Id = "go", Name = "其它", TaskId = "other" });
        db.Tags.Add(new Tag { Id = "tago", Item = "io", TaskId = "other", GroupId = "go" });
        db.SaveChanges();
        return path;
    }

    [Fact]
    public async Task UpdateAsync_PersistsFields_PreservesCreatedAt_AdvancesUpdatedAt()
    {
        var path = Seed(out var opts);
        try
        {
            DateTime created;
            await using (var db = new DcDbContext(opts))
                created = (await db.Tasks.FindAsync("task1"))!.CreatedAt;

            // 编辑器产出的实体：带 Id、改了字段、CreatedAt 为 default（关键陷阱）
            var edited = new CollectorTask { Id = "task1", Server = "新名", Node = "opc.tcp://y",
                Type = 2, Interval = 2000, Deviation = 5, TcpAddress = "1.2.3.4:9", UseSecurity = false };

            await using (var db = new DcDbContext(opts))
                await TaskStore.UpdateAsync(db, edited);

            await using (var db = new DcDbContext(opts))
            {
                var t = await db.Tasks.FindAsync("task1");
                Assert.Equal("新名", t!.Server);
                Assert.Equal(2000, t.Interval);
                Assert.False(t.UseSecurity);
                Assert.Equal(created, t.CreatedAt);        // CreatedAt 未被 default 覆盖
                Assert.True(t.UpdatedAt >= created);        // UpdatedAt 前进
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task DeleteCascadeAsync_RemovesTaskGroupsTags_NoOrphans_LeavesOtherTask()
    {
        var path = Seed(out var opts);
        try
        {
            await using (var db = new DcDbContext(opts))
                await TaskStore.DeleteCascadeAsync(db, "task1");

            await using (var db = new DcDbContext(opts))
            {
                Assert.Null(await db.Tasks.FindAsync("task1"));
                Assert.Equal(0, await db.Groups.CountAsync(g => g.TaskId == "task1"));
                Assert.Equal(0, await db.Tags.CountAsync(t => t.TaskId == "task1"));
                // 其它任务完好
                Assert.NotNull(await db.Tasks.FindAsync("other"));
                Assert.Equal(1, await db.Groups.CountAsync(g => g.TaskId == "other"));
                Assert.Equal(1, await db.Tags.CountAsync(t => t.TaskId == "other"));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **步骤 2：跑测试确认失败**

运行：`~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter TaskStoreTests`
预期：编译失败 `TaskStore` 不存在。

- [ ] **步骤 3：实现 TaskStore**

创建 `src/Dc.Infrastructure/Persistence/TaskStore.cs`：

```csharp
using Dc.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dc.Infrastructure.Persistence;

// 任务写路径单一来源（WPF App 与测试共用，跨平台）。仿 DbTaskLauncher 静态助手先例。
public static class TaskStore
{
    // 更新可编辑字段。禁用 db.Tasks.Update(task)：编辑器实体 CreatedAt=default，
    // 全量 Modified 会覆盖 created_at。改 load-then-copy 只改可编辑列，CreatedAt 保留、
    // UpdatedAt 由 DcDbContext.ApplyAutoFields 自动刷。任务不存在则静默返回。
    public static async Task UpdateAsync(DcDbContext db, CollectorTask task)
    {
        var existing = await db.Tasks.FirstOrDefaultAsync(t => t.Id == task.Id);
        if (existing is null) return;
        existing.Server = task.Server;
        existing.Node = task.Node;
        existing.Clsid = task.Clsid;
        existing.Type = task.Type;
        existing.Interval = task.Interval;
        existing.Deviation = task.Deviation;
        existing.TcpAddress = task.TcpAddress;
        existing.UseSecurity = task.UseSecurity;
        await db.SaveChangesAsync();
    }

    // 安全删除：一个事务里按 task_id 删 tags → groups → task。tag 同时挂 task 与 group，
    // 按 task_id 删可一网打尽（含分组下的），不留孤儿。EF 关系是 NoAction + ForeignKeys=false，
    // 故必须显式删子项。
    public static async Task DeleteCascadeAsync(DcDbContext db, string taskId)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Tags.Where(t => t.TaskId == taskId).ExecuteDeleteAsync();
        await db.Groups.Where(g => g.TaskId == taskId).ExecuteDeleteAsync();
        await db.Tasks.Where(t => t.Id == taskId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }
}
```

- [ ] **步骤 4：跑测试确认通过**

运行：同步骤 2。预期：2 passed。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.Infrastructure/Persistence/TaskStore.cs tests/Dc.Integration.Tests/Persistence/TaskStoreTests.cs
git commit -m "✨ feat(task): TaskStore 更新(保留 CreatedAt) + 事务级联删除(零孤儿)"
```

---

### 任务 4：IWorkspaceTaskSource + DbWorkspaceTaskSource 委托

**文件：**
- 修改：`src/Dc.App/ViewModels/Workspace/IWorkspaceTaskSource.cs`
- 修改：`src/Dc.App/ViewModels/Workspace/DbWorkspaceTaskSource.cs`

> 说明：`DbWorkspaceTaskSource` 在 net8.0-windows 项目，无 Linux 单测；逻辑已在任务 3 的 `TaskStore`（Linux）覆盖，此处仅薄委托。VM 编排测试在任务 6（Windows）。

- [ ] **步骤 1：扩接口**

`IWorkspaceTaskSource.cs` 在 `SaveNewTaskAsync` 后加（保持 `GetCountsAsync` 的默认实现风格）：

```csharp
    /// <summary>Persist edits to an existing task (preserves CreatedAt).</summary>
    Task UpdateTaskAsync(CollectorTask task);

    /// <summary>Delete a task and cascade-delete its groups and tags in one transaction.</summary>
    Task DeleteTaskCascadeAsync(string taskId);
```

- [ ] **步骤 2：实现委托**

`DbWorkspaceTaskSource.cs` 在 `SaveNewTaskAsync` 后加：

```csharp
    public async Task UpdateTaskAsync(CollectorTask task)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await TaskStore.UpdateAsync(db, task);
    }

    public async Task DeleteTaskCascadeAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await TaskStore.DeleteCascadeAsync(db, taskId);
    }
```

确保文件顶部 `using Dc.Infrastructure.Persistence;` 已在（`UlidGenerator`/`DcDbContext` 已引该命名空间）。

- [ ] **步骤 3：编译确认**

运行（Linux 可编译该项目依赖链，但 Dc.App 是 windows —— 改在 Windows 编译；Linux 仅确认 Infrastructure/测试不破）：
`~/.dotnet/dotnet build src/Dc.Infrastructure/Dc.Infrastructure.csproj -c Debug`
预期：build succeeded。（Dc.App 编译留 Windows 阶段/任务 8 后。）

- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/ViewModels/Workspace/IWorkspaceTaskSource.cs src/Dc.App/ViewModels/Workspace/DbWorkspaceTaskSource.cs
git commit -m "✨ feat(task): IWorkspaceTaskSource 加 Update/DeleteCascade（委托 TaskStore）"
```

---

### 任务 5：IConfirmDialog + WpfConfirmDialog

**文件：**
- 创建：`src/Dc.App/Services/IConfirmDialog.cs`
- 创建：`src/Dc.App/Services/WpfConfirmDialog.cs`

> 仿现有 `ITaskEditorDialog` + `TaskEditorDialog` 模式（接口在 Services、WPF 实现同目录）。无单测（薄 MessageBox 包装），由任务 6 的 VM 测试用 fake 覆盖编排。

- [ ] **步骤 1：建接口**

`src/Dc.App/Services/IConfirmDialog.cs`：

```csharp
namespace Dc.App.Services;

public interface IConfirmDialog
{
    /// <summary>Show a yes/no confirmation. Returns true if the user confirms.</summary>
    bool Confirm(string title, string message);
}
```

- [ ] **步骤 2：建 WPF 实现**

`src/Dc.App/Services/WpfConfirmDialog.cs`：

```csharp
using System.Windows;

namespace Dc.App.Services;

public sealed class WpfConfirmDialog : IConfirmDialog
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes;
}
```

- [ ] **步骤 3：编译确认**（Windows 阶段统一编译，此处仅文件就位）

- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/Services/IConfirmDialog.cs src/Dc.App/Services/WpfConfirmDialog.cs
git commit -m "✨ feat(ui): 薄 IConfirmDialog 确认框（WPF MessageBox）"
```

---

### 任务 6：TaskWorkspaceViewModel 编辑/删除命令 + Config 收敛

**文件：**
- 修改：`src/Dc.App/ViewModels/Workspace/WorkspaceConfigViewModel.cs`
- 修改：`src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs`（修改，Windows 跑）

- [ ] **步骤 1：写失败测试（Windows）**

在 `TaskWorkspaceViewModelTests.cs` 的 `FakeTaskSource` 加跟踪 + 新建用例。先扩 `FakeTaskSource`：

```csharp
        public List<CollectorTask> Updated { get; } = new();
        public List<string> Deleted { get; } = new();

        public Task UpdateTaskAsync(CollectorTask task) { Updated.Add(task); return Task.CompletedTask; }
        public Task DeleteTaskCascadeAsync(string taskId) { Deleted.Add(taskId); return Task.CompletedTask; }
        public Task<(int Groups, int Tags)> GetCountsAsync(string taskId) => Task.FromResult((2, 5));
```

加 `FakeConfirm`（在测试类内）+ 用例。注意核实 `Deps` builder 怎么构造 VM、`SelectedTask` 怎么设、`_orchestrator` 是否可注入（`TaskOrchestrator?` 可空——删运行中任务的「先停」断言可能需真/mock orchestrator；若 builder 未传 orchestrator，先覆盖「确认→删除」「取消→不删」「编辑→UpdateTaskAsync」三条核心，运行中先停留作 Windows 活体复验）：

```csharp
    private sealed class FakeConfirm : Dc.App.Services.IConfirmDialog
    {
        public bool Result = true;
        public int Calls;
        public bool Confirm(string title, string message) { Calls++; return Result; }
    }

    [Fact]
    public async Task DeleteSelected_Confirmed_CallsCascadeDelete()
    {
        var src = new FakeTaskSource { Tasks = { Task1("a") } };
        var confirm = new FakeConfirm { Result = true };
        var vm = BuildVm(src, confirm: confirm);          // 核实/扩展 BuildVm 支持传 confirm
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks.First();
        await vm.DeleteSelectedAsync();
        Assert.Equal(1, confirm.Calls);
        Assert.Equal(new[] { "a" }, src.Deleted);
    }

    [Fact]
    public async Task DeleteSelected_Cancelled_DoesNotDelete()
    {
        var src = new FakeTaskSource { Tasks = { Task1("a") } };
        var confirm = new FakeConfirm { Result = false };
        var vm = BuildVm(src, confirm: confirm);
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks.First();
        await vm.DeleteSelectedAsync();
        Assert.Empty(src.Deleted);
    }

    [Fact]
    public async Task EditSelected_NonNullResult_PersistsUpdate()
    {
        var src = new FakeTaskSource { Tasks = { Task1("a") } };
        var editor = new EditorReturning(Task1("a", server: "改后"));   // 核实/加：返回非空实体的 fake editor
        var vm = BuildVm(src, editor: editor);
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks.First();
        await vm.EditSelectedAsync();
        Assert.Single(src.Updated);
        Assert.Equal("改后", src.Updated[0].Server);
    }
```

`EditorReturning`：

```csharp
    private sealed class EditorReturning(CollectorTask result) : Dc.App.Services.ITaskEditorDialog
    {
        public CollectorTask? Edit(CollectorTask? existing) => result;
    }
```

> 实现者注意：阅读现有 `BuildVm`/`Deps`（测试文件 90 行起）确认其签名，按需加可选 `confirm`/`editor` 形参，**不破坏现有用例**。

- [ ] **步骤 2：跑测试确认失败**

Windows（office/家里经 dc-remote）：`dotnet test tests/Dc.App.Tests/... --filter "DeleteSelected|EditSelected"`
预期：编译失败（VM 无 `EditSelectedAsync`/`DeleteSelectedAsync`、构造无 confirm 形参）。

- [ ] **步骤 3：Config 事件改传实体**

`WorkspaceConfigViewModel.cs`：把 `event Action<string>? Edited;` 改为 `event Action<CollectorTask>? Edited;`，`Edit()` 改：

```csharp
    public event Action<CollectorTask>? Edited;

    private void Edit()
    {
        if (_task is null) return;
        var result = _editor.Edit(_task);
        if (result is not null) Edited?.Invoke(result);
    }
```

- [ ] **步骤 4：VM 加命令 + 收敛持久化**

`TaskWorkspaceViewModel.cs`：
1. 构造加 `IConfirmDialog? confirm = null` 形参，存 `_confirm`（null → 用 `AlwaysConfirm` 空对象返回 true，便于测试默认）。实际：`private readonly IConfirmDialog _confirm;` 赋 `confirm ?? new AlwaysYesConfirm();`，并加私有 `AlwaysYesConfirm` 空对象。
2. 构造内 Config 订阅改为单一持久化来源：
   `Config.Edited += async edited => await PersistEditedAsync(edited);`
3. 加方法：

```csharp
    private async Task PersistEditedAsync(Dc.Domain.Entities.CollectorTask edited)
    {
        await _source.UpdateTaskAsync(edited);
        await LoadAsync();
    }

    public async Task EditSelectedAsync()
    {
        if (SelectedTask is null || _editor is null) return;
        var task = _tasksById.GetValueOrDefault(SelectedTask.TaskId);
        if (task is null) return;
        var edited = _editor.Edit(task);
        if (edited is null) return;
        await PersistEditedAsync(edited);
    }

    public async Task DeleteSelectedAsync()
    {
        if (SelectedTask is null) return;
        var id = SelectedTask.TaskId;
        var name = SelectedTask.Name;

        // 运行中先停（自动）
        if (_orchestrator is not null && _orch.RunningTaskIds.Contains(id))
            await _orchestrator.StopAsync(id);

        var (g, t) = await _source.GetCountsAsync(id);
        if (!_confirm.Confirm("删除任务",
                $"将删除任务「{name}」及其 {g} 个分组、{t} 个 Tag，不可恢复。确定删除？"))
            return;

        await _source.DeleteTaskCascadeAsync(id);
        SelectedTask = null;
        await LoadAsync();
    }
```

加空对象类：

```csharp
    private sealed class AlwaysYesConfirm : Services.IConfirmDialog
    {
        public bool Confirm(string title, string message) => true;
    }
```

（确认顶部 `using Dc.App.Services;` 或用全限定 `Services.IConfirmDialog`。）

- [ ] **步骤 5：跑测试确认通过**（Windows）

预期：3 新用例 + 既有用例全 pass。

- [ ] **步骤 6：Commit**

```bash
git add src/Dc.App/ViewModels/Workspace/WorkspaceConfigViewModel.cs src/Dc.App/ViewModels/Workspace/TaskWorkspaceViewModel.cs tests/Dc.App.Tests/ViewModels/Workspace/TaskWorkspaceViewModelTests.cs
git commit -m "✨ feat(task): 编辑持久化 + 安全删除命令（确认+先停+级联）"
```

---

### 任务 7：TaskEditorViewModel UseSecurity + IsUaProtocol

**文件：**
- 修改：`src/Dc.App/ViewModels/TaskEditorViewModel.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/TaskEditorViewModelTests.cs`（核实是否存在；存在则加用例，否则创建）

- [ ] **步骤 1：写失败测试（Windows）**

核实 `tests/Dc.App.Tests/ViewModels/TaskEditorViewModelTests.cs` 是否存在。加：

```csharp
    [Fact]
    public void NewTask_UseSecurity_DefaultsTrue()
        => Assert.True(new TaskEditorViewModel().UseSecurity);

    [Fact]
    public void Edit_RoundTripsUseSecurity_AndToEntity()
    {
        var existing = new CollectorTask { Id = "a", Server = "s", Node = "n", Type = 2, UseSecurity = false,
            Interval = 1000, TcpAddress = "1.2.3.4:5" };
        var vm = new TaskEditorViewModel(existing, Array.Empty<IOpcBrowserFactory>());
        Assert.False(vm.UseSecurity);
        Assert.False(vm.ToEntity().UseSecurity);
    }

    [Fact]
    public void IsUaProtocol_TracksProtocol()
    {
        var vm = new TaskEditorViewModel();           // 默认 Ua
        Assert.True(vm.IsUaProtocol);
        vm.Protocol = OpcProtocol.Da;
        Assert.False(vm.IsUaProtocol);
    }
```

- [ ] **步骤 2：跑测试确认失败**（Windows）—— VM 无 `UseSecurity`/`IsUaProtocol`。

- [ ] **步骤 3：实现**

`TaskEditorViewModel.cs`：
1. 加字段（放 `_tcpAddress` 后）：

```csharp
    [ObservableProperty] private bool _useSecurity = true;   // 仅 UA 生效，默认安全
```

2. 加计算属性（在 `IsClassicOpcProtocol` 旁）：

```csharp
    public bool IsUaProtocol => Protocol == OpcProtocol.Ua;
```

3. `OnProtocolChanged` 加通知：

```csharp
    partial void OnProtocolChanged(OpcProtocol value)
    {
        OnPropertyChanged(nameof(IsDaProtocol));
        OnPropertyChanged(nameof(IsClassicOpcProtocol));
        OnPropertyChanged(nameof(IsUaProtocol));
    }
```

4. 构造 existing 分支读：`_useSecurity = existing.UseSecurity;`（放 `_tcpAddress = existing.TcpAddress;` 后）。
5. `ToEntity()` 加 `UseSecurity = UseSecurity,`（放 `TcpAddress` 后）。

- [ ] **步骤 4：跑测试确认通过**（Windows）

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/ViewModels/TaskEditorViewModel.cs tests/Dc.App.Tests/ViewModels/TaskEditorViewModelTests.cs
git commit -m "✨ feat(task): 编辑器 UseSecurity 字段 + IsUaProtocol 可见性开关"
```

---

### 任务 8：XAML 入口 + DI 接线（Windows 编译验证）

**文件：**
- 修改：`src/Dc.App/Views/TaskEditorWindow.xaml`
- 修改：`src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml`
- 修改：`src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml.cs`
- 修改：`src/Dc.App/Composition/ServiceRegistration.cs`

> 此任务无单测（纯 UI/接线），验证 = Windows 编译通过 + 任务 9 活体。实现者须先读这三个 XAML/cs 现状再改。

- [ ] **步骤 1：编辑器加 UseSecurity 复选框**

读 `TaskEditorWindow.xaml` 结构，在合适位置（采样/TCP 字段附近）加，仅 UA 可见：

```xml
<CheckBox Content="使用安全连接（推荐）"
          IsChecked="{Binding UseSecurity}"
          Visibility="{Binding IsUaProtocol, Converter={StaticResource BoolToVisibility}}"
          Margin="0,8,0,0" />
```

核实项目里布尔→可见性 Converter 的实际 key（grep `BooleanToVisibility`/`BoolToVis` 资源；WPF-UI/项目可能已有。没有则用现有模式，或代码后置切换）。

- [ ] **步骤 2：任务行加 编辑/删除 按钮**

`TaskWorkspaceView.xaml` 的每行按钮组（`Grid.Column="1"` 的 StackPanel，启动/停止/重启所在，约 199-249 行）末尾、`⟳ 重启` 之后加：

```xml
                    <Button Content="✎ 编辑" Margin="6,0,6,0" Click="OnEdit"
                            Style="{StaticResource DcBtnGhostSm}" />
                    <Button Content="🗑 删除" Click="OnDelete"
                            Style="{StaticResource DcBtnGhostSm}" />
```

（与启动/停止不同，编辑/删除不随 IsRunning 切换可见，常驻。删除按钮如有警示色样式可换用。）

- [ ] **步骤 3：后置 OnEdit/OnDelete**

`TaskWorkspaceView.xaml.cs` 仿 `OnStart` 加：

```csharp
    private async void OnEdit(object s, System.Windows.RoutedEventArgs e)
    {
        try { if (Vm is { } v) await v.EditSelectedAsync(); }
        catch (Exception ex) { Log.Error(ex, "EditSelected failed"); }
    }

    private async void OnDelete(object s, System.Windows.RoutedEventArgs e)
    {
        try { if (Vm is { } v) await v.DeleteSelectedAsync(); }
        catch (Exception ex) { Log.Error(ex, "DeleteSelected failed"); }
    }
```

注意：每行按钮用 `SelectedTask`——核实点击行内按钮时该行已选中（现有启动/停止同模式已工作，应无碍；若不确定，读 XAML 看行选中绑定）。

- [ ] **步骤 4：DI 注册 IConfirmDialog + 传入 VM**

`ServiceRegistration.cs`：核实 `IConfirmDialog` 注册 + `TaskWorkspaceViewModel` 构造处怎么组装（grep `TaskWorkspaceViewModel(` 与 `ITaskEditorDialog` 注册位置），加：

```csharp
// 注册（仿 ITaskEditorDialog 那行）
services.AddSingleton<IConfirmDialog, WpfConfirmDialog>();
```

并在构造 `TaskWorkspaceViewModel` 处把 `IConfirmDialog` 传入新增的 `confirm` 形参。

- [ ] **步骤 5：Windows 编译验证**

家里/office 经 dc-remote：
`dotnet build src/Dc.App/Dc.App.csproj -c Release -p:Platform=x64 -p:CustomTestTarget=net8.0-windows`
预期：build succeeded，0 错误。

- [ ] **步骤 6：Commit**

```bash
git add src/Dc.App/Views/TaskEditorWindow.xaml src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml.cs src/Dc.App/Composition/ServiceRegistration.cs
git commit -m "✨ feat(ui): 工具栏 编辑/删除 入口 + 编辑器安全开关 + DI 接线"
```

---

### 任务 9：全量验证 + 家里活体复验

**文件：** 无（验证任务）

- [ ] **步骤 1：Linux 全量回归**

运行：`export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj`
预期：全绿（含新增 TaskStore/Schema/DbTaskLauncher 用例）。

- [ ] **步骤 2：Windows 单测回归**（office/家里 dc-remote）

运行：`dotnet test tests/Dc.App.Tests/Dc.App.Tests.csproj -p:Platform=x64`
预期：全绿（含新增编辑/删除/UseSecurity VM 用例）。

- [ ] **步骤 3：家里真 server 活体复验**（dc-remote，需家里 Prosys UA）

逐项截图确认：
1. 编辑现有任务改字段 → 重启任务 → 确认新参数生效（日志/诊断）。**（验「改不了」已修）**
2. 删除一个有分组/Tag 的任务 → 确认确认框显示正确数量 → 删后任务消失、`/metrics` 无该 task、DB 查 `dc_groups`/`dc_tags` 无该 task_id 残留。**（验「删不了」+ 零孤儿）**
3. 删除运行中任务 → 确认先停再删、不报错。
4. 新建 UA 任务勾选「使用安全连接」→ 打安全端点（需证书，预期 BadSecurityChecksFailed/证书流程）；取消勾选 → 打 None 端点直连成功。**（验 UseSecurity 可配）**
5. DA/AE 任务编辑器**不显示**安全开关。

- [ ] **步骤 4：更新 memory**

把 `dc-ua-live-validation-findings` 中本特性已修的项标注「已修复（feat/task-crud-usesecurity）」。

---

## 自检结果

**规格覆盖度**：① 编辑持久化→任务 3(TaskStore.UpdateAsync)+4+6；② 安全删除(级联/确认/先停)→任务 3(DeleteCascade)+5+6；③ UseSecurity(Domain/schema/映射/编辑器)→任务 1+2+7+8；④ 工具栏入口→任务 8。测试(Linux DB/迁移/映射 + Windows VM/编辑器 + 活体)→任务 1-3、6-7、9。全覆盖。

**占位符扫描**：无 TODO/待定；所有步骤含完整代码。少数「核实」标注（TaskStartRequest.Options 属性名、BuildVm 签名、BoolToVisibility 资源 key、ServiceRegistration 组装、TaskEditorViewModelTests 是否存在）是**必要的现状确认点**而非占位——实现者读现有文件即得，已指明确切位置。

**类型一致性**：`TaskStore.UpdateAsync/DeleteCascadeAsync`、`IWorkspaceTaskSource.UpdateTaskAsync/DeleteTaskCascadeAsync`、`IConfirmDialog.Confirm`、`CollectorTask.UseSecurity`、`TaskEditorViewModel.UseSecurity/IsUaProtocol`、`WorkspaceConfigViewModel.Edited(Action<CollectorTask>)` 跨任务一致。

**已知边界**：`DbWorkspaceTaskSource`/VM 编排在 net8.0-windows，Linux 无单测——核心风险逻辑已下沉 TaskStore 在 Linux 覆盖，编排靠 Windows 单测 + 活体。删运行中任务「先停」断言若 builder 无法注入 orchestrator，降级为活体复验（任务 6 步骤 1 已注明）。
