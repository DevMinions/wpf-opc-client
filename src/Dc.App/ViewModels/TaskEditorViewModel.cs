using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.Domain.Entities;
using Dc.Opc.Abstractions;

namespace Dc.App.ViewModels;

public partial class TaskEditorViewModel : ObservableObject
{
    private readonly Dictionary<OpcProtocol, IOpcBrowserFactory> _factories;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _server = string.Empty;       // OPC DA ProgID 或 UA URL
    [ObservableProperty] private string _node = "localhost";          // OPC DA Host；UA 时通常和 Server 同
    [ObservableProperty] private string _clsid = string.Empty;        // DA 兜底，可空
    [ObservableProperty] private OpcProtocol _protocol = OpcProtocol.Da;
    [ObservableProperty] private int _interval = 1000;
    [ObservableProperty] private int _deviation = 0;
    [ObservableProperty] private string _tcpAddress = "127.0.0.1:5000";

    // DA 扫描 UI
    [ObservableProperty] private string _discoveryHost = "localhost";
    [ObservableProperty] private string? _selectedDiscoveredServer;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = string.Empty;
    public ObservableCollection<string> DiscoveredServers { get; } = new();

    public bool IsDaProtocol => Protocol == OpcProtocol.Da;
    // DA 和 AE 都走 classic OPC（DCOM/COM），扫描发现 + CLSID 兜底 UI 两者共用
    public bool IsClassicOpcProtocol => Protocol == OpcProtocol.Da || Protocol == OpcProtocol.Ae;
    public string? OriginalId { get; }

    public IReadOnlyList<OpcProtocol> Protocols { get; } = new[]
    {
        OpcProtocol.Da, OpcProtocol.Ua, OpcProtocol.Ae
    };

    public TaskEditorViewModel() : this(null, Array.Empty<IOpcBrowserFactory>()) { }

    public TaskEditorViewModel(CollectorTask? existing, IEnumerable<IOpcBrowserFactory> browserFactories)
    {
        _factories = browserFactories.ToDictionary(f => f.Protocol);

        if (existing is null)
        {
            _title = "新建任务";
        }
        else
        {
            _title = "编辑任务";
            OriginalId = existing.Id;
            _server = existing.Server;
            _node = existing.Node;
            _clsid = existing.Clsid ?? string.Empty;
            _protocol = (OpcProtocol)existing.Type;
            _interval = existing.Interval;
            _deviation = existing.Deviation;
            _tcpAddress = existing.TcpAddress;
            if (!string.IsNullOrEmpty(existing.Node)) _discoveryHost = existing.Node;
        }
    }

    partial void OnProtocolChanged(OpcProtocol value)
    {
        OnPropertyChanged(nameof(IsDaProtocol));
        OnPropertyChanged(nameof(IsClassicOpcProtocol));
    }

    // 与 BrowseViewModel 同套逻辑：用 LocalPath 防 {} 被编码成 %7B/%7D，再按 '/' 拆出 progId 与 clsid
    partial void OnSelectedDiscoveredServerChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // 同时接受 opcda:// (DA) 和 opcae:// (AE) — 二者 URL 结构一致
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("opcda", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("opcae", StringComparison.OrdinalIgnoreCase)))
        {
            var host = string.IsNullOrEmpty(uri.Host) ? "localhost" : uri.Host;
            Node = host;
            var path = uri.LocalPath.TrimStart('/');
            var slash = path.IndexOf('/');
            if (slash >= 0)
            {
                Server = path.Substring(0, slash);
                Clsid = path.Substring(slash + 1);
                ScanStatus = $"已填充：{Server} / {Clsid} @ {host}";
            }
            else
            {
                Server = path;
                Clsid = string.Empty;
                ScanStatus = $"已填充：{Server} @ {host}";
            }
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!_factories.TryGetValue(Protocol, out var factory))
        {
            ScanStatus = $"未注册 {Protocol} 协议的浏览器";
            return;
        }
        IsScanning = true;
        ScanStatus = $"正在扫描 {DiscoveryHost}…";
        IOpcBrowser? scanner = null;
        try
        {
            scanner = factory.Create();
            var urls = await scanner.EnumerateServersAsync(DiscoveryHost);
            DiscoveredServers.Clear();
            foreach (var u in urls) DiscoveredServers.Add(u);
            ScanStatus = urls.Count == 0
                ? "未发现 OPC 服务器（检查 OPCEnum / DCOM / 防火墙）"
                : $"发现 {urls.Count} 个服务器";
        }
        catch (Exception ex)
        {
            ScanStatus = $"扫描失败: {ex.Message}";
        }
        finally
        {
            if (scanner is not null) try { await scanner.DisposeAsync(); } catch { }
            IsScanning = false;
        }
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Server)) errors.Add("服务器不能为空");
        if (string.IsNullOrWhiteSpace(Node)) errors.Add("节点不能为空");
        if (Interval < 1) errors.Add("采样间隔必须大于 0 ms");
        if (Deviation < 0 || Deviation > 100) errors.Add("死区必须在 0-100 范围内");
        if (string.IsNullOrWhiteSpace(TcpAddress) || !TcpAddress.Contains(':'))
            errors.Add("TCP 地址格式应为 host:port");
        return errors;
    }

    public CollectorTask ToEntity() => new()
    {
        Id = OriginalId ?? string.Empty,
        Server = Server.Trim(),
        Node = Node.Trim(),
        Clsid = string.IsNullOrWhiteSpace(Clsid) ? null : Clsid.Trim(),
        Type = (byte)Protocol,
        Interval = Interval,
        Deviation = Deviation,
        TcpAddress = TcpAddress.Trim()
    };
}
