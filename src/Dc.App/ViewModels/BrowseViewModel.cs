using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.Opc.Abstractions;
using System.Linq;

namespace Dc.App.ViewModels;

public partial class BrowseViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dictionary<OpcProtocol, IOpcBrowserFactory> _factories;
    private IOpcBrowser? _browser;
    private readonly Stack<(string? Id, string Name)> _path = new();

    [ObservableProperty] private string _title = "OPC 浏览";
    [ObservableProperty] private OpcProtocol _protocol = OpcProtocol.Ua;
    [ObservableProperty] private string _serverUri = "opc.tcp://localhost:4840";
    [ObservableProperty] private string _serverProgId = string.Empty; // DA only: 如 Technosoftware.DaSample
    [ObservableProperty] private string _serverClsid = string.Empty;  // DA 兜底: 显式 CLSID，给值时绕过 OPCEnum
    [ObservableProperty] private string _discoveryHost = "localhost"; // DA only: 扫描的目标 IP/主机
    [ObservableProperty] private string? _selectedDiscoveredServer;   // 用户从扫描结果中选中的 opcda:// URL
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "未连接";
    [ObservableProperty] private bool _isConnectError;
    [ObservableProperty] private string _currentPath = "(根)";
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

    public BrowseViewModel(IEnumerable<IOpcBrowserFactory> browserFactories)
    {
        _factories = browserFactories.ToDictionary(f => f.Protocol);
        // 暴露注册的所有协议；DI 顺序 = UA, DA → UI 下拉同序
        AvailableProtocols = _factories.Keys.ToArray();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        await DisposeBrowserAsync().ConfigureAwait(true);

        if (!_factories.TryGetValue(Protocol, out var factory))
        {
            StatusMessage = $"未注册 {Protocol} 协议的浏览器";
            return;
        }

        IsConnectError = false;
        IsLoading = true;
        StatusMessage = "正在连接…";
        try
        {
            _browser = factory.Create();
            var options = BuildOptions();
            await _browser.ConnectAsync(options);
            Connected = true;
            _path.Clear();
            _path.Push((null, "(根)"));
            CurrentPath = "(根)";
            await LoadChildrenAsync(null);
            StatusMessage = $"已连接 {options.ServerUri}" + (string.IsNullOrEmpty(options.ServerProgId) ? "" : $" / {options.ServerProgId}");
        }
        catch (Exception ex)
        {
            Connected = false;
            IsConnectError = true;
            StatusMessage = $"连接失败: {ex.Message}";
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
            StatusMessage = $"未注册 {Protocol} 协议的浏览器";
            return;
        }

        IsLoading = true;
        StatusMessage = $"正在扫描 {DiscoveryHost} 上的 OPC 服务器…";
        IOpcBrowser? scanner = null;
        try
        {
            scanner = factory.Create();
            var urls = await scanner.EnumerateServersAsync(DiscoveryHost);
            DiscoveredServers.Clear();
            foreach (var u in urls) DiscoveredServers.Add(u);
            StatusMessage = urls.Count == 0
                ? $"未发现 OPC 服务器（检查 OPCEnum / DCOM / 防火墙）"
                : $"发现 {urls.Count} 个服务器";
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败: {ex.Message}";
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
                StatusMessage = $"已填充：{ServerProgId} / {ServerClsid} @ {DiscoveryHost}";
            }
            else
            {
                ServerProgId = path;
                ServerClsid = string.Empty;
                StatusMessage = $"已填充：{ServerProgId} @ {DiscoveryHost}";
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
            ServerClsid = string.IsNullOrWhiteSpace(ServerClsid) ? null : ServerClsid.Trim()
        };
    }

    [RelayCommand(CanExecute = nameof(CanDrill))]
    private async Task DrillDownAsync()
    {
        if (SelectedNode is null || _browser is null) return;
        if (SelectedNode.Node.Kind != OpcNodeKind.Folder)
        {
            StatusMessage = $"叶子节点不可下钻: {SelectedNode.Node.Id}";
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
        try { System.Windows.Clipboard.SetText(SelectedNode.Node.Id); StatusMessage = $"已复制: {SelectedNode.Node.Id}"; }
        catch (Exception ex) { StatusMessage = $"复制失败: {ex.Message}"; }
    }

    private async Task LoadChildrenAsync(string? parentId)
    {
        if (_browser is null) return;
        IsLoading = true;
        try
        {
            var list = await _browser.BrowseAsync(parentId);
            Children.Clear();
            var rows = new List<BrowseNodeRowViewModel>(list.Count);
            foreach (var n in list) { var r = new BrowseNodeRowViewModel(n); Children.Add(r); rows.Add(r); }
            _ = LoadValuesAsync(rows);
        }
        catch (Exception ex)
        {
            StatusMessage = $"浏览失败: {ex.Message}";
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
            StatusMessage = $"读取值失败: {ex.Message}";
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
        Children.Clear();
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
        CurrentPath = "(未连接)";
        StatusMessage = "已断开";
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
            SelectedNodeValue = "读取中…";
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
                SelectedNodeValue = $"(读取失败: {ex.Message})";
            }
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        Array a => $"[{a.Length} 项]",
        _ => value.ToString() ?? "null"
    };

    public ValueTask DisposeAsync() => new(DisposeBrowserAsync());
}
