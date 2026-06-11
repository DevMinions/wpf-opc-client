# 任务上下文内建分组/Tag —— 隐藏冗余归属选择器 设计规格

- 日期：2026-06-11
- 范围：Dc.App（WPF）。分组编辑器、Tag 编辑器两个弹窗的归属选择器，在有上下文时隐藏锁定。
- 目标用户视角：工程师从某个任务的工作台进来建分组/Tag —— 归属理应是"当前任务/分组"，不该再选一次。

## 1. 背景与问题

现状（已核实代码）：
- `GroupsViewModel.NewGroup` 已把当前任务 `TaskFilter` 作为 `defaultTask` 传入编辑器；`GroupEditorViewModel` 也已 `_task = defaultTask ?? AvailableTasks.FirstOrDefault()` 预选。
- `TagsViewModel.NewAsync` 已把当前分组 `GroupFilter` 作为 `defaultGroup` 传入；`TagEditorViewModel` 也已 `_group = defaultGroup ?? AvailableGroups.FirstOrDefault()` 预选。

所以归属**已预填**。真正的摩擦：两个弹窗仍显示一个**可编辑的归属下拉框**（分组的"所属任务"、Tag 的"所属分组"），让人感觉"又要选一次任务/分组"，弹窗像通用表单而非"在【当前上下文】下新建"。

## 2. 目标与非目标

**目标**
- 有归属上下文时，编辑器**隐藏**归属选择器、锁定为该上下文（新建与编辑均如此）。
- 当前上下文写进**弹窗标题**（任务名/分组名），一眼可知"建在哪"。

**非目标（YAGNI）**
- 不支持跨任务移动分组 / 跨分组移动 Tag（编辑时也不显示归属选择器）。需要时另立项。
- 不改采集/持久化逻辑，纯弹窗展示层。

## 3. 决策（已与用户确认）
- 隐藏并锁定归属选择器；新建与编辑一致。
- 上下文写进标题。
- Tag 在无分组上下文（直接点 Tag tab 未选分组的少见情况）回退显示选择器。

## 4. 分组编辑器

文件：`src/Dc.App/ViewModels/GroupEditorViewModel.cs`、`src/Dc.App/Views/GroupEditorWindow.xaml`

- VM 新增：`public bool ShowTaskSelector => Task is null;`
  - 正常路径 `Task` 恒非空（分组 tab 必属某任务；新建传 `TaskFilter`、编辑取 `existing.TaskId`）→ `false` → 隐藏。
  - 仅当极端无任务上下文（防御）才 `true` → 显示选择器。
- VM 标题带上下文（任务显示用 `Server`，与弹窗下拉框 ItemTemplate 的主字段一致）：
  - 新建：`$"新建分组 · 任务：{Task?.Server}"`
  - 编辑：`$"编辑分组 · 任务：{Task?.Server}"`
  - 标题在构造里 `_task` 确定之后再拼。无任务上下文时退化为 `"新建分组"`/`"编辑分组"`（配合显示的选择器）。
- XAML：把"所属任务:"标签（`Grid.Row=1 Col=0`）与 ComboBox（`Grid.Row=1 Col=1`）的 `Visibility` 绑 `ShowTaskSelector`（经 `BoolToVis`）。**该 Grid 第 2 个 `RowDefinition` 现为 `Height="36"`，须改为 `Height="Auto"`**——否则两元素 Collapsed 后行仍占 36px 留空隙；改 Auto 后隐藏时收缩为 0、显示时按内容高度。
- `ToEntity()`/`Validate()` 不变：`Task` 仍来自锁定上下文。

## 5. Tag 编辑器

文件：`src/Dc.App/ViewModels/TagEditorViewModel.cs`、`src/Dc.App/Views/TagEditorWindow.xaml`

- VM 改构造：新建分支 `_group = defaultGroup ?? AvailableGroups.FirstOrDefault();` 改为 **`_group = defaultGroup;`**（无上下文时留 `null`，不自作主张选第一个）。编辑分支不变（`_group` 取自 `existing.GroupId`）。
- VM 新增：`public bool ShowGroupSelector => Group is null;`
  - 有分组上下文（`GroupFilter` 已传 / 编辑已有分组）→ `Group` 非空 → `false` → 隐藏锁定。
  - 无分组上下文（直接点 Tag tab 未选分组）→ `Group` 为 `null` → `true` → 显示选择器让用户选。
- VM 标题带上下文（分组显示用 `Name`）：
  - 有分组：新建 `$"新建 Tag · 分组：{Group?.Name}"`、编辑 `$"编辑 Tag · 分组：{Group?.Name}"`
  - 无分组：退化 `"新建 Tag"`（配合显示的选择器）
- XAML：把"所属分组:"标签（`Grid.Row=2 Col=0`）与 ComboBox（`Grid.Row=2 Col=1`）`Visibility` 绑 `ShowGroupSelector`；**该 Grid 第 3 个 `RowDefinition` 现为 `Height="36"`，须改为 `Height="Auto"`**（同上，隐藏时收缩、不留空隙）。
- `TagsViewModel.NewAsync` 现有"`AvailableGroups` 为空 → 提示先建分组"的守卫**保留**。
- `Validate()` 已含 `if (Group is null) errors.Add("必须选择所属分组")`（已核实）——移除兜底后，无上下文且用户未选时保存被拦，安全，无需改 Validate。

## 6. 错误处理与边界
- 归属选择器隐藏时，归属 100% 来自锁定上下文，不可能为空（除防御性回退路径）。
- Tag 无分组上下文 → 选择器可见 + Validate 要求选分组，保证不会建出无归属的 Tag。
- 纯展示层，采集/持久化零改动。

## 7. 测试方案
- **VM 单测**（`Dc.App.Tests`）：
  - GroupEditorViewModel：给 `defaultTask` → `ShowTaskSelector==false`、标题含任务 `Server`、`ToEntity().TaskId` 等于该任务；不给 `defaultTask` 且无 existing → `ShowTaskSelector==true`。编辑（给 existing）→ `ShowTaskSelector==false`、标题"编辑分组"。
  - TagEditorViewModel：给 `defaultGroup` → `ShowGroupSelector==false`、标题含分组 `Name`、`ToEntity().GroupId` 等于该分组；不给 `defaultGroup` 且无 existing → `Group==null`、`ShowGroupSelector==true`。
- **视觉验证**（dc-remote）：seed 一个任务 + 一个分组，截图新建分组弹窗（无任务下拉、标题带任务名）、新建 Tag 弹窗（无分组下拉、标题带分组名）。
- 全量回归：`dc-remote home test`（Dc.App.Tests 现 88 通过，加新测后应仍全绿）。

## 8. 涉及文件
- 修改：`ViewModels/GroupEditorViewModel.cs`、`Views/GroupEditorWindow.xaml`、`ViewModels/TagEditorViewModel.cs`、`Views/TagEditorWindow.xaml`
- 新增测试：`tests/Dc.App.Tests/ViewModels/GroupEditorViewModelTests.cs`、`TagEditorViewModelTests.cs`

## 9. 验收标准
- 从任务工作台新建/编辑分组：无"所属任务"下拉，标题为"新建分组 · 任务：X"。
- 选中分组后新建/编辑 Tag：无"所属分组"下拉，标题为"新建 Tag · 分组：Y"。
- 直接点 Tag tab 未选分组时新建 Tag：仍显示分组下拉，可选后保存。
- 保存后归属正确（分组挂在当前任务、Tag 挂在当前分组）。
- 全量测试通过；两个弹窗截图复核通过。
