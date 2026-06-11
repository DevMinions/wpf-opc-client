# UA 批量读 + 浏览页显示子节点值 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 加 `IOpcBrowser.ReadValuesAsync(nodeIds)`（UA 一次 `Session.Read` 读 N 个、超限分块），浏览进文件夹自动批量读变量子节点值并在新增「值」列显示（按质量着色）+「刷新值」按钮；1000 节点基准证明批量 >5× 快。

**架构：** 抽象层加默认方法，UA 实现批量+分块（复用 `ReadValueAsync` 抽出的 `BuildNodeValue`）。浏览页用 `BrowseNodeRowViewModel` 包 `OpcNode`+值，进文件夹异步批量读填值。基准用进程内 UA Server 造 1000 节点。

**技术栈：** UA SDK（Opc.Ua / Session.Read）、CommunityToolkit.Mvvm、xUnit。

**验证分两路：**
- **UA 批量读 + 基准**（`Dc.Opc.Ua` + `Dc.Integration.Tests`，net8.0 跨平台）→ **本机 Linux**：
  `export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter <名>`
- **浏览页 UI**（`Dc.App` net8.0-windows）→ **dc-remote office**：
  `~/dc-remote.sh office sync && ~/dc-remote.sh office build`；测试 `~/dc-remote.sh office test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter <名>'`；截图 `~/dc-remote.sh office run` → `ui click 浏览节点` → `shot`。

**规格依据：** `docs/superpowers/specs/2026-06-11-ua-batch-read-browse-values-design.md`

**已核实事实：**
- `OpcUaBrowser._session`（`Opc.Ua.Client.Session?`）；`ReadValueAsync` 用 `ReadValueIdCollection`（每节点 Value+DataType）+ `Session.Read` + 质量位运算（Bad 0x00/Uncertain 0x40/Good 0xC0）+ `ResolveDataType(valueDv, dataTypeDv)`。
- `OpcNode(string Id, string DisplayName, OpcNodeKind Kind{Folder|Item}, bool HasChildren)`；`OpcNodeValue(string DataType, object? Value, ushort Quality, DateTimeOffset? SourceTimestamp)`。
- `BrowseViewModel.Children` = `ObservableCollection<OpcNode>`（line 39）；`SelectedNode` = `OpcNode?`；`SelectedNode.X` 用法在 DrillDown/GoBack/CopyNodeId/节点详情读约 5 处。
- 基准 server 栈：`MinimalUaNodeManager`（1 个 Demo.Int32）←`MinimalUaServer`←`TestUaServerHost(port, pkiRoot?)`；`TestUaServerHost.FindFreePort()`。`EmbeddedUaServerFixture`/`[Collection("Ua")]` 共享 1 节点 server，**勿改其结构**（UaBrowserSmokeTests 有断言）。

---

## 文件结构

**新增：**
- `src/Dc.App/ViewModels/BrowseNodeRowViewModel.cs` — 浏览行 VM（包 OpcNode + 值/质量）。
- `tests/Dc.Integration.Tests/Ua/UaBatchReadTests.cs` — 批量读正确性 + 1000 节点基准。
- `tests/Dc.App.Tests/ViewModels/BrowseNodeRowViewModelTests.cs` — 行 VM 单测。

**修改：**
- `src/Dc.Opc.Abstractions/IOpcBrowser.cs` — 加 `ReadValuesAsync` 默认方法。
- `src/Dc.Opc.Ua/OpcUaBrowser.cs` — override 批量读 + 抽 `BuildNodeValue`。
- `src/Dc.App/ViewModels/BrowseViewModel.cs` — Children/SelectedNode 改行 VM、LoadValuesAsync、RefreshValuesCommand。
- `src/Dc.App/Views/BrowseView.xaml` — 「值」列 + 「刷新值」按钮 + 列绑定改 `.Node.X`。
- `tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaNodeManager.cs`、`MinimalUaServer.cs`、`TestUaServerHost.cs` — 加 `extraIntVars=0` 默认参数（造基准节点）。

---

## 任务 1：批量读 API（抽象 + UA 实现）

**文件：** 改 `IOpcBrowser.cs`、`OpcUaBrowser.cs`；测试 `tests/Dc.Integration.Tests/Ua/UaBatchReadTests.cs`

- [ ] **步骤 1：编写失败的测试（正确性，跑在共享 1 节点 fixture）**

创建 `tests/Dc.Integration.Tests/Ua/UaBatchReadTests.cs`：

```csharp
using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class UaBatchReadTests
{
    private readonly EmbeddedUaServerFixture _fx;
    private readonly ITestOutputHelper _out;
    public UaBatchReadTests(EmbeddedUaServerFixture fx, ITestOutputHelper o) { _fx = fx; _out = o; }

    private async Task<OpcUaBrowser> ConnectAsync()
    {
        var b = new OpcUaBrowser();
        await b.ConnectAsync(new OpcConnectionOptions { ServerUri = _fx.AnonymousEndpointUrl });
        return b;
    }

    [Fact(Timeout = 30_000)]
    public async Task ReadValuesAsync_ReturnsValuePerNode_OrderedAndMatchesSingleRead()
    {
        await using var b = await ConnectAsync();
        // 浏览 Demo 文件夹拿到变量节点
        var roots = await b.BrowseAsync(null);
        var demo = roots.First(n => n.DisplayName == "Demo");
        var children = await b.BrowseAsync(demo.Id);
        var ids = children.Where(n => n.Kind == OpcNodeKind.Item).Select(n => n.Id).ToList();
        Assert.NotEmpty(ids);

        var batch = await b.ReadValuesAsync(ids);
        Assert.Equal(ids.Count, batch.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var single = await b.ReadValueAsync(ids[i]);
            Assert.NotNull(batch[i]);
            // 数据类型一致（值可能因 ticker 漂移，故只比类型/质量稳定项）
            Assert.Equal(single!.DataType, batch[i]!.DataType);
        }
    }
}
```

- [ ] **步骤 2：跑测试验证失败**

运行：`export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter ReadValuesAsync_ReturnsValuePerNode_OrderedAndMatchesSingleRead`
预期：编译失败（`ReadValuesAsync` 不存在）。

- [ ] **步骤 3：抽象层加默认方法**

`src/Dc.Opc.Abstractions/IOpcBrowser.cs`，在 `ReadValueAsync` 默认方法后加：

```csharp
    // 一次读 N 个节点当前值；返回与入参等长、按序对应（读不到处为 null）。
    // 默认空实现（DA/AE 暂不 override，同 ReadValueAsync）。
    Task<IReadOnlyList<OpcNodeValue?>> ReadValuesAsync(IReadOnlyList<string> nodeIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OpcNodeValue?>>(new OpcNodeValue?[nodeIds.Count]);
```

- [ ] **步骤 4：OpcUaBrowser 抽 BuildNodeValue + override 批量读**

在 `src/Dc.Opc.Ua/OpcUaBrowser.cs`：把 `ReadValueAsync` 里"由 valueDv/dataTypeDv 组装 OpcNodeValue"那段抽成私有方法，`ReadValueAsync` 改用它；新增 `ReadValuesAsync`。

抽出的 helper（放在 `ResolveDataType` 附近）：
```csharp
    // 由一对 Value/DataType DataValue 组装 OpcNodeValue（质量位运算 + 类型解析）。
    private OpcNodeValue BuildNodeValue(DataValue valueDv, DataValue? dataTypeDv)
    {
        ushort quality;
        if (StatusCode.IsBad(valueDv.StatusCode)) quality = 0x00;
        else if (StatusCode.IsUncertain(valueDv.StatusCode)) quality = 0x40;
        else quality = 0xC0;

        DateTimeOffset? ts = valueDv.SourceTimestamp == DateTime.MinValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(valueDv.SourceTimestamp, DateTimeKind.Utc));

        return new OpcNodeValue(ResolveDataType(valueDv, dataTypeDv), valueDv.Value, quality, ts);
    }
```
`ReadValueAsync` 末尾改为 `var result = BuildNodeValue(valueDv, dataTypeDv); return Task.FromResult<OpcNodeValue?>(result);`（删掉原来内联的 quality/ts/new OpcNodeValue 三段）。

新增批量读：
```csharp
    public Task<IReadOnlyList<OpcNodeValue?>> ReadValuesAsync(IReadOnlyList<string> nodeIds, CancellationToken ct = default)
    {
        if (_session is null) throw new InvalidOperationException("ConnectAsync must be called first");
        var results = new OpcNodeValue?[nodeIds.Count];
        if (nodeIds.Count == 0) return Task.FromResult<IReadOnlyList<OpcNodeValue?>>(results);

        // 每节点 2 个 ReadValueId（Value+DataType）；按服务器 MaxNodesPerRead/2 分块，未知则 500。
        uint maxNodes = _session.OperationLimits?.MaxNodesPerRead ?? 0u;
        int chunk = maxNodes > 1 ? (int)(maxNodes / 2) : 500;
        if (chunk < 1) chunk = 500;

        for (var start = 0; start < nodeIds.Count; start += chunk)
        {
            var end = Math.Min(start + chunk, nodeIds.Count);
            var toRead = new ReadValueIdCollection();
            for (var i = start; i < end; i++)
            {
                var id = new NodeId(nodeIds[i]);
                toRead.Add(new ReadValueId { NodeId = id, AttributeId = Attributes.Value });
                toRead.Add(new ReadValueId { NodeId = id, AttributeId = Attributes.DataType });
            }
            _session.Read(null, 0, TimestampsToReturn.Source, toRead,
                out DataValueCollection dvs, out DiagnosticInfoCollection _);
            for (var i = start; i < end; i++)
            {
                var k = (i - start) * 2;
                results[i] = BuildNodeValue(dvs[k], k + 1 < dvs.Count ? dvs[k + 1] : null);
            }
        }
        return Task.FromResult<IReadOnlyList<OpcNodeValue?>>(results);
    }
```
> `OperationLimits?.MaxNodesPerRead` 是 UA SDK 属性（0/null=未知）；若属性名/类型与 SDK 实际有出入，按实际调整、拿不到就用默认 500。

- [ ] **步骤 5：跑测试验证通过**

运行：`~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter ReadValuesAsync_ReturnsValuePerNode_OrderedAndMatchesSingleRead`（带 `DOTNET_ROOT`）
预期：通过。

- [ ] **步骤 6：Commit**

```bash
cd /home/adamyu/workspace/wpf-opc-client
git add src/Dc.Opc.Abstractions/IOpcBrowser.cs src/Dc.Opc.Ua/OpcUaBrowser.cs tests/Dc.Integration.Tests/Ua/UaBatchReadTests.cs
git commit -m "✨ feat(opc): IOpcBrowser.ReadValuesAsync 批量读（UA 一次 Read + 分块）"
```
（末尾加 `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`）

---

## 任务 2：1000 节点基准

**文件：** 改 `MinimalUaNodeManager.cs`、`MinimalUaServer.cs`、`TestUaServerHost.cs`（加 extraIntVars）；加基准测试到 `UaBatchReadTests.cs`

- [ ] **步骤 1：三处加 extraIntVars 参数（造基准节点）**

`MinimalUaNodeManager.cs`：构造加参数并存字段；`CreateAddressSpace` 在 `folder.AddChild(_demoInt)` 之后、`AddPredefinedNode` 之前循环造静态 Int32 变量。

构造改：
```csharp
    private readonly int _extraIntVars;
    public MinimalUaNodeManager(IServerInternal server, ApplicationConfiguration configuration, int extraIntVars = 0)
        : base(server, configuration, TestNamespace)
    {
        _extraIntVars = extraIntVars;
    }
```
`folder.AddChild(_demoInt);` 之后加：
```csharp
            for (var i = 0; i < _extraIntVars; i++)
            {
                var v = new BaseDataVariableState(folder)
                {
                    NodeId = new NodeId($"Bench.{i}", NamespaceIndex),
                    BrowseName = new QualifiedName($"Bench.{i}", NamespaceIndex),
                    DisplayName = new LocalizedText($"Bench.{i}"),
                    DataType = DataTypeIds.Int32,
                    ValueRank = ValueRanks.Scalar,
                    AccessLevel = AccessLevels.CurrentRead,
                    UserAccessLevel = AccessLevels.CurrentRead,
                    Value = i,                 // 静态值（不 tick），基准/正确性确定
                    StatusCode = StatusCodes.Good,
                    Timestamp = DateTime.UtcNow
                };
                folder.AddChild(v);
            }
```

`MinimalUaServer.cs`：加构造转发参数。
```csharp
internal sealed class MinimalUaServer : StandardServer
{
    private readonly int _extraIntVars;
    public MinimalUaServer(int extraIntVars = 0) => _extraIntVars = extraIntVars;

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        var nodeManagers = new INodeManager[]
        {
            new MinimalUaNodeManager(server, configuration, _extraIntVars)
        };
        return new MasterNodeManager(server, configuration, dynamicNamespaceUri: null, nodeManagers);
    }
}
```

`TestUaServerHost.cs`：构造加参数，`StartAsync` 里 `new MinimalUaServer(_extraIntVars)`。
```csharp
    private readonly int _extraIntVars;
```
构造签名改为 `public TestUaServerHost(int port, string? pkiRoot = null, int extraIntVars = 0)`，体内存 `_extraIntVars = extraIntVars;`；`StartAsync` 里 `_server = new MinimalUaServer(_extraIntVars);`。

> 现有调用方都用默认（extraIntVars=0）→ 结构不变，UaBrowserSmokeTests/Subscriber 等不受影响。

- [ ] **步骤 2：编写基准测试**

在 `UaBatchReadTests.cs` 类内追加（自建 1000 节点 server，不用共享 fixture）：

```csharp
    [Fact(Timeout = 120_000)]
    public async Task Benchmark_BatchRead_IsMuchFasterThanLoop_AndConsistent()
    {
        await using var host = new TestUaServerHost(TestUaServerHost.FindFreePort(), extraIntVars: 1000);
        await host.StartAsync();

        await using var b = new OpcUaBrowser();
        await b.ConnectAsync(new OpcConnectionOptions { ServerUri = host.EndpointUrl });

        var roots = await b.BrowseAsync(null);
        var demo = roots.First(n => n.DisplayName == "Demo");
        var children = await b.BrowseAsync(demo.Id);
        // 只取静态 Bench.* 节点（Demo.Int32 在 tick，避免比对漂移）
        var ids = children.Where(n => n.Kind == OpcNodeKind.Item && n.DisplayName.StartsWith("Bench."))
                          .Select(n => n.Id).ToList();
        Assert.Equal(1000, ids.Count);

        var swLoop = System.Diagnostics.Stopwatch.StartNew();
        var loop = new OpcNodeValue?[ids.Count];
        for (var i = 0; i < ids.Count; i++) loop[i] = await b.ReadValueAsync(ids[i]);
        swLoop.Stop();

        var swBatch = System.Diagnostics.Stopwatch.StartNew();
        var batch = await b.ReadValuesAsync(ids);
        swBatch.Stop();

        _out.WriteLine($"loop(1000×单读)={swLoop.ElapsedMilliseconds}ms  batch(1次)={swBatch.ElapsedMilliseconds}ms  speedup={(double)swLoop.ElapsedMilliseconds / Math.Max(1, swBatch.ElapsedMilliseconds):F1}×");

        // 正确性：每个 Bench.i 的值是静态 i，两路一致
        for (var i = 0; i < ids.Count; i++)
            Assert.Equal(Convert.ToInt32(loop[i]!.Value), Convert.ToInt32(batch[i]!.Value));

        // 性能：批量显著快（留余量，不锁死 14×）
        Assert.True(swBatch.ElapsedMilliseconds * 5 < swLoop.ElapsedMilliseconds,
            $"batch {swBatch.ElapsedMilliseconds}ms 应 < loop {swLoop.ElapsedMilliseconds}ms / 5");
    }
```

- [ ] **步骤 3：跑基准验证通过 + 看加速比**

运行：`~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --filter "Benchmark_BatchRead_IsMuchFasterThanLoop_AndConsistent" -l "console;verbosity=detailed"`（带 `DOTNET_ROOT`）
预期：通过；输出里能看到 `loop=…ms batch=…ms speedup=…×`（应远大于 5×）。把加速比记进 commit/汇报。

- [ ] **步骤 4：Commit**

```bash
git add tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaNodeManager.cs tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaServer.cs tests/Dc.Integration.Tests/Ua/Fixtures/TestUaServerHost.cs tests/Dc.Integration.Tests/Ua/UaBatchReadTests.cs
git commit -m "✅ test(opc): 1000 节点批量读基准（批量 vs 逐个，断言 >5× 且一致）"
```

---

## 任务 3：BrowseNodeRowViewModel + 单测

**文件：** 创建 `src/Dc.App/ViewModels/BrowseNodeRowViewModel.cs`；测试 `tests/Dc.App.Tests/ViewModels/BrowseNodeRowViewModelTests.cs`（dc-remote office）

- [ ] **步骤 1：编写失败的测试**

创建 `tests/Dc.App.Tests/ViewModels/BrowseNodeRowViewModelTests.cs`：

```csharp
using Dc.App.ViewModels;
using Dc.Opc.Abstractions;

namespace Dc.App.Tests.ViewModels;

public class BrowseNodeRowViewModelTests
{
    private static BrowseNodeRowViewModel Row()
        => new(new OpcNode("ns=2;s=A", "A", OpcNodeKind.Item, false));

    [Fact]
    public void SetValue_Good_FormatsText_SetsQuality_IsGood()
    {
        var row = Row();
        row.SetValue(new OpcNodeValue("Int32", 42, 0xC0, DateTimeOffset.UtcNow));
        Assert.Equal("42", row.ValueText);
        Assert.Equal((ushort)0xC0, row.Quality);
        Assert.True(row.HasValue);
        Assert.True(row.IsGood);
    }

    [Fact]
    public void SetValue_Null_ShowsDash_NotHasValue_NotGood()
    {
        var row = Row();
        row.SetValue(null);
        Assert.Equal("—", row.ValueText);
        Assert.False(row.HasValue);
        Assert.False(row.IsGood);
    }

    [Fact]
    public void SetValue_BadQualityNullValue_HasValueButDash_NotGood()
    {
        var row = Row();
        row.SetValue(new OpcNodeValue("Int32", null, 0x00, null));
        Assert.Equal("—", row.ValueText);
        Assert.True(row.HasValue);
        Assert.False(row.IsGood);
    }
}
```

- [ ] **步骤 2：跑测试验证失败**

运行：`~/dc-remote.sh office sync && ~/dc-remote.sh office test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter BrowseNodeRowViewModelTests'`
预期：编译失败（`BrowseNodeRowViewModel` 不存在）。

- [ ] **步骤 3：创建 BrowseNodeRowViewModel**

创建 `src/Dc.App/ViewModels/BrowseNodeRowViewModel.cs`：

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Dc.Opc.Abstractions;

namespace Dc.App.ViewModels;

// 浏览结果行：包一个 OpcNode + 其当前值（批量读填充）。仅展示，零业务逻辑。
public partial class BrowseNodeRowViewModel : ObservableObject
{
    public OpcNode Node { get; }

    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private ushort _quality;
    [ObservableProperty] private bool _hasValue;

    public bool IsGood => Quality == 0xC0;

    public BrowseNodeRowViewModel(OpcNode node) => Node = node;

    // 批量读结果填入：null（文件夹/读失败）→ "—"、无值；否则取值文本（值为 null 也显 "—"）。
    public void SetValue(OpcNodeValue? v)
    {
        HasValue = v is not null;
        Quality = v?.Quality ?? 0;
        ValueText = v?.Value?.ToString() ?? "—";
    }

    partial void OnQualityChanged(ushort value) => OnPropertyChanged(nameof(IsGood));
}
```

- [ ] **步骤 4：跑测试验证通过**

运行：`~/dc-remote.sh office test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter BrowseNodeRowViewModelTests'`
预期：`total=3 passed=3 failed=0`

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/ViewModels/BrowseNodeRowViewModel.cs tests/Dc.App.Tests/ViewModels/BrowseNodeRowViewModelTests.cs
git commit -m "✨ feat(ui): BrowseNodeRowViewModel（浏览行包节点+值）"
```

---

## 任务 4：BrowseViewModel 接入批量读

**文件：** 改 `src/Dc.App/ViewModels/BrowseViewModel.cs`（dc-remote office build）

> 无新单测（集成性改动，靠构建 + 任务 5 截图验证）。本任务改动较多，先完整 Read 该文件再动手。

- [ ] **步骤 1：改集合与选中类型**

- `public ObservableCollection<OpcNode> Children { get; } = new();` → `public ObservableCollection<BrowseNodeRowViewModel> Children { get; } = new();`
- `[ObservableProperty] private OpcNode? _selectedNode;` → `[ObservableProperty] private BrowseNodeRowViewModel? _selectedNode;`
- 把所有 `SelectedNode.X` 改为 `SelectedNode.Node.X`（DrillDown 里 `SelectedNode.Kind`/`.Id`/`.DisplayName`、CopyNodeId、节点详情读取等约 5 处；CanDrill/CanGoBack 里若引用 `SelectedNode is { Kind: ... }` 改 `SelectedNode?.Node is { Kind: ... }`）。逐处编译错误驱动改全。

- [ ] **步骤 2：LoadChildrenAsync 填行 VM + 异步批量读**

找到 `LoadChildrenAsync`，把"`BrowseAsync` 结果填进 Children"那段改为包行 VM，并在填完后异步批量读：
```csharp
        var nodes = await _browser.BrowseAsync(parentId);
        Children.Clear();
        var rows = new List<BrowseNodeRowViewModel>(nodes.Count);
        foreach (var n in nodes) { var r = new BrowseNodeRowViewModel(n); Children.Add(r); rows.Add(r); }
        _ = LoadValuesAsync(rows);   // 不阻塞浏览返回
```
> 以现有 LoadChildrenAsync 实际结构为准微调（变量名/是否已有 Children.Clear()）。

新增方法：
```csharp
    private async Task LoadValuesAsync(IReadOnlyList<BrowseNodeRowViewModel> rows)
    {
        if (_browser is null) return;
        var items = rows.Where(r => r.Node.Kind == OpcNodeKind.Item).ToList();
        if (items.Count == 0) return;
        try
        {
            var values = await _browser.ReadValuesAsync(items.Select(r => r.Node.Id).ToList());
            for (var i = 0; i < items.Count; i++) items[i].SetValue(values[i]);
        }
        catch (Exception ex)
        {
            foreach (var r in items) r.SetValue(null);   // 整体降级显 "—"
            StatusMessage = $"读取值失败: {ex.Message}";
        }
    }
```

- [ ] **步骤 3：刷新值命令**

加：
```csharp
    [RelayCommand]
    private Task RefreshValuesAsync() => LoadValuesAsync(Children.ToList());
```

- [ ] **步骤 4：构建验证**

运行：`~/dc-remote.sh office sync && ~/dc-remote.sh office build`
预期：`0 个错误`（若有 `SelectedNode.X` 漏改的编译错，按报错补全）。

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/ViewModels/BrowseViewModel.cs
git commit -m "✨ feat(ui): 浏览页进文件夹异步批量读子节点值 + 刷新值命令"
```

---

## 任务 5：BrowseView.xaml「值」列 + 「刷新值」按钮

**文件：** 改 `src/Dc.App/Views/BrowseView.xaml`（dc-remote office build + 截图）

- [ ] **步骤 1：列绑定改 .Node + 加「值」列**

读 `BrowseView.xaml` 的 DataGrid（`ItemsSource="{Binding Children}"`，约 168 行）。把名称/NodeId 列的 Binding 从 `DisplayName`/`Id` 改为 `Node.DisplayName`/`Node.Id`。在 NodeId 列后追加「值」列（按 IsGood 着色）：
```xml
                            <DataGridTextColumn Header="值" Binding="{Binding ValueText}" Width="160">
                                <DataGridTextColumn.ElementStyle>
                                    <Style TargetType="TextBlock">
                                        <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding HasValue}" Value="True">
                                                <Setter Property="Foreground" Value="{DynamicResource SystemFillColorCautionBrush}" />
                                            </DataTrigger>
                                            <DataTrigger Binding="{Binding IsGood}" Value="True">
                                                <Setter Property="Foreground" Value="{DynamicResource SystemFillColorSuccessBrush}" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </DataGridTextColumn.ElementStyle>
                            </DataGridTextColumn>
```
> 触发器顺序：默认主文本色（文件夹/空）；HasValue（变量有读结果）→ 警示色；IsGood 覆盖为成功色。后者优先级（后定义）保证 Good 绿、非 Good 黄、无值常态。以现有列定义结构为准放在合适位置。

- [ ] **步骤 2：加「刷新值」按钮**

在工具栏「返回」按钮（`Command="{Binding GoBackCommand}"`，约 134 行）附近追加：
```xml
                    <Button Content="刷新值" Margin="6,0,0,0"
                            Command="{Binding RefreshValuesCommand}"
                            Style="{StaticResource DcBtnGhostSm}" />
```

- [ ] **步骤 3：构建 + 截图验证**

```bash
~/dc-remote.sh office sync && ~/dc-remote.sh office build   # 0 错误
~/dc-remote.sh office run
~/dc-remote.sh office ui click 浏览节点
~/dc-remote.sh office ui click 连接          # 连默认 opc.tcp://localhost:4840（office 无 server → 失败也行，主要看列在不在）
~/dc-remote.sh office shot                    # Read：确认表多了「值」列、工具栏有「刷新值」
```
> office 上无 OPC server，连接会失败（红条），但「值」列与「刷新值」按钮结构应可见。若要看真实值需有 UA server——本步以列/按钮存在 + 构建 0 错误为准，真实值由任务 1/2 的基准保证。把截图所见写进汇报。

- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/Views/BrowseView.xaml
git commit -m "✨ feat(ui): 浏览表加「值」列（质量着色）+「刷新值」按钮"
```

---

## 任务 6：整体验证

- [ ] **步骤 1：UA 侧全量（Linux）**

运行：`export DOTNET_ROOT=$HOME/.dotnet && ~/.dotnet/dotnet test tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj`
预期：全绿（含新批量读 + 基准；基准约数秒）。

- [ ] **步骤 2：App 侧全量（dc-remote office）**

运行：`~/dc-remote.sh office sync && ~/dc-remote.sh office test 'tests/Dc.App.Tests/Dc.App.Tests.csproj'`
预期：全绿（现 96 + 新 3 = 99 上下）。

- [ ] **步骤 3：浏览页截图复核**

`~/dc-remote.sh office run` → `ui click 浏览节点` → `shot` → Read，确认「值」列 + 「刷新值」按钮在位、布局正常。

- [ ] **步骤 4：最终 Commit（如有微调）**

```bash
git add -A
git commit -m "✅ test: UA 批量读 + 浏览取值整体验证" --allow-empty
```

---

## 自检结论

- **规格覆盖**：批量读 API（任务1）、1000 节点基准（任务2）、行 VM（任务3）、Browse 接入（任务4）、值列+刷新按钮（任务5）、整体验证（任务6）——规格各节均有对应任务。
- **类型一致**：`ReadValuesAsync`/`BuildNodeValue`/`BrowseNodeRowViewModel`(Node/ValueText/Quality/HasValue/IsGood/SetValue)/`Children`/`SelectedNode`/`LoadValuesAsync`/`RefreshValuesCommand`/`extraIntVars` 全程一致。
- **验证分路明确**：UA/基准本机 Linux `dotnet test`；App/UI 走 dc-remote office。
- **待实现期确认**（已标注）：`OperationLimits.MaxNodesPerRead` 的 SDK 精确属性名/类型；LoadChildrenAsync 实际填充结构；BrowseView 列/工具栏精确插入点；`SelectedNode.X` 全部改 `.Node.X` 由编译错驱动补全。
- **占位符**：无 TODO；office 无 UA server 的截图局限已标注（以列/按钮存在 + 构建为准，真实值由基准保证）。
