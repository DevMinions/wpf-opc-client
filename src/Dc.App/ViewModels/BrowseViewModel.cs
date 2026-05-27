using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.Opc.Abstractions;

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
    [ObservableProperty] private string _currentPath = "(根)";
    [ObservableProperty] private OpcNode? _selectedNode;

    // 节点详情：节点类来自 Kind（真实）。
    [ObservableProperty] private string _selectedNodeClass = "—";
    // MOCK: 数据类型 / 当前值 暂无真实数据源（OpcNode 不含、浏览未 read 节点属性）。
    // 接入真实节点读取（UA ReadValue / DA Read）后撤掉这两处 mock。详见 docs/code-review。
    [ObservableProperty] private string _selectedNodeDataType = "—";
    [ObservableProperty] private string _selectedNodeValue = "—";

    public ObservableCollection<OpcNode> Children { get; } = new();
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
        if (SelectedNode.Kind != OpcNodeKind.Folder)
        {
            StatusMessage = $"叶子节点不可下钻: {SelectedNode.Id}";
            return;
        }
        _path.Push((SelectedNode.Id, SelectedNode.DisplayName));
        CurrentPath = string.Join(" / ", _path.Reverse().Select(p => p.Name));
        await LoadChildrenAsync(SelectedNode.Id);
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
        try { System.Windows.Clipboard.SetText(SelectedNode.Id); StatusMessage = $"已复制: {SelectedNode.Id}"; }
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
            foreach (var n in list) Children.Add(n);
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

    private bool CanDrill() => SelectedNode is { Kind: OpcNodeKind.Folder } && Connected;
    private bool CanGoBack() => _path.Count > 1 && Connected;
    private bool CanCopy() => SelectedNode is not null;

    [RelayCommand(CanExecute = nameof(Connected))]
    private async Task DisconnectAsync()
    {
        await DisposeBrowserAsync();
        _path.Clear();
        CurrentPath = "(未连接)";
        StatusMessage = "已断开";
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void AddTag()
    {
        if (SelectedNode is null) return;
        try
        {
            System.Windows.Clipboard.SetText(SelectedNode.Id);
            StatusMessage = $"NodeId 已复制到剪贴板，请在 Tag 管理中新建并粘贴 Item: {SelectedNode.Id}";
        }
        catch (Exception ex) { StatusMessage = $"操作失败: {ex.Message}"; }
    }

    partial void OnSelectedNodeChanged(OpcNode? value)
    {
        DrillDownCommand.NotifyCanExecuteChanged();
        CopyNodeIdCommand.NotifyCanExecuteChanged();

        if (value is null)
        {
            SelectedNodeClass = SelectedNodeDataType = SelectedNodeValue = "—";
            return;
        }
        SelectedNodeClass = value.Kind == OpcNodeKind.Folder ? "Folder" : "Variable";
        // MOCK: 待接真实节点读取后用实际 DataType / Value 替换。
        if (value.Kind == OpcNodeKind.Item)
        {
            SelectedNodeDataType = "Float (mock)";
            SelectedNodeValue = "842.3 · Good (mock)";
        }
        else
        {
            SelectedNodeDataType = "—";
            SelectedNodeValue = "—";
        }
    }

    public ValueTask DisposeAsync() => new(DisposeBrowserAsync());
}
