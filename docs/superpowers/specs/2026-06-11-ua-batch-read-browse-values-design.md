# UA 批量读 + 浏览页显示子节点值 设计规格

- 日期：2026-06-11
- 范围：`Dc.Opc.Abstractions`（接口）、`Dc.Opc.Ua`（UA 批量读实现）、`Dc.App`（浏览页接入）、`Dc.Integration.Tests`（基准）。
- 目标：浏览 OPC 地址空间时，进入文件夹自动一次批量读全部可读子节点的当前值并显示；提供批量读 API，性能对标 article.md 的 8200ms→580ms（14×）。

## 1. 背景与现状（已核实）

- `IOpcBrowser.ReadValueAsync(string nodeId)` 单节点读（一次 `Session.Read` 取 Value+DataType 两属性），唯一调用方 `BrowseViewModel:307`（选中节点时读详情）。
- **无批量读 API**；订阅路径已批量（`ApplyChanges` 一次 CreateMonitoredItems）。
- `Dc.Opc.Ua` 为 `net8.0` 跨平台 → UA 性能可在 **Linux 本机基准测试**（集成测试用进程内 UA Server，`MinimalUaNodeManager` 可复用造节点）。
- 模型：`OpcNode(Id, DisplayName, Kind{Folder|Item}, HasChildren)`、`OpcNodeValue(DataType, Value, Quality, SourceTimestamp)`。
- 浏览表绑 `BrowseViewModel.Children`（`ObservableCollection<OpcNode>`），`SelectedNode` 为 `OpcNode`，列显示名称/NodeId，双击下钻用 `SelectedNode.Kind/.Id/.DisplayName`。
- 质量码三态约定：Good `0xC0` / Uncertain `0x40` / Bad `0x00`。

## 2. 目标与非目标

**目标**
- `IOpcBrowser` 加 `ReadValuesAsync(nodeIds)`，UA 一次 `Session.Read` 读 N 个（超 `MaxNodesPerRead` 分块）。
- 进文件夹自动异步批量读 `Kind==Item` 子节点值，填进浏览表新增「值」列（按质量着色：Good 正常色 / 非 Good 警示色，与 LiveData 的 `IsGood` 约定一致，二态足够）。
- 工具栏「刷新值」按钮重读当前层。
- 基准测试证明批量显著快于逐个读。

**非目标（YAGNI）**
- 不做定时/订阅式自动刷新（仅手动「刷新值」）。
- DA/AE 暂不 override `ReadValuesAsync`（走默认全 null，同现有 `ReadValueAsync`）。
- 文件夹节点不读值。

## 3. 决策（已与用户确认）
- 进文件夹**自动批量读一次**；**「刷新值」按钮**重读；文件夹行 → 值列空白；不可读/坏质量的**变量** → 值列「—」。

## 4. 批量读 API

文件：`src/Dc.Opc.Abstractions/IOpcBrowser.cs`、`src/Dc.Opc.Ua/OpcUaBrowser.cs`

- 接口加默认方法（与 `ReadValueAsync` 对称，默认全 null，DA/AE 不 override）：
```csharp
// 一次读 N 个节点当前值；返回与入参等长、按序对应（读不到处为 null）。
Task<IReadOnlyList<OpcNodeValue?>> ReadValuesAsync(IReadOnlyList<string> nodeIds, CancellationToken ct = default)
    => Task.FromResult<IReadOnlyList<OpcNodeValue?>>(new OpcNodeValue?[nodeIds.Count]);
```
- `OpcUaBrowser` override：每个 nodeId 拼两个 `ReadValueId`（Value + DataType），合并成一个 `ReadValueIdCollection`，**一次 `Session.Read`**；按序解析（复用现有质量码位运算 + `ResolveDataType`）。结果按入参顺序回填 `OpcNodeValue?[]`。
- **分块**：批大小 = `_session.OperationLimits?.MaxNodesPerRead`（>0 时；注意每节点占 2 个 ReadValueId，故按 `Max/2` 切）否则默认 500 节点/批；逐块 Read 后拼接。对调用方仍是"一次调用读 N 个"。
- 14× 来源：N 次往返 → ⌈N/批⌉ 次往返。

## 5. 浏览页接入

文件：新增 `src/Dc.App/ViewModels/BrowseNodeRowViewModel.cs`；改 `src/Dc.App/ViewModels/BrowseViewModel.cs`、`src/Dc.App/Views/BrowseView.xaml`。

### 5.1 行 VM
`BrowseNodeRowViewModel`（`ObservableObject`）：
- `public OpcNode Node { get; }`（含 Id/DisplayName/Kind/HasChildren）。
- `[ObservableProperty] string _valueText = "";`、`[ObservableProperty] ushort _quality;`、`[ObservableProperty] bool _hasValue;`。
- `public bool IsGood => Quality == 0xC0;`（着色用，OnQualityChanged 通知）。
- 方法 `SetValue(OpcNodeValue? v)`：v 为 null → `ValueText="—"`、Quality=0x00、HasValue=false；否则格式化 `v.Value`（null→"—"）、Quality=v.Quality、HasValue=true。

### 5.2 BrowseViewModel
- `Children` 改 `ObservableCollection<BrowseNodeRowViewModel>`；`SelectedNode` 改 `BrowseNodeRowViewModel?`。所有 `SelectedNode.X` 用法改 `SelectedNode.Node.X`（DrillDown/GoBack/CopyNodeId/节点详情读取共约 5 处）。
- `LoadChildrenAsync` 末尾：`BrowseAsync` 得到 `OpcNode` 列表 → 包成行 VM 填 `Children` → **异步**调 `LoadValuesAsync(currentItems)`（不阻塞浏览返回）。
- `LoadValuesAsync(rows)`：取 `Kind==Item` 行的 NodeId → `ReadValuesAsync` 一次批量 → 按序 `row.SetValue(...)`；文件夹行不读、保持 ValueText=""。失败整体降级（行值留 "—"），记 StatusMessage。
- `[RelayCommand] RefreshValuesAsync()`：对当前 `Children` 重跑 `LoadValuesAsync`。
- 节点详情面板的现有单读（选中时）**保持不变**（与列值并存）。

### 5.3 BrowseView.xaml
- 表加一列 **「值」**：`Binding ValueText`，按 `IsGood` 着色（Good→`TextFillColorPrimaryBrush`；非 Good→`SystemFillColorCautionBrush`，复用 LiveData 同类画刷约定）。文件夹行值为空白。
- 工具栏（返回/路径附近）加 **「刷新值」** 按钮，绑 `RefreshValuesCommand`，仅已连接可用。
- 名称/NodeId 列绑定改为 `Node.DisplayName`/`Node.Id`；双击下钻、选中绑定不变（SelectedItem 现为行 VM）。

## 6. 错误处理与边界
- `ReadValuesAsync` 入参空 → 返回空列表，不发请求。
- 读失败（连接断/超时）：`LoadValuesAsync` 整体 catch，行值留 "—"，不崩浏览。
- 文件夹行 → 值列空白（不参与批量读）；不可读/坏质量变量 → 值 "—"（HasValue=false）。
- 大文件夹：分块异步读，UI 不冻结；无硬上限（分块 + 异步兜住）。

## 7. Benchmark + 测试
- **基准**（`Dc.Integration.Tests`，`[Collection("Ua")]` 或独立 fixture，跨平台 Linux 跑）：进程内 UA Server 造 1000 个可读 Int32 变量（扩 `MinimalUaNodeManager` 或新 benchmark node manager 循环造节点），`OpcUaBrowser` 连接后测：
  - ① 循环 1000 × `ReadValueAsync`，计时。
  - ② 一次 `ReadValuesAsync(1000 个 nodeId)`，计时。
  - 断言：两者每个节点值一致（正确性）；`batch.Elapsed < loop.Elapsed / 5`（留余量，不锁死 14×）；两耗时 `ITestOutputHelper` 输出。
- **VM 单测**（`Dc.App.Tests`）：`BrowseNodeRowViewModel.SetValue` —— 给值→ValueText/Quality/HasValue 正确、IsGood 映射；给 null→"—"/HasValue=false。
- 全量回归：`dc-remote ... test`（现 Dc.App.Tests 96、Integration 跨平台）。

## 8. 涉及文件
- 新增：`ViewModels/BrowseNodeRowViewModel.cs`、基准测试文件、（可能）benchmark node manager。
- 修改：`IOpcBrowser.cs`、`OpcUaBrowser.cs`、`BrowseViewModel.cs`、`BrowseView.xaml`、`MinimalUaNodeManager.cs`（或新增）。
- 新增测试：BrowseNodeRowViewModel 测、批量读基准测。

## 9. 验收标准
- 浏览进文件夹后，变量子节点的「值」列自动显示当前值并按质量着色；文件夹行值为空。
- 「刷新值」重读当前层、值更新。
- `ReadValuesAsync` 一次读 N 个、超限分块、结果按序对应。
- 基准测试通过：批量读结果与逐个读一致且显著更快（>5×）。
- 全量测试通过。
