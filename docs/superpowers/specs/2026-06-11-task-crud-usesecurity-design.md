# 任务 CRUD 补全 + UseSecurity 可配 设计

> 2026-06-11。direction 2「打磨」首个特性。源于家里真 Prosys UA server 活体验证心跳修复时暴露的一组真 bug（见 memory `dc-ua-live-validation-findings`）。

## 目标

补齐任务 CRUD 的写路径——编辑可持久化、删除安全（级联清子项）——并把 OPC UA `UseSecurity` 做成 per-task 可配（默认仍 true）。

## 背景：已探明现状

- **新建**可用：`TaskWorkspaceViewModel.NewTaskAsync` → `_editor.Edit(null)` → `_source.SaveNewTaskAsync`。
- **编辑不持久化**：编辑入口藏在「选中任务 → Config 子页签 → 编辑」；`WorkspaceConfigViewModel.Edit()` 只 `Edited?.Invoke(id)`，`DbWorkspaceTaskSource` **无 UpdateTaskAsync**，改动从不写回 DB。
- **删除完全缺失**：无命令、无数据源方法、UI 无入口。
- **级联风险**：EF 关系全 `OnDelete(DeleteBehavior.NoAction)`（task→groups、task→tags、group→tags），连接串 `ForeignKeys=false`。草率 `Remove(task)` 不报错但留孤儿 group/tag。
- **UseSecurity 不可配**：`OpcConnectionOptions.UseSecurity` 默认 true；`dc_tasks` 无 `use_security` 列；`TaskEditorWindow`/编辑器无开关；`DbTaskLauncher.ToStartRequest` 不映射（硬编码默认 true）。

数据模型：`CollectorTask{Server,Node,Clsid?,Type,Interval,Deviation,TcpAddress}` + 导航 `List<Group> Groups` / `List<Tag> Tags`；`Group{Name,TaskId,Tags}`；`Tag{Item,DataType,TaskId,GroupId}`。`Type`：1=DA 2=UA 3=AE。schema 迁移走 `DbSchemaInitializer.EnsureColumn`（已有 `pragma_table_info` 查列 + `ALTER TABLE ADD COLUMN` 模式）。

## 已定决策

| 维度 | 决策 |
|---|---|
| 删除关联子项 | **级联删除**：一个事务里删 task + 其所有 group + 所有 tag；删前弹确认框显示数量 |
| 删运行中任务 | **确认在前、副作用在后（as-built 改进）**：先 `GetCounts`+确认框，确认后才 `StopAsync`+级联删。取消 = 真 no-op（不停不删），比原设计「先停再确认」更安全 |
| 漏接确认框 | **fail-safe 拒绝（as-built）**：VM 默认兜底 `DenyConfirm`（返回 false），漏接 DI 时删不掉而非无确认静默删；生产经 DI 注入 `WpfConfirmDialog` |
| 编辑/删除入口 | **工具栏按钮**，与现有 启动/停止/重启 并列 |
| UseSecurity 默认 | **仍 true**（CLAUDE.md 安全约束：不为图方便默认关）；旧库迁移列默认 1 |
| UseSecurity 适用面 | **仅 UA（Type==2）显示开关**；DA/AE 无此概念、忽略 |
| 编辑运行中任务 | 改动持久化但**不热加载**，下次启动/重启生效（确认/提示注明） |

## 组件设计

### ① 编辑持久化

- `IWorkspaceTaskSource` + `DbWorkspaceTaskSource` 新增 `Task UpdateTaskAsync(CollectorTask task)`：
  `db.Tasks.Update(task); await db.SaveChangesAsync();`（`DcDbContext.ApplyAutoFields` 自动刷 `UpdatedAt`）。
- `TaskWorkspaceViewModel` 新增 `EditSelectedCommand` → `EditSelectedAsync`：
  取选中任务完整实体（`_tasksById` 或 `GetTaskWithTagsAsync`）→ `_editor.Edit(task)` → 非空 → `UpdateTaskAsync(result)` → `LoadAsync()`。
- `WorkspaceConfigViewModel` 的编辑按钮改走同一持久化路径（不再空转）：把保存逻辑收敛到 `TaskWorkspaceViewModel`，Config 的 `EditCommand` 委托过去，或 Config 的 `Edited` 事件处理器内执行 `UpdateTaskAsync`。**单一保存来源**，避免两条编辑路径分叉。

### ② 安全删除（级联 + 确认 + 先停）

- `IWorkspaceTaskSource` + `DbWorkspaceTaskSource` 新增 `Task DeleteTaskCascadeAsync(string taskId)`：
  开 `db.Database.BeginTransactionAsync()` → `ExecuteDeleteAsync`（或 `RemoveRange`）按 `TaskId` 删 `dc_tags` → `dc_groups` → 删 `dc_tasks` 该行 → commit。tag 同时挂 task 与 group，按 `task_id` 删可覆盖全部该任务的 tag（含分组下的），不留孤儿。
- `TaskWorkspaceViewModel` 新增 `DeleteSelectedCommand` → `DeleteSelectedAsync`（**as-built：确认在前、副作用在后**）：
  1. `var (g,t) = await _source.GetCountsAsync(id)`。
  2. 弹确认框：「将删除任务 {name} 及其 {g} 个分组、{t} 个 tag，不可恢复。」**取消即 return —— 真 no-op，不停不删**（比原设计「先停再确认」更安全：取消不中断运行中任务）。
  3. 确认后：若 `_orch.RunningTaskIds` 含该 id 且 `_orchestrator` 非空 → `await _orchestrator.StopAsync(id)`。
  4. `DeleteTaskCascadeAsync(id)` → `SelectedTask=null` → `LoadAsync()`。
  - VM 默认确认兜底用 **fail-safe `DenyConfirm`**（返回 false）：漏接 DI 时删不掉而非无确认静默删；生产经 `ServiceRegistration` 注入 `WpfConfirmDialog`。
- 确认框：先核实是否已有对话框/确认服务可复用；无则加一个极薄的 `IConfirmDialog.Confirm(title, message) : bool`（WPF `MessageBox` 实现 + 测试用 stub）。

### ③ UseSecurity 可配

- **Domain**：`CollectorTask` 加 `public bool UseSecurity { get; set; } = true;`
- **Schema**：`DbSchemaInitializer.EnsureCreated` 加
  `EnsureColumn(db, "dc_tasks", "use_security", "use_security INTEGER NOT NULL DEFAULT 1");`
- **DbContext 映射**：确保 `UseSecurity` ↔ `use_security` 列（核实 DcDbContext 的列名约定——其它属性如 `TaskId`↔`task_id` 怎么映射的；若靠 snake_case 约定则自动，否则显式 `HasColumnName`）。
- **DbTaskLauncher**：`ToStartRequest` 的 `OpcConnectionOptions` 加 `UseSecurity = task.UseSecurity`。
- **编辑器**：`TaskEditorWindow.xaml` + 其 VM 加复选框「使用安全连接（推荐）」绑 `UseSecurity`，默认勾选；仅当协议为 UA 时可见/启用（DA/AE 隐藏）。

### ④ 工具栏入口

- `src/Dc.App/Views/Workspace/TaskWorkspaceView.xaml`：在启动/停止/重启按钮一排加「编辑」「删除」，绑 `EditSelectedCommand` / `DeleteSelectedCommand`，`IsEnabled` 随选中任务（`SelectedTask is not null`）。删除按钮可用警示色。

## 错误处理

- 确认框取消 = no-op，不触碰 DB / orchestrator。
- 级联删除走事务，全有或全无；任一步失败回滚，任务与子项保持一致。
- 编辑/删除按钮在无选中任务时禁用。
- 编辑运行中任务：持久化成功但运行实例仍用旧参数，提示「下次启动/重启生效」。

## 测试

**Linux 跨平台 `Dc.Integration.Tests`（`export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Integration.Tests/...`）：**

1. `UpdateTaskAsync` 改字段 → 重新加载 → 断言新值持久化、`UpdatedAt` 前进。
2. `DeleteTaskCascadeAsync`：seed 任务 + 2 分组 + 5 tag → 删 → 断言 `dc_tasks`/`dc_groups`/`dc_tags` 中该 task_id 行全为 0（零孤儿）。
3. 删运行中任务：起真任务（或 mock orchestrator）→ DeleteSelected → 断言先 `StopAsync` 再删、运行集不含该 id。
4. UseSecurity round-trip：建 `UseSecurity=false` 任务 → 重载断言 false → `DbTaskLauncher.ToStartRequest` 得 `OpcConnectionOptions.UseSecurity==false`；默认建任务断言 true。
5. 迁移：用无 `use_security` 列的旧 schema DB → `EnsureCreated` → 断言列已加、既有行值=1。

**家里真 Prosys server 活体复验（dc-remote）：** 编辑任务字段并确认重启后生效；删除任务确认彻底消失且无孤儿 group/tag；`UseSecurity` 勾选 → 打安全端点（需双向证书），取消 → 打 None 端点直连。

## 范围外（YAGNI）

- 安全策略/消息模式细粒度选择（None/Basic256Sha256、Sign/SignAndEncrypt）——现仅 bool，够用。
- 编辑热加载（运行中改参数即时生效）——下次启动生效即可。
- 软删除/回收站——级联硬删 + 确认框已满足。
- 切 EF Cascade + 打开 ForeignKeys——本特性用应用层事务级联，不动全局 FK 开关（避免波及其它路径）。
