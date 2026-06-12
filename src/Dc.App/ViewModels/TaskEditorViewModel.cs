using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.Domain.Entities;
using Dc.Opc.Abstractions;

namespace Dc.App.ViewModels;

// ObservableValidator：字段带校验特性 + [NotifyDataErrorInfo] → 输入即时触发 INotifyDataErrorInfo，
// View 的 TextBox 自动红框、CanSave 据 HasErrors 禁用保存。默认协议 Ua（与浏览节点一致、跨平台通用）。
public partial class TaskEditorViewModel : ObservableValidator
{
    private readonly Dictionary<OpcProtocol, IOpcBrowserFactory> _factories;

    [ObservableProperty] private string _title;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(AllowEmptyStrings = false, ErrorMessage = "服务器不能为空")]
    private string _server = string.Empty;       // OPC DA ProgID 或 UA URL

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(AllowEmptyStrings = false, ErrorMessage = "节点不能为空")]
    private string _node = "localhost";          // OPC DA Host；UA 时即服务器地址(opc.tcp URL)，由 OnNodeChanged 镜像进 Server

    [ObservableProperty] private string _clsid = string.Empty;        // DA 兜底，可空
    [ObservableProperty] private OpcProtocol _protocol = OpcProtocol.Ua;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, int.MaxValue, ErrorMessage = "采样间隔必须大于 0 ms")]
    private int _interval = 1000;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(0, 100, ErrorMessage = "死区必须在 0-100 范围内")]
    private int _deviation = 0;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [RegularExpression(@"^[^:]+:\d+$", ErrorMessage = "TCP 地址格式应为 host:port")]
    private string _tcpAddress = "127.0.0.1:5000";

    [ObservableProperty] private bool _useSecurity = true;   // 仅 UA 生效，默认安全

    // DA 扫描 UI
    [ObservableProperty] private string _discoveryHost = "localhost";
    [ObservableProperty] private string? _selectedDiscoveredServer;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = string.Empty;
    public ObservableCollection<string> DiscoveredServers { get; } = new();

    public bool IsUaProtocol => Protocol == OpcProtocol.Ua;
    public bool IsDaProtocol => Protocol == OpcProtocol.Da;
    // DA 和 AE 都走 classic OPC（DCOM/COM），扫描发现 + CLSID 兜底 UI 两者共用
    public bool IsClassicOpcProtocol => Protocol == OpcProtocol.Da || Protocol == OpcProtocol.Ae;
    // UA 下节点即服务器地址，显示标签和占位符随协议切换
    public string NodeLabel => IsUaProtocol ? "服务器地址:" : "节点:";
    public string NodePlaceholder => IsUaProtocol ? "opc.tcp://host:port/path" : "主机名或 IP";
    public string? OriginalId { get; }

    // Ua 优先（默认协议），与浏览节点默认一致
    public IReadOnlyList<OpcProtocol> Protocols { get; } = new[]
    {
        OpcProtocol.Ua, OpcProtocol.Da, OpcProtocol.Ae
    };

    // 无校验错误才可保存；ErrorsChanged 时通知（见构造）。
    public bool CanSave => !HasErrors;

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
            _useSecurity = existing.UseSecurity;
            if (!string.IsNullOrEmpty(existing.Node)) _discoveryHost = existing.Node;
        }

        // HasErrors 变化时刷新 CanSave（驱动保存按钮禁用）；初次全量校验让初始态正确。
        ErrorsChanged += (_, _) => OnPropertyChanged(nameof(CanSave));
        ValidateAllProperties();
    }

    partial void OnNodeChanged(string value)
    {
        if (IsUaProtocol) Server = value;
    }

    partial void OnProtocolChanged(OpcProtocol value)
    {
        OnPropertyChanged(nameof(IsUaProtocol));
        OnPropertyChanged(nameof(IsDaProtocol));
        OnPropertyChanged(nameof(IsClassicOpcProtocol));
        OnPropertyChanged(nameof(NodeLabel));
        OnPropertyChanged(nameof(NodePlaceholder));
        if (IsUaProtocol) Server = Node;
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

    // 保存时点的兜底校验：单一来源 = 字段上的校验特性（避免规则两处维护）。
    public IReadOnlyList<string> Validate()
    {
        ValidateAllProperties();
        return GetErrors()
            .Select(e => e.ErrorMessage ?? string.Empty)
            .Where(m => m.Length > 0)
            .Distinct()
            .ToList();
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
        TcpAddress = TcpAddress.Trim(),
        UseSecurity = UseSecurity
    };
}
