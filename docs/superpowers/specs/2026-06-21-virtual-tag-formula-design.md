# 虚拟测点与公式引擎 + Tag 缩放 设计

日期：2026-06-21
分支：feat/ui-interaction-polish（后续可另开实现分支）

## 背景

借鉴 `TaylorBoys/IndustrialDataCollector`（WPF + OPC UA/Modbus 工业采集），引入两块我们缺失的能力：

1. **Tag 缩放（ScaleFactor + Offset）**：真实 Tag 出值时把原始值映射为工程量（4-20mA → 工程量、单位换算）。工业现场最常见需求，改动小、无新依赖。
2. **公式引擎（虚拟测点）**：一个虚拟 Tag 的值 = 公式（同任务内若干真实 Tag 的工程量值），如温压补偿流量、热量、压损。需表达式求值、跨 Tag 取值、就绪门控。

设计原则：缩放与公式分层（缩放产出工程量，公式消费工程量产出衍生量）；虚拟 Tag 与真实 Tag 出口同构（都进 UI 实时值/诊断/broker）。

## 关键决策（brainstorming 已定）

| 决策点 | 选择 |
|---|---|
| Q1 范围 | 缩放 + 公式 都做 |
| Q2 公式取值时机 | 事件驱动 + 就绪门控（C）：所有输入至少到过一次 Good 才产出，之后任一输入变化即算 |
| Q3 虚拟值出口 | 与真实 Tag 完全同构（A）：UI 实时值 + 诊断 + broker 全走 |
| Q4 公式引用方式 | 别名引用 + 别名→TagId 映射表（C）：公式可读、引用精确 |
| Q5 输入作用域 | 仅同任务内引用（A） |
| Q6 就绪/质量 | 必须 Good 才就绪；Bad 输入时产出 Bad 虚拟值（质量传播，B） |
| Q7 求值库 | DynamicExpresso（A） |
| Q8 缩放与公式关系 | 缩放只作用于真实 Tag，公式输入用工程量；虚拟 Tag 不缩放（A） |
| 实现落点 | 独立 `ITagValueTransform` 组件，编排器持有（方案 2） |

## §1 领域模型与持久化

### Tag 实体扩展（真实 Tag）

```
ScaleFactor : double?    // 可空，空=不缩放
Offset      : double?    // 可空
IsVirtual   : bool       // 显式判别真实/虚拟
```

缩放公式：`engineering = raw * (ScaleFactor ?? 1) + (Offset ?? 0)`。仅对数值型 Tag 生效；非数值（String/Bool）忽略缩放，原值透传。

虚拟 Tag 不进订阅器（无真实节点）。但 `TagValue` 仅以 `Item` 标识（broker 线格式 + UI 键），故虚拟 Tag 必须有唯一 `Item`：编辑器把虚拟 Tag 的 `Item` 设为 `Formula.Name`，并强制 `Formula.Name` 在任务内唯一。UI 展示也用 `Formula.Name`。

### Formula 实体（虚拟 Tag 的公式定义）

```
Id            : string (ULID)
Name          : string          // 可读名，如"温压补偿流量"
Expression    : string          // DynamicExpresso 表达式，如 "Q * SQRT(P/101.325 * 293.15/(T+273.15))"
OutputTagId   : string          // 产出的虚拟 Tag 的 Id（一对一）
OutputUnit    : string?
```

### FormulaInput 实体（别名映射表）

```
FormulaId   : string
Alias       : string    // 公式里的变量名，如 "T"、"P"、"Q"
SourceTagId : string    // 引用的真实 Tag Id（必须同任务）
```

复合主键 `(FormulaId, Alias)`。同任务约束在应用层校验（`SourceTagId` 所属 task == Formula 所属 task）。

虚拟 Tag 与 Formula 一对一：删除虚拟 Tag 连带删 Formula + FormulaInput。不单独做 Formula 管理页（YAGNI）。

### 持久化

EF Core `DcDbContext` 加 `Formulas` / `FormulaInputs` DbSet + 迁移；`Tags` 表加 `ScaleFactor`/`Offset`/`IsVirtual` 列。虚拟 Tag 行也进 `Tags` 表（`IsVirtual=1`）。

### 订阅组装约束

`TagDescriptor` 不变（只描述订阅）。`TaskStartRequest.Tags` 在组装时**仅含真实 Tag**——`DbTaskLauncher.ToStartRequest` 与 WPF 启动路径过滤掉 `IsVirtual` Tag。`TaskRuntime.Tags` 词典保留全部（含虚拟），供就绪判定/反查表用。

## §2 ITagValueTransform 接口与职责

文件：`src/Dc.Infrastructure/Orchestration/ITagValueTransform.cs`

```csharp
public interface ITagValueTransform
{
    // 处理一个真实 Tag 的原始值，返回该批应发布/上抛的所有值（缩放后真值 + 触发算出的虚拟值）。
    // 顺序：先真值，后虚拟值（若有）。空集合表示该真值被丢弃。
    IReadOnlyList<TagValue> Apply(TagValue raw);

    void OnTagsAdded(IEnumerable<TagDescriptor> tags);
    void OnTagsRemoved(IEnumerable<string> tagItems);
}
```

输入约定：只接收**真实 Tag** 的原始值（编排器保证虚拟值不回流进 Apply）。返回的 `TagValue` 用 `Item` 区分——真值用原 Item，虚拟值用虚拟 Tag 的 `Item`（即 `Formula.Name`，任务内唯一）。

### 职责边界（单一职责，可独立测）

1. **缩放**：对真值套 `ScaleFactor/Offset`，产出工程量 `TagValue`（质量位透传原值）。非数值型原值透传。
2. **就绪门控 + 公式求值**：维护"输入 Tag Id → 它喂的公式列表"反查表 + 每公式的"各输入最新值 + 是否已就绪"状态。真值到来时更新输入槽 → 若公式已就绪，用各输入最新工程量值重算 → 产出虚拟 `TagValue`，质量 = 所有输入中最差者。未就绪不产出。
3. **Interpreter 复用**：DynamicExpresso `Interpreter` 编译并缓存表达式（按 `FormulaId` 缓存 `Lambda`），运行期只 `Eval`。启动时构建。

### 不负责

连接/重连/看门狗（编排器）、发布（编排器拿返回值 publish）、UI。transform 是无状态求值器 + 内部就绪状态，纯同步、单线程调用（编排器在 pipeline 线程内串行调用，无锁需求）。

### 生命周期

每 task 一个 transform 实例，在 `TaskRuntime` 持有，task 启动时由工厂按该 task 的 `Formula`+`FormulaInput`+真实 Tag 缩放配置构建。`AddTags/RemoveTags` 时同步更新 transform 的反查表与缩放配置（在编排器已有 `_mutationLock` 内，串行）。

### 工厂

```csharp
public interface ITagValueTransformFactory
{
    // 无公式且无缩放时返回 NoOp 实现以零开销。
    ITagValueTransform Create(string taskId, TransformConfig config);
}
```

`TransformConfig` 是从 DB 加载的快照（Formula 列表 + 每个 Tag 的缩放 + Item↔TagId 映射），由 `DbTaskLauncher`/WPF 启动路径组装。

`NoOpTransform`：无公式无缩放时返回，`Apply` 直接返回单元素数组（真值透传），零额外开销，保证不拖慢现有无公式 task。

## §3 编排器集成与数据流

### TaskRuntime 扩展

加 `ITagValueTransform? Transform` 字段。task 启动时构建（无公式无缩放则为 `NoOpTransform`）。

### TaskStartRequest 扩展

加 `TransformConfig? TransformConfig` 字段（task 的静态配置，与 `Tags`/`OpcOptions` 同类，放请求里最自然，启动路径单参数不变）。

### pipeline 改动（RunPipelineAsync 真值 handler）

```
ConsumeAsync(rt.Subscriber.TagValues, async v =>
{
    Interlocked.Increment(ref rt.ValueCount);
    rt.LastValueAt = DateTimeOffset.UtcNow;
    var outputs = rt.Transform.Apply(v);          // 新增
    foreach (var o in outputs)
    {
        TagValueReceived?.Invoke(rt.TaskId, o);
        try { await rt.Publisher.PublishAsync(o, ct); }
        catch (OperationCanceledException) { throw; }
        catch { Interlocked.Increment(ref rt.PublishErrorCount); continue; }
        Interlocked.Increment(ref rt.PublishSuccessCount);
    }
}, ct);
```

`ValueCount` 语义保持为**原始真值到达数**（诊断关心的是收到的原始值速率）。发布成功/失败计数按实际发布条数累加（一条真值可能触发多条发布：真值 + N 个虚拟值）。

### AddTags/RemoveTags 联动

只接受真实 Tag 的增删（虚拟 Tag 不通过此路径增删，改公式需停 task 重启）。

- `AddTags`：新增真实 Tag → 调 `transform.OnTagsAdded` 更新缩放配置/反查表。
- `RemoveTags`：移除真实 Tag → 调 `transform.OnTagsRemoved`。若移除的是某公式输入 Tag → 该公式转 `Failed`，停止产出虚拟值（就绪门控天然处理：输入被移除=该输入不会再有新值，公式冻结）。一次性 INFO 日志。
- transform 暴露 `OnTagsAdded`/`OnTagsRemoved` 供编排器在 `_mutationLock` 内调用。

### 重启路径（RestartIfStaleAsync）

重连用 `req.Tags`（原始请求里的真实 Tag 列表），transform 实例**保留不动**（就绪状态、Interpreter 缓存有效，虚拟配置未变）。重连只换订阅器，transform 跨重连复用——方案 2 的额外好处。

### 诊断

`TaskDiagnostics.Tags.Count` 是全部 Tag（含虚拟）。暂不加 `VirtualTagCount`（YAGNI），必要时再补。

## §4 公式求值与就绪门控细节

### DynamicExpresso 用法

- 启动时按公式解析表达式得到可复用的编译结果（DynamicExpresso 的 `Lambda`），缓存之，运行期只求值，避免每拍重解析。具体 API 在实现时确认。
- `Interpreter` 配置：`InterpreterOptions.Default`（不开 `AllowReflection`），禁反射。
- **内置函数**：在 `Interpreter` 构建时用 `SetFunction("SQRT", ...)` 注册 `SQRT/SIN/COS/TAN/ASIN/ACOS/ATAN/ABS/LOG/LOG10/EXP/POW/MIN/MAX/ROUND/FLOOR/CEILING/IF/AVG/SUM` 及常量 `PI`/`E`（对齐 IndustrialDataCollector 函数集，现场熟悉）。用 `Func<double,...>` 委托注册。
- 变量注入：每拍用各输入工程量值构造 `Parameter[]` 传值，类型统一 `double`。

### 输入值取数

公式输入用真实 Tag 的**工程量值**（缩放后），非原始值（Q8 A）。transform 内部对每个输入 Tag 维护"最新工程量值 + 质量"。

### 非数值输入处理

公式参数统一 `double`。Bool→`1.0/0.0`；String 型 Tag **不允许作为公式输入**（编辑器校验拒绝）。运行期若某输入值无法转 double（异常情况），视为该输入 Bad。

### 就绪门控状态机（每公式一份）

```
state: NotReady | Ready | Failed
inputs: dict<Alias, (double value, ushort quality, bool seenGood)>
```

- 初始 `NotReady`。
- 某输入真值到来 → 更新该 Alias 槽（`value`=工程量，`quality`=传播后质量，若 IsGood 则 `seenGood=true`）。
- `NotReady` 且**所有**输入 `seenGood==true` → 转 `Ready`，并**立即算一次**（用各输入当前最新值），产出虚拟值。
- `Ready` 后任一输入变化 → 重算，产出虚拟值，质量 = 各输入最差者。
- 输入变 Bad 不冻结、不停算——仍用其最新值算，但输出质量标 Bad（传播）。保证下游知道"此刻虚拟值不可信"而非"冻结陈旧好值"。`Ready` 后某输入暂未到新值时，用其**上一次**最新值算。
- `Failed`：`OnTagsRemoved` 移除了某输入 → 该公式转 `Failed`，停止产出。重启 task 或重新配置公式才恢复。

### 质量传播计算

输出 `Quality` = 输入质量中最差者：
- 任一输入 `IsBad` → 输出 Bad（`0x00`）
- 否则任一 `IsUncertain` → 输出 Uncertain（`0x40`）
- 否则 Good（`0xC0`）

时间戳取触发本次计算的输入值时间戳。

### 求值异常

公式抛异常（除零、NaN、类型错）→ 不产出虚拟值，记节流警告（每公式每 60s 一条），公式状态保持 `Ready`（下次输入变化再试）。避免坏公式刷爆日志。

## §5 UI 与编辑器

### Tag 编辑器扩展（TagEditorViewModel / TagEditorWindow）

- 加缩放字段：`ScaleFactor`、`Offset`（可空 double 输入，留空=不缩放）。仅真实 Tag 显示。
- 加 `IsVirtual` 切换：勾选"虚拟测点（公式）"后：
  - 禁用 Item/Browse/DataType 输入。
  - 显示公式编辑区：可读名（`Formula.Name`）、表达式（`Expression`）、输出单位（`OutputUnit`）。
  - 显示输入映射表：每行 = `别名 | 选择输入 Tag（下拉，限同任务真实 Tag）`，可增删行。
- 校验（`Validate` 扩展）：
  - 真实 Tag：ScaleFactor/Offset 填了非数字 → 报错；Item 必填。
  - 虚拟 Tag：表达式必填且能用 DynamicExpresso 解析（调 `IFormulaValidator`）；`Formula.Name` 必填且任务内唯一（用作虚拟 Tag 的 `Item`）；每个别名必须选输入 Tag；表达式引用变量必须都在映射表里（否则"未定义变量"）；映射表别名未在表达式出现 → 警告（非错误）。输入 Tag 必须同任务、可数值化（String 拒绝）。

### 公式校验服务

`IFormulaValidator.Validate(expression, aliases) → (ok, error)`，UI 编辑器与运行期构建共用同一份校验逻辑（单一来源）。放 Infrastructure/Orchestration，与 transform 同处。

### Tag 列表/工作区展示

- Tag 行加来源标记：真实 Tag 标"实"或不标；虚拟 Tag 标"公式"或显示公式名。
- 虚拟 Tag 不显示 Item，改显示公式名或"= 表达式摘要"。
- LiveData/Dashboard 里虚拟 Tag 与真实 Tag 同等显示实时值（都走 `TagValueReceived`，天然一致）。

### 引用完整性

删除某真实 Tag 而它被公式引用 → 拒绝删除并提示"被公式 XXX 引用，先改公式或删虚拟测点"。

### UI 侧新增/改动文件

- `TagEditorViewModel.cs` + `TagEditorWindow.xaml`：加缩放字段、虚拟模式、公式区、输入映射。
- 新 `IFormulaValidator` 接口 + 实现。
- `TagsViewModel`/`TagRow`：加 `IsVirtual`/缩放展示。
- 新 `FormulaInputRow` VM 给输入映射表用。

## §6 错误处理、测试与边界

### 错误处理汇总

- **缩放**：真值非数值型（Bool/String）→ 原值透传，不缩放。缩放结果 NaN/Infinity → 仍发布但质量标 Uncertain。
- **就绪门控**：未就绪不产出虚拟值，无日志噪声（正常冷启动期）。`Failed` → 一次性 INFO 日志"公式 X 因输入 Y 被移除而停止"。
- **求值异常**：节流警告（每公式每 60s 一条），不产出该次虚拟值，公式保持 Ready。
- **质量传播**：输入有 Bad→输出 Bad；有 Uncertain 无 Bad→输出 Uncertain；全 Good→Good。
- **Interpreter 构建失败**（表达式语法错，启动时）：task 启动失败，抛异常带公式名 + 解析错误，`DbTaskLauncher`/WPF 启动路径捕获记日志、跳过该 task（与现有"启动失败跳过"一致）。坏公式不阻塞其他 task。

### 测试策略（对齐现有 Dc.Infrastructure.Tests/Orchestration/ 风格）

**`TagValueTransformTests`（新）**：
- 缩放：`ScaleFactor=0.1, Offset=0` → 原值 255 → 工程量 25.5；非数值 Tag 原值透传。
- 就绪：单输入公式，首值 Bad → NotReady 不产出；首值 Good → Ready 产出；之后变化重算。
- 多输入：两输入公式，先到一个→不产出；两个都 Good→产出；之后任一变化重算（用各自最新值）。
- 质量传播：一输入 Good 一输入 Bad → 输出 Bad；全 Uncertain → 输出 Uncertain。
- 求值异常（除零）→ 不产出、不抛、状态保持 Ready。
- 移除输入 → 公式 Failed、停产出。

**`FormulaValidatorTests`**：合法表达式通过；未定义变量报错；String 型输入拒绝；别名/引用闭合检查。

**`TaskOrchestratorTests`（扩展现有）**：真实 Tag 经 transform 后发布工程量；虚拟 Tag 不进订阅器（用 `FakeOpcSubscriber` 断言 SubscribeAsync 收到的 TagDescriptor 不含虚拟）；移除公式输入后虚拟值停发。

**集成**：`OrchestratorEndToEndTests` 加一例：带缩放 + 公式的 task，合成值流入 → broker 收到工程量 + 虚拟值。

`FakeOpcSubscriber`（现有）无需改。

### 边界与不做的（YAGNI）

- ❌ 跨任务公式引用（Q5 定 A）。
- ❌ 虚拟 Tag 再缩放（Q8 定 A）。
- ❌ 公式运行期热编辑（改公式需停 task 重启；AddTags/RemoveTags 只动真实 Tag）。
- ❌ 公式循环依赖检测：虚拟 Tag 不能作为另一公式输入（虚拟值不回流 Apply 已隐含禁止）；编辑器校验"输入必须是真实 Tag"即可，无需拓扑排序。
- ❌ 内置工业公式库（温压补偿等预设）：先只提供表达式 + 内置数学函数；预设库作为后续可加项。
- ❌ `TaskDiagnostics` 新增虚拟 Tag 计数字段：必要时再补。

### 性能

transform 同步、单线程、`Lambda` 缓存，每拍开销 = N 次缩放 + 至多 M 次公式求值（N 真值、M 受影响公式）。`NoOpTransform`（无公式无缩放）直接返回单元素数组，零额外开销——保证不拖慢现有无公式 task。

## 依赖

- 新增 NuGet：`DynamicExpresso`（~200KB，无 Native 依赖，解释器沙箱，默认禁反射）。
