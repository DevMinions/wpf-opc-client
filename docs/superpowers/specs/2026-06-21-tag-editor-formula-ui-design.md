# Tag 编辑器缩放/公式 UI 设计

日期：2026-06-21
分支：feat/ui-interaction-polish
关联：核心引擎设计 `2026-06-21-virtual-tag-formula-design.md`（已实现并真机验证）

## 背景

核心引擎（缩放 + 公式虚拟测点）已完成并通过真机测试。引擎的运行路径（orchestrator transform、DbTaskLauncher 组装、WPF 启动路径过滤虚拟 Tag + 加载公式）均已就绪。**唯一缺失的是用户创建/编辑缩放与虚拟测点的 UI**——目前 Tag 编辑器只有 Item/数据类型/分组，没有缩放字段，也没有任何公式入口。虚拟 Tag 只能通过直接写 DB 创建（真机测试即如此）。

本设计补齐这块 UI：扩展 Tag 编辑器，让用户在界面里配置真实 Tag 的缩放、以及创建/编辑虚拟测点（公式）。

## 关键决策（brainstorming 已定）

| 决策点 | 选择 |
|---|---|
| Q1 编辑器返回数据 | 扩展返回 `TagEditResult`（Tag + Formula? + Inputs[]），编辑器出数据、VM 管持久化（A） |
| Q2 真实/虚拟切换 | 编辑器内一个开关"虚拟测点(公式计算)"，互斥显示两套字段（A） |
| Q3 公式输入映射 UX | 表达式优先：用户写表达式，系统提取变量名，为每个变量选同任务真实 Tag（C） |
| Q4 输入 Tag 范围 | 仅同任务真实 Tag（排除自身虚拟）（A） |
| Q5 删除引用完整性 | 删被引用的真实 Tag → 拦截 + 列出引用公式；删虚拟 Tag → 级联删公式（A） |
| Q6 虚拟 Tag 的 Item | 由 Formula.Name 派生，用户不直接编辑 Item（A） |
| Q7 缩放字段可见性 | 仅真实模式显示缩放；虚拟模式不缩放（A） |
| Q8 公式变更热同步 | 不热应用，持久化 + 提示"重启任务后生效"（A） |
| 布局 | 单卡片，开关切换两套互斥字段集（方案 1） |

## §1 契约：TagEditResult 与 ITagEditorDialog

### TagEditResult

`src/Dc.App/Services/TagEditResult.cs`：
```csharp
public sealed record TagEditResult(
    Tag Tag,
    Formula? Formula,              // 虚拟 Tag 时非空；真实 Tag 为 null
    IReadOnlyList<FormulaInput> Inputs);  // 虚拟 Tag 时为公式输入；真实 Tag 为空
```

### ITagEditorDialog.Edit 签名

```csharp
TagEditResult? Edit(
    IEnumerable<Group> availableGroups,
    Tag? existing,
    IReadOnlyCollection<Tag>? taskTags = null,            // 同任务全部 Tag（含虚拟），供输入选择 + 唯一性校验
    IReadOnlyCollection<Formula>? existingFormulas = null,// 编辑虚拟 Tag 时回填的公式
    Group? defaultGroup = null,
    Func<string, CollectorTask?>? taskLookup = null);
```
- `taskTags`：编辑器用来 (a) 填输入下拉（同任务真实 Tag，排除自身虚拟）；(b) 校验 Formula.Name 任务内唯一（排除自身）。调用方（TagsViewModel）从 DB 加载该任务全部 Tag 传入。
- `existingFormulas`：编辑已存在虚拟 Tag 时回填其 Formula + Inputs。新建时 null。
- 两个新参可选，保证现有调用方编译；返回类型 `Tag?`→`TagEditResult?`。

### TagEditorDialog 实现

构造 VM 注入 `IFormulaValidator`，传 `taskTags`/`existingFormulas`，开窗，返回 `vm.ToResult()`（替代 `vm.ToEntity()`）。

## §2 TagEditorViewModel 状态与模式

### 新增字段/属性

- `IsVirtual : bool` — 开关。编辑时从 `existing.IsVirtual` 回填；新建默认 false。
- 真实模式专属：`ScaleFactor : string`、`Offset : string`（字符串输入，留空=不缩放，保存时解析为 `double?`）。
- 虚拟模式专属：
  - `FormulaName : string` — 公式名（= 虚拟 Tag 的 Item，Q6）。
  - `Expression : string` — 表达式。
  - `OutputUnit : string` — 输出单位（可选）。
  - `AvailableInputTags : ObservableCollection<Tag>` — 同任务真实 Tag（排除自身虚拟），供输入选择（Q4 A）。
  - `InputBindings : ObservableCollection<InputBindingRow>` — 每行 `[Alias(只读) | SelectedTag(下拉)]`，随表达式重算（§3）。

### InputBindingRow（嵌套 VM）

```csharp
public sealed class InputBindingRow : ObservableObject
{
    public string Alias { get; }              // 从表达式提取，只读
    [ObservableProperty] private Tag? _selectedTag;
}
```

### 模式切换

勾选/取消 `IsVirtual` 时，XAML 两套面板互斥显示（§4）。切到虚拟模式：清空真实专属字段记忆、从表达式提取输入；切回真实模式：清空虚拟专属字段。开关未保存前不影响 DB。

### 构造

新增参数：`IReadOnlyCollection<Tag>? taskTags`、`IReadOnlyCollection<Formula>? existingFormulas`、`IFormulaValidator? validator`（DI 注入，表达式校验）。`AvailableInputTags` = `taskTags.Where(t => !t.IsVirtual && t.Id != existing?.Id)`。编辑虚拟 Tag 时，从 `existingFormulas` 找 `OutputTagId == existing.Id` 的公式，回填 Name/Expression/OutputUnit，并在初始 `InputBindings` 按 `Formula.Inputs` 预选 Tag。

### ToResult()（替代 ToEntity()）

返回 `TagEditResult`：
- 真实 Tag：Tag 带缩放字段（ScaleFactor/Offset 解析），Formula=null，Inputs=空。
- 虚拟 Tag：Tag（`IsVirtual=true`、`Item=FormulaName`），Formula（`Name=FormulaName`、Expression、`OutputTagId=Tag.Id`、TaskId、OutputUnit），Inputs（从 `InputBindings` 映射 `Alias→SelectedTag.Id`）。

## §3 表达式变量提取（Q3 C）

### 提取方法

`TagEditorViewModel.ExtractAliases`：正则 `[A-Za-z_][A-Za-z0-9_]*` 扫描表达式标识符，排除内置函数名（SQRT/SIN/COS/TAN/ASIN/ACOS/ATAN/ABS/LOG/LOG10/EXP/POW/MIN/MAX/ROUND/FLOOR/CEILING/IF/AVG/SUM）与常量（PI/E），去重保序（首次出现顺序）。

> 排除集在 VM 内硬编码为一份 `static readonly HashSet<string>`（与 Infrastructure 的 `FormulaBuiltins` 内容一致但独立）。`FormulaBuiltins` 是 Infrastructure `internal static`，App 层 VM 引不到；VM 不依赖它。两份集合需人工保持同步（函数集稳定，风险低）。最终语义正确性由 `IFormulaValidator.Parse` 兜底（它内部用真实的 `FormulaBuiltins`）。

### 触发时机

表达式 TextBox `TextChanged`（提取很轻，无需防抖）。每次变化：
1. 重新提取别名集合。
2. 对比当前 `InputBindings`：保留仍存在别名的已选 Tag，移除消失的别名，追加新别名（SelectedTag=null）。
3. 用户正为某变量选 Tag 时，表达式一改导致该变量消失 → 该行移除（需重配）。

### 校验耦合

`Validate()`（§5）调 `IFormulaValidator.Validate(expression, aliasToDataType)`，`aliasToDataType` 从 `InputBindings` 的 `SelectedTag.DataType` 构建。未选 Tag 的变量视为未定义，校验报错。表达式里出现的变量必须都有对应行 + 都选了 Tag。

### 边界

正则可能误把函数名当变量（已排除注册集）；公式基本无字符串字面量（数值表达式场景）。`IFormulaValidator` 的 `Parse` 是最终兜底；正则提取只用于生成输入行 UI。

## §4 XAML 布局（单卡片，开关切换）

`TagEditorWindow.xaml` 在现有卡片内重组。

### 顶部开关（所有模式可见）

CheckBox 绑定 `IsVirtual`，文本"虚拟测点(公式计算)"。

### 真实面板（IsVirtual == false，BoolToVis）

- Item + 浏览（现有）
- 数据类型 ComboBox（现有）
- ScaleFactor、Offset 两个 TextBox（新增，并排；Placeholder"留空=不缩放"）
- 所属分组（现有，共享）

### 虚拟面板（IsVirtual == true，BoolToVis）

- 名称（FormulaName）TextBox — Placeholder"任务内唯一，作为虚拟测点标识"
- 表达式（Expression）TextBox — 多行，Placeholder"如 T * 1.8 + 32"
- 输出单位（OutputUnit）TextBox（可选）
- 输入映射 `ItemsControl`，`ItemsSource=InputBindings`，每行模板：`[Alias(只读 TextBlock, 宽 60)] [选 Tag ComboBox(同任务真实 Tag, 显示 Item)]`。行随表达式增减，**不放删除按钮**。
- 所属分组（现有，共享）

### 分组选择器位置

开关下方、模式面板之下，两种模式都显示（共享）。

### 卡片尺寸

宽度从 460 调到 ~520；高度随内容自适应（固定宽，高 Auto）。

### 样式复用

沿用 `DcBtnPrimarySm`/`DcBtnGhostSm`、`TextFillColor*Brush`、`BoolToVis` 转换器、`controls:Placeholder`。输入映射行用与真实字段一致的 label/value 风格。

### code-behind 不变

`OnSaveClick` 仍调 `vm.Validate()` + `MessageDialog` 报错；`DialogResult=true`。

## §5 校验（Validate）

返回 `IReadOnlyList<string>`（沿用现有签名）。

### 通用（两种模式）

- 所属分组必选（现有）。

### 真实模式（!IsVirtual）

- Item 必填（现有）。
- ScaleFactor/Offset 若非空，必须 `double.TryParse`(InvariantCulture) → 否则"缩放系数/偏移量必须是数字"。

### 虚拟模式（IsVirtual）

- `FormulaName` 必填，任务内唯一（查 `taskTags` 其他虚拟 Tag 的 Item/公式名，排除自身）→ 否则"公式名不能为空"/"公式名在任务内已存在"。
- `Expression` 必填。
- 每个提取出的变量（InputBindings 每行）必须选了 Tag → 否则"变量 {Alias} 未选择输入测点"。
- 调 `IFormulaValidator.Validate(expression, aliasToDataType)`，`aliasToDataType` 从 `InputBindings` 的 `SelectedTag.DataType` 构建：
  - 通过 → OK。
  - 失败 → 透传错误（"表达式无效:..."/"输入 T 的数据类型不可数值化"）。
  - 含义：表达式变量必须都在 InputBindings（Q3 提取保证一致）；选的 Tag 数据类型必须可数值化（validator NumericTypeCodes 把关 String/DateTime）。

### 校验不通过

code-behind `OnSaveClick` 把 errors 用 `MessageDialog` 列出，不关窗（现有行为）。

## §6 TagsViewModel：创建/编辑/删除 + 引用完整性

### 加载任务 Tag（供编辑器）

`NewAsync`/`EditAsync` 前从 DB 加载该任务全部 Tag + 若编辑虚拟 Tag 则加载该任务公式（Include Inputs），传给 `ITagEditorDialog.Edit`。

### NewAsync（新建）

- `result = _editor.Edit(availableGroups, null, taskTags, null, GroupFilter, taskLookup)`。
- 生成 ULID：`result.Tag.Id`；若虚拟，`result.Formula.Id`、各 `FormulaInput.Id` 也生成；`Formula.OutputTagId = result.Tag.Id`，`Formula.TaskId`/Inputs 关联填好。
- 事务写：`db.Tags.Add(tag)`；若虚拟，`db.Formulas.Add(formula)`（EF 级联加 Inputs）。
- `SaveChangesAsync`。
- 列表刷新行（ToRow）。
- 热同步：真实 Tag 走现有 `TryHotAddAsync`；虚拟 Tag 不订阅（Q8 A：不热加）。任务运行中新建虚拟 Tag → 弹提示"虚拟测点已保存,重启任务后生效"。

### EditAsync（编辑）

- 加载现有实体（Tag + 若虚拟则其 Formula/Inputs）。
- `Edit(...)` 回填；返回 TagEditResult。
- 同步字段到现有实体：`Item/DataType/GroupId/TaskId/ScaleFactor/Offset/IsVirtual`。
- 公式变更：若虚拟，删旧 Formula+Inputs（`db.Formulas.Remove(old)`）加新（`db.Formulas.Add(new)`）。从虚拟改真实（开关允许）→ 删 Formula。
- 保存。
- 热同步：真实 Tag 的 Item/Task 变更走现有 `TryHotRemove`+`TryHotAdd`；虚拟/公式变更不热同步，任务运行中提示重启。
- 虚拟 Tag 的 Item=FormulaName，改名=Item 变，但虚拟不订阅，无需热同步。

### DeleteAsync（删除）

- **引用完整性（Q5 A）**：删前查 `db.FormulaInputs.Where(i => i.SourceTagId == tag.Id)`。命中 → 取对应 Formula 名，弹 `MessageDialog`"被公式 {names} 引用,请先修改公式或删除对应虚拟测点"，**阻止删除**，return。
- 删虚拟 Tag：**级联删**其 Formula+Inputs（`db.Formulas.RemoveRange(OutputTagId==tag.Id 的公式)`，EF 级联删 Inputs），再 `db.Tags.Remove(tag)`。
- 删真实 Tag：现有 `ExecuteDelete` + `TryHotRemove`。
- 列表移除行。

### TagEditorDialog 实现

构造 VM 注入 `IFormulaValidator`（从 DI），传 `taskTags`/`existingFormulas`，返回 `vm.ToResult()`。

### DI

`ServiceRegistration` 注册 `IFormulaValidator` → `FormulaValidator`（Infrastructure 层实现，App 可引用）。`TagEditorDialog` 构造加 `IFormulaValidator` 参数。

## §7 测试

### 前置：修复 stale FakeGroupPanel

`tests/Dc.App.Tests` 当前有预先存在的编译错（`FakeGroupPanel` 缺 `IEmbeddableGroupPanel.NavigateToTasksRequested`）。实现时顺手补该接口成员让项目能编——这是已有 stale fake 的修复，非本设计新功能。

### TagEditorViewModelTests 扩展

真实模式：
- 新建真实 Tag，填 ScaleFactor="0.1"/Offset="-5" → `ToResult().Tag.ScaleFactor==0.1`、`Formula==null`。
- ScaleFactor="abc" → Validate 含"缩放"错误。
- 缩放留空 → `ToResult().Tag.ScaleFactor==null`。

虚拟模式：
- 勾 IsVirtual，填 Name="Doubled"、Expression="T * 2"，taskTags 含真实 Tag T → InputBindings 提取 ["T"]。
- 选 T → `ToResult().Formula.Expression=="T * 2"`、`Inputs[0].Alias=="T"`、`Inputs[0].SourceTagId==T.Id`、`Tag.IsVirtual==true`、`Tag.Item=="Doubled"`。
- Name 留空 → Validate 含"公式名"错误。
- Name 与 taskTags 已有虚拟 Tag 同名 → Validate 含"已存在"。
- 表达式变量未选 Tag → Validate 含"未选择输入"。
- 表达式含未注册变量（`U + 1` 但只选了 T）→ 提取出 U 行，未选 → 报错。
- 表达式函数名不被当变量 → `ExtractAliases("SQRT(T)")` == ["T"]。
- 输入选 String 型 Tag → Validate 含"数值化"错误（validator 拒绝）。

表达式变量提取：
- `"T * 1.8 + P / (T + 273.15)"` → ["T","P"]（去重保序）。
- 表达式变化保留已配选择：先 `"T"` 配 T，改成 `"T + P"` → T 行保留已选，P 行新增 null。

### TagsViewModel 引用完整性

`TagsViewModel` 直接依赖 `IDbContextFactory`，DB 集成测试较重。若现有无 DB 集成测试先例，本项用 `TagEditorViewModel` 单测覆盖契约，`TagsViewModel` 引用完整性靠真机/UI 验证（本项目已有 UIA 真机测试惯例）。

### 真机/UI 验证

沿用 UIA 驱动 + Prosys：
- 新建虚拟测点（Name=Sum、Expression="Random + Counter"、选两个真实 Tag）→ 保存 → 任务运行 → 实时数据出现 Sum 行。
- 删除被引用的真实 Tag → 弹拦截提示。

## 依赖

- `IFormulaValidator` / `FormulaValidator`（核心引擎 Task 3 已实现，Infrastructure 层，公开接口，App 可引用）。
- 内置函数/常量排除集：VM 内硬编码一份（与 Infrastructure `FormulaBuiltins` 内容一致，见 §3 注）。
- 无新 NuGet 包。
