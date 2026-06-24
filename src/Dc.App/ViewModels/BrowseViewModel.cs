using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Services;
using Dc.App.Services.I18n;
using Dc.App.ViewModels.Workspace;
using Dc.Domain.Entities;
using Dc.Infrastructure.Persistence;
using Dc.Opc.Abstractions;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace Dc.App.ViewModels;

public partial class BrowseViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dictionary<OpcProtocol, IOpcBrowserFactory> _factories;
    // 可空:导航页注入(走 DI)→ 启用批量「加为 Tag」;Tag 编辑器内嵌的单点取用对话框传 null → 不启用。
    private readonly IDbContextFactory<DcDbContext>? _dbFactory;
    private readonly ITaskEditorDialog? _taskEditor;
    private readonly Action<string>? _onTagsAdded; // 批量加 Tag 后回调(taskId):由组合根接「选中该任务+跳工作台」
    private readonly ILocalizer _loc;
    private IOpcBrowser? _browser;
    private readonly Stack<(string? Id, string Name)> _path = new();

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private OpcProtocol _protocol = OpcProtocol.Ua;
    [ObservableProperty] private string _serverUri = "opc.tcp://localhost:4840";
    [ObservableProperty] private string _serverProgId = string.Empty; // DA only: 如 Technosoftware.DaSample
    [ObservableProperty] private string _serverClsid = string.Empty;  // DA 兜底: 显式 CLSID，给值时绕过 OPCEnum
    [ObservableProperty] private bool _useSecurity = true;            // 仅 UA:默认安全;加 Tag 浏览时从任务带入,浏览页可勾选
    [ObservableProperty] private string _discoveryHost = "localhost"; // DA only: 扫描的目标 IP/主机
    [ObservableProperty] private string? _selectedDiscoveredServer;   // 用户从扫描结果中选中的 opcda:// URL
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isConnectError;
    [ObservableProperty] private string _currentPath = string.Empty;
    [ObservableProperty] private BrowseNodeRowViewModel? _selectedNode;

    public bool ShowConnectPrompt => !Connected && !IsLoading && !IsConnectError;

    partial void OnConnectedChanged(bool value) => OnPropertyChanged(nameof(ShowConnectPrompt));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowConnectPrompt));
    partial void OnIsConnectErrorChanged(bool value) => OnPropertyChanged(nameof(ShowConnectPrompt));

    // 节点详情：节点类来自 Kind；数据类型/当前值在选中变化时异步读取（UA ReadValue）。
    [ObservableProperty] private string _selectedNodeClass = "—";
    [ObservableProperty] private string _selectedNodeDataType = "—";
    [ObservableProperty] private string _selectedNodeValue = "—";

    public ObservableCollection<BrowseNodeRowViewModel> Children { get; } = new();
    public ObservableCollection<string> DiscoveredServers { get; } = new();
    public IReadOnlyList<OpcProtocol> AvailableProtocols { get; }
    public bool IsDaProtocol => Protocol == OpcProtocol.Da;
    // DA 和 AE 都是 classic COM/DCOM 流：ProgID 字段、扫描发现、CLSID 兜底三件套对二者都适用
    public bool IsClassicOpcProtocol => Protocol == OpcProtocol.Da || Protocol == OpcProtocol.Ae;
    public bool IsUaProtocol => Protocol == OpcProtocol.Ua;   // 安全连接复选框仅 UA 显示

    public BrowseViewModel(
        IEnumerable<IOpcBrowserFactory> browserFactories,
        IDbContextFactory<DcDbContext>? dbFactory = null,
        ITaskEditorDialog? taskEditor = null,
        Action<string>? onTagsAdded = null,
        ILocalizer? localizer = null)
    {
        _factories = browserFactories.ToDictionary(f => f.Protocol);
        _dbFactory = dbFactory;
        _taskEditor = taskEditor;
        _onTagsAdded = onTagsAdded;
        _loc = localizer ?? new ResourceLocalizer();
        Title = _loc["Browse_Title"];
        StatusMessage = _loc["Browse_StatusNotConnected"];
        CurrentPath = _loc["Browse_PathRoot"];
        // 暴露注册的所有协议；DI 顺序 = UA, DA → UI 下拉同序
        AvailableProtocols = _factories.Keys.ToArray();
        if (_dbFactory is not null) _ = LoadTasksAsync();
    }

    // ── 批量「加为 Tag」(发现→多选→加为 Tag,主配置入口;单点取用对话框不启用) ─────────
    public ObservableCollection<TaskPick> AvailableTasks { get; } = new();
    [ObservableProperty] private TaskPick? _selectedTaskForAdd;
    [ObservableProperty] private int _checkedCount;
    public bool HasCheckedNodes => CheckedCount > 0;
    // 仅导航页(注入了 dbFactory/taskEditor)启用批量加 Tag;对话框单点取用不显示复选框/动作条。
    public bool ShowBulkAdd => _dbFactory is not null && _taskEditor is not null;
    public bool ShowActionBar => ShowBulkAdd && HasCheckedNodes;

    partial void OnCheckedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasCheckedNodes));
        OnPropertyChanged(nameof(ShowActionBar));
        AddToTaskCommand.NotifyCanExecuteChanged();
        AddToNewTaskCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedTaskForAddChanged(TaskPick? value) => AddToTaskCommand.NotifyCanExecuteChanged();

    public async Task LoadTasksAsync()
    {
        if (_dbFactory is null) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var tasks = await db.Tasks.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();
        var prev = SelectedTaskForAdd?.Id;
        AvailableTasks.Clear();
        foreach (var t in tasks) AvailableTasks.Add(new TaskPick(t.Id, t.DisplayName));
        SelectedTaskForAdd = AvailableTasks.FirstOrDefault(p => p.Id == prev) ?? AvailableTasks.FirstOrDefault();
    }

    private void RecomputeChecked() => CheckedCount = Children.Count(r => r.IsItem && r.IsChecked);

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowseNodeRowViewModel.IsChecked)) RecomputeChecked();
    }

    private bool CanAddToTask() => ShowBulkAdd && CheckedCount > 0 && SelectedTaskForAdd is not null;
    private bool CanAddToNew() => ShowBulkAdd && CheckedCount > 0;

    [RelayCommand(CanExecute = nameof(CanAddToTask))]
    private async Task AddToTaskAsync()
    {
        if (SelectedTaskForAdd is null) return;
        await AddCheckedAsTagsAsync(SelectedTaskForAdd.Id, SelectedTaskForAdd.Display);
    }

    [RelayCommand(CanExecute = nameof(CanAddToNew))]
    private async Task AddToNewTaskAsync()
    {
        if (_taskEditor is null || _dbFactory is null) return;
        var created = _taskEditor.Edit(null);
        if (created is null) return;
        created.Id = UlidGenerator.NewId();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.Tasks.Add(created);
            await db.SaveChangesAsync();
        }
        await LoadTasksAsync();
        SelectedTaskForAdd = AvailableTasks.FirstOrDefault(p => p.Id == created.Id);
        await AddCheckedAsTagsAsync(created.Id, created.DisplayName);
    }

    // 把勾选的叶子节点批量建成 Tag,直接挂到任务;同任务已存在的 Item 跳过。
    private async Task AddCheckedAsTagsAsync(string taskId, string taskDisplay)
    {
        if (_dbFactory is null) return;
        var picked = Children.Where(r => r.IsItem && r.IsChecked).ToList();
        if (picked.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = (await db.Tags.AsNoTracking().Where(t => t.TaskId == taskId)
            .Select(t => t.Item).ToListAsync()).ToHashSet(StringComparer.Ordinal);

        var toAdd = picked
            .Where(r => existing.Add(r.Node.Id))   // Add 返回 false = 已存在 → 跳过
            .Select(r => new Tag
            {
                Id = UlidGenerator.NewId(),
                Item = r.Node.Id,
                DataType = MapDataType(r.DataTypeText),
                TaskId = taskId
            })
            .ToList();

        if (toAdd.Count > 0)
        {
            db.Tags.AddRange(toAdd);
            await db.SaveChangesAsync();
        }

        foreach (var r in picked) r.IsChecked = false; // 清勾选(触发 RecomputeChecked)

        var skipped = picked.Count - toAdd.Count;
        var msg = _loc.Format("Browse_AddTagAdded", toAdd.Count, taskDisplay)
            + (skipped > 0 ? _loc.Format("Browse_AddTagSkipped", skipped) : "")
            + _loc["Browse_AddTagGoCollect"];
        MessageDialog.Show(_loc["Browse_AddAsTag"], msg, MessageDialogKind.Success);

        // 跳到采集任务并选中目标任务(发现→配置→看数据 一条线);组合根接线,BrowseViewModel 不依赖具体 VM。
        if (toAdd.Count > 0) _onTagsAdded?.Invoke(taskId);
    }

    // 浏览到的数据类型名(UA: Float/Double/...; 或选项 DisplayName)→ Tag.DataType 码;认不出落 0(默认/自动)。
    private static int MapDataType(string t) => t.Trim() switch
    {
        "Boolean" or "Bool" => 11,
        "SByte" or "Int8" => 16,
        "Byte" or "UInt8" => 17,
        "Int16" => 2, "UInt16" => 18,
        "Int32" => 3, "UInt32" => 19,
        "Int64" => 20, "UInt64" => 21,
        "Float" or "Single" or "Float32" => 4,
        "Double" or "Float64" => 5,
        "String" => 8,
        "DateTime" => 7,
        _ => 0
    };

    // 选中节点的数据类型码(复用 MapDataType):单选浏览取点把真实类型带回 Tag 编辑器,与批量加 Tag 一致;认不出落 0。
    public int SelectedNodeDataTypeCode => SelectedNode is null ? 0 : MapDataType(SelectedNode.DataTypeText);

    [RelayCommand]
    private async Task ConnectAsync()
    {
        await DisposeBrowserAsync().ConfigureAwait(true);

        if (!_factories.TryGetValue(Protocol, out var factory))
        {
            StatusMessage = _loc.Format("Browse_StatusNoBrowserForProtocol", Protocol);
            return;
        }

        IsConnectError = false;
        IsLoading = true;
        StatusMessage = _loc["Browse_StatusConnecting"];
        try
        {
            _browser = factory.Create();
            var options = BuildOptions();
            await _browser.ConnectAsync(options);
            Connected = true;
            _path.Clear();
            var root = _loc["Browse_PathRoot"];
            _path.Push((null, root));
            CurrentPath = root;
            await LoadChildrenAsync(null);
            _ = LoadTasksAsync(); // 刷新「加为 Tag」的任务下拉(可能在工作台新建过任务)
            StatusMessage = string.IsNullOrEmpty(options.ServerProgId)
                ? _loc.Format("Browse_StatusConnected", options.ServerUri)
                : _loc.Format("Browse_StatusConnectedWithProgId", options.ServerUri, options.ServerProgId);
        }
        catch (Exception ex)
        {
            Connected = false;
            IsConnectError = true;
            StatusMessage = _loc.Format("Browse_StatusConnectFailed", ex.Message);
            await DisposeBrowserAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    // 扫描指定主机/IP 上的 OPC DA 服务器（DCOM OPCEnum）。Browser 本身不需要先 Connect。
    [RelayCommand]
    private async Task ScanServersAsync()
    {
        if (!_factories.TryGetValue(Protocol, out var factory))
        {
            StatusMessage = _loc.Format("Browse_StatusNoBrowserForProtocol", Protocol);
            return;
        }

        IsLoading = true;
        StatusMessage = _loc.Format("Browse_StatusScanning", DiscoveryHost);
        IOpcBrowser? scanner = null;
        try
        {
            scanner = factory.Create();
            var urls = await scanner.EnumerateServersAsync(DiscoveryHost);
            DiscoveredServers.Clear();
            foreach (var u in urls) DiscoveredServers.Add(u);
            StatusMessage = urls.Count == 0
                ? _loc["TaskEditor_ScanNoServers"]
                : _loc.Format("TaskEditor_ScanFoundCount", urls.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = _loc.Format("TaskEditor_ScanFailed", ex.Message);
        }
        finally
        {
            if (scanner is not null) try { await scanner.DisposeAsync(); } catch { }
            IsLoading = false;
        }
    }

    // 把扫描出来的 opcda:// URL 一键填进 ServerUri/ProgId(/Clsid)。
    partial void OnSelectedDiscoveredServerChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // 解析 opcda://host/progId[/{clsid}] 或 opcae://host/progId[/{clsid}]
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("opcda", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("opcae", StringComparison.OrdinalIgnoreCase)))
        {
            DiscoveryHost = string.IsNullOrEmpty(uri.Host) ? "localhost" : uri.Host;
            ServerUri = DiscoveryHost;
            // ⚠ AbsolutePath 是 escape 后的，{}会被编码成%7B/%7D；用 LocalPath 拿原文，否则
            //   ProgId 里夹带 %7B，BuildOpcDaUrl 重拼后 vendor new Guid() 必抛 CO_E_CLASSSTRING(0x800401F3)。
            var path = uri.LocalPath.TrimStart('/');
            var slash = path.IndexOf('/');
            if (slash >= 0)
            {
                // scan 给了完整 progId/{clsid}：拆开分别落两个字段
                ServerProgId = path.Substring(0, slash);
                ServerClsid = path.Substring(slash + 1);
                StatusMessage = _loc.Format("TaskEditor_ScanFilledWithClsid", ServerProgId, ServerClsid, DiscoveryHost);
            }
            else
            {
                ServerProgId = path;
                ServerClsid = string.Empty;
                StatusMessage = _loc.Format("TaskEditor_ScanFilled", ServerProgId, DiscoveryHost);
            }
        }
        else
        {
            // 非典型 URL：整串塞 ServerUri，让 Browser 原样透传
            ServerUri = value;
        }
    }

    partial void OnProtocolChanged(OpcProtocol value)
    {
        OnPropertyChanged(nameof(IsDaProtocol));
        OnPropertyChanged(nameof(IsClassicOpcProtocol));
        OnPropertyChanged(nameof(IsUaProtocol));
        // 切换协议时给个合理默认 ServerUri
        if (value == OpcProtocol.Ua && !ServerUri.StartsWith("opc.tcp", StringComparison.OrdinalIgnoreCase))
            ServerUri = "opc.tcp://localhost:4840";
        else if ((value == OpcProtocol.Da || value == OpcProtocol.Ae) && ServerUri.StartsWith("opc.tcp", StringComparison.OrdinalIgnoreCase))
            ServerUri = "localhost";
    }

    private OpcConnectionOptions BuildOptions()
    {
        // DA：ServerUri 当 host 用，配合 ServerProgId；OpcDaBrowser.BuildOpcDaUrl 会拼成 opcda://host/progId
        // 给 ServerClsid 时拼 opcda://host/progId/{clsid}，vendor 直接吃 GUID，跳过 OPCEnum（兜底场景）
        // UA：直接传 opc.tcp:// URL
        return new OpcConnectionOptions
        {
            ServerUri = ServerUri,
            ServerProgId = string.IsNullOrWhiteSpace(ServerProgId) ? null : ServerProgId.Trim(),
            ServerClsid = string.IsNullOrWhiteSpace(ServerClsid) ? null : ServerClsid.Trim(),
            UseSecurity = UseSecurity   // UA:与任务一致(无安全任务浏览也走无安全),否则浏览会报安全错
        };
    }

    [RelayCommand(CanExecute = nameof(CanDrill))]
    private async Task DrillDownAsync()
    {
        if (SelectedNode is null || _browser is null) return;
        if (SelectedNode.Node.Kind != OpcNodeKind.Folder)
        {
            StatusMessage = _loc.Format("Browse_StatusCannotDrillLeaf", SelectedNode.Node.Id);
            return;
        }
        _path.Push((SelectedNode.Node.Id, SelectedNode.Node.DisplayName));
        CurrentPath = string.Join(" / ", _path.Reverse().Select(p => p.Name));
        await LoadChildrenAsync(SelectedNode.Node.Id);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBackAsync()
    {
        if (_path.Count <= 1 || _browser is null) return;
        _path.Pop();
        var parent = _path.Peek();
        CurrentPath = string.Join(" / ", _path.Reverse().Select(p => p.Name));
        await LoadChildrenAsync(parent.Id);
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void CopyNodeId()
    {
        if (SelectedNode is null) return;
        try { System.Windows.Clipboard.SetText(SelectedNode.Node.Id); StatusMessage = _loc.Format("Browse_StatusCopied", SelectedNode.Node.Id); }
        catch (Exception ex) { StatusMessage = _loc.Format("Browse_StatusCopyFailed", ex.Message); }
    }

    private async Task LoadChildrenAsync(string? parentId)
    {
        if (_browser is null) return;
        IsLoading = true;
        try
        {
            var list = await _browser.BrowseAsync(parentId);
            foreach (var old in Children) old.PropertyChanged -= OnRowChanged;
            Children.Clear();
            var rows = new List<BrowseNodeRowViewModel>(list.Count);
            foreach (var n in list) { var r = new BrowseNodeRowViewModel(n); r.PropertyChanged += OnRowChanged; Children.Add(r); rows.Add(r); }
            CheckedCount = 0; // 切目录重置多选(多选限当前目录视图内)
            _ = LoadValuesAsync(rows);
        }
        catch (Exception ex)
        {
            StatusMessage = _loc.Format("Browse_StatusBrowseFailed", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

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
            foreach (var r in items) r.SetValue(null);
            StatusMessage = _loc.Format("Browse_StatusReadValuesFailed", ex.Message);
        }
    }

    [RelayCommand]
    private Task RefreshValuesAsync() => LoadValuesAsync(Children.ToList());

    private async Task DisposeBrowserAsync()
    {
        if (_browser is not null)
        {
            try { await _browser.DisposeAsync(); } catch { }
            _browser = null;
        }
        Connected = false;
        foreach (var old in Children) old.PropertyChanged -= OnRowChanged;
        Children.Clear();
        CheckedCount = 0;
    }

    private bool CanDrill() => SelectedNode?.Node is { Kind: OpcNodeKind.Folder } && Connected;
    private bool CanGoBack() => _path.Count > 1 && Connected;
    private bool CanCopy() => SelectedNode is not null;

    [RelayCommand(CanExecute = nameof(Connected))]
    private async Task DisconnectAsync()
    {
        await DisposeBrowserAsync();
        IsConnectError = false;
        _path.Clear();
        CurrentPath = _loc["Browse_PathDisconnected"];
        StatusMessage = _loc["Browse_StatusDisconnected"];
    }

    partial void OnSelectedNodeChanged(BrowseNodeRowViewModel? value)
    {
        DrillDownCommand.NotifyCanExecuteChanged();
        CopyNodeIdCommand.NotifyCanExecuteChanged();

        if (value is null)
        {
            SelectedNodeClass = SelectedNodeDataType = SelectedNodeValue = "—";
            return;
        }
        SelectedNodeClass = value.Node.Kind == OpcNodeKind.Folder ? "Folder" : "Variable";
        if (value.Node.Kind == OpcNodeKind.Item)
        {
            SelectedNodeDataType = "…";
            SelectedNodeValue = _loc["Browse_DetailValueReading"];
            _ = LoadSelectedNodeDetailAsync(value);
        }
        else
        {
            SelectedNodeDataType = "—";
            SelectedNodeValue = "—";
        }
    }

    // 异步读取选中变量节点的真实值。OnSelectedNodeChanged 在 UI 线程触发、await 默认续回 UI 线程，
    // 故直接设属性安全。读完若选中已变，丢弃旧结果。DA/AE 浏览器未 override ReadValueAsync → 返回 null 显示「—」。
    private async Task LoadSelectedNodeDetailAsync(BrowseNodeRowViewModel row)
    {
        var browser = _browser;
        if (browser is null) return;
        try
        {
            var r = await browser.ReadValueAsync(row.Node.Id);
            if (!ReferenceEquals(SelectedNode, row)) return;   // 选中已变，别覆盖
            if (r is null)
            {
                SelectedNodeDataType = "—";
                SelectedNodeValue = "—";
                return;
            }
            var q = (r.Quality & 0xC0) switch { 0xC0 => "Good", 0x40 => "Uncertain", _ => "Bad" };
            SelectedNodeDataType = r.DataType;
            SelectedNodeValue = $"{FormatValue(r.Value)} · {q}";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(SelectedNode, row))
            {
                SelectedNodeDataType = "—";
                SelectedNodeValue = _loc.Format("Browse_DetailReadFailed", ex.Message);
            }
        }
    }

    private string FormatValue(object? value) => value switch
    {
        null => "null",
        Array a => _loc.Format("Browse_ValueArrayItems", a.Length),
        _ => value.ToString() ?? "null"
    };

    public ValueTask DisposeAsync() => new(DisposeBrowserAsync());
}

/// <summary>「加为 Tag」任务下拉项:任务 Id + 可读名(DisplayName)。</summary>
public sealed record TaskPick(string Id, string Display);
