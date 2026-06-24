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
    private readonly Dc.App.Services.I18n.ILocalizer _loc;

    [ObservableProperty] private string _title;

    // 用户可读名称(可选)。为空时列表回落 Server(UA 是整条 URL 被截断,故鼓励填短名)。
    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(AllowEmptyStrings = false, ErrorMessage = "服务器不能为空")]
    private string _server = string.Empty;       // OPC DA ProgID 或 UA URL

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(TaskEditorViewModel), nameof(ValidateNodeForProtocol))]
    private string _node = "localhost";          // OPC DA Host；UA 隐藏此字段且运行时不读它，故仅 classic OPC 必填(见 ValidateNodeForProtocol)

    [ObservableProperty] private string _clsid = string.Empty;        // DA 兜底，可空
    [ObservableProperty] private OpcProtocol _protocol = OpcProtocol.Ua;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, int.MaxValue, ErrorMessage = "采样间隔必须大于 0 ms")]
    private int _interval = 1000;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(TaskEditorViewModel), nameof(ValidateDeviationForProtocol))]
    private int _deviation = 0;                   // 死区仅 classic OPC 用且可见；UA 隐藏，故 UA 时不做范围校验(见 ValidateDeviationForProtocol)

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

    // 「服务器」字段一词多义：classic OPC 填 ProgID，UA 填 opc.tcp:// URL。按协议切换标签/占位/提示，
    // 避免一个 placeholder 自己写「DA: ProgID / UA: opc.tcp://…」让用户两边猜。
    public string ServerLabel => IsClassicOpcProtocol ? _loc["TaskEditor_ServerLabelProgId"] : _loc["TaskEditor_ServerLabelUa"];
    public string ServerPlaceholder => IsClassicOpcProtocol ? _loc["TaskEditor_ServerPlaceholderProgId"] : _loc["TaskEditor_ServerPlaceholderUa"];
    public string ServerToolTip => IsClassicOpcProtocol
        ? _loc["TaskEditor_ServerTooltipProgId"]
        : _loc["TaskEditor_ServerTooltipUa"];
    // UA 不读 Node（启动时用 Server 字段的 opc.tcp URL，见 DbTaskLauncher.ToStartRequest），
    // 死区对 UA 订阅协议错配——两者仅 classic OPC 需要，UA 时隐藏。
    public bool ShowClassicOnlyFields => IsClassicOpcProtocol;
    public string? OriginalId { get; }

    // Ua 优先（默认协议），与浏览节点默认一致
    public IReadOnlyList<OpcProtocol> Protocols { get; } = new[]
    {
        OpcProtocol.Ua, OpcProtocol.Da, OpcProtocol.Ae
    };

    // 无校验错误才可保存；ErrorsChanged 时通知（见构造）。
    public bool CanSave => !HasErrors;

    public TaskEditorViewModel() : this(null, Array.Empty<IOpcBrowserFactory>()) { }

    public TaskEditorViewModel(CollectorTask? existing, IEnumerable<IOpcBrowserFactory> browserFactories,
        Dc.App.Services.I18n.ILocalizer? localizer = null)
    {
        _factories = browserFactories.ToDictionary(f => f.Protocol);
        _loc = localizer ?? new Dc.App.Services.I18n.ResourceLocalizer();

        if (existing is null)
        {
            _title = _loc["TaskEditor_TitleNew"];
        }
        else
        {
            _title = _loc["TaskEditor_TitleEdit"];
            OriginalId = existing.Id;
            _name = existing.Name ?? string.Empty;
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
        OnPropertyChanged(nameof(ServerLabel));
        OnPropertyChanged(nameof(ServerPlaceholder));
        OnPropertyChanged(nameof(ServerToolTip));
        OnPropertyChanged(nameof(ShowClassicOnlyFields));
        // 切协议后服务器可能不再符合新协议的格式（如 UA 切 DA 后 opc.tcp:// 串无意义），
        // 重跑校验刷新 CanSave/红框。
        ValidateAllProperties();
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
                ScanStatus = _loc.Format("TaskEditor_ScanFilledWithClsid", Server, Clsid, host);
            }
            else
            {
                Server = path;
                Clsid = string.Empty;
                ScanStatus = _loc.Format("TaskEditor_ScanFilled", Server, host);
            }
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!_factories.TryGetValue(Protocol, out var factory))
        {
            ScanStatus = _loc.Format("TaskEditor_ScanNoBrowser", Protocol);
            return;
        }
        IsScanning = true;
        ScanStatus = _loc.Format("TaskEditor_Scanning", DiscoveryHost);
        IOpcBrowser? scanner = null;
        try
        {
            scanner = factory.Create();
            var urls = await scanner.EnumerateServersAsync(DiscoveryHost);
            DiscoveredServers.Clear();
            foreach (var u in urls) DiscoveredServers.Add(u);
            ScanStatus = urls.Count == 0
                ? _loc["TaskEditor_ScanNoServers"]
                : _loc.Format("TaskEditor_ScanFoundCount", urls.Count);
        }
        catch (Exception ex)
        {
            ScanStatus = _loc.Format("TaskEditor_ScanFailed", ex.Message);
        }
        finally
        {
            if (scanner is not null) try { await scanner.DisposeAsync(); } catch { }
            IsScanning = false;
        }
    }

    // Node / 死区仅 classic OPC(DA/AE)可见且需要;UA 隐藏这两个字段、运行时也不读它们(见上字段注释),
    // 故 UA 协议下不校验它们 —— 否则 UA 任务若这两字段恰为非法值(如旧数据/导入 Node 为空),
    // 保存按钮会因 HasErrors 永久禁用,而字段又隐藏、用户无处可改。消息文本不展示给用户(无 ErrorTemplate),
    // 仅驱动 HasErrors/红框,故与既有 DataAnnotation 常量一样保留中文。
    public static ValidationResult? ValidateNodeForProtocol(string? node, ValidationContext ctx)
    {
        var vm = (TaskEditorViewModel)ctx.ObjectInstance;
        return vm.IsClassicOpcProtocol && string.IsNullOrWhiteSpace(node)
            ? new ValidationResult("节点不能为空")
            : ValidationResult.Success;
    }

    public static ValidationResult? ValidateDeviationForProtocol(int deviation, ValidationContext ctx)
    {
        var vm = (TaskEditorViewModel)ctx.ObjectInstance;
        return vm.IsClassicOpcProtocol && (deviation < 0 || deviation > 100)
            ? new ValidationResult("死区必须在 0-100 范围内")
            : ValidationResult.Success;
    }

    // 保存时点的兜底校验：DataAnnotation 特性只驱动 HasErrors/红框(其消息文本不展示给用户),
    // 这里据相同字段条件产出本地化的用户可见错误消息(含属性特性无法表达的协议相关 opc.tcp 前缀检查)。
    public IReadOnlyList<string> Validate()
    {
        ValidateAllProperties();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Server)) errors.Add(_loc["Validation_ServerRequired"]);
        if (Interval < 1) errors.Add(_loc["Validation_IntervalRange"]);
        // Node/死区仅 classic OPC 需要(UA 隐藏且不读),与 DataAnnotation 校验条件一致。
        if (IsClassicOpcProtocol)
        {
            if (string.IsNullOrWhiteSpace(Node)) errors.Add(_loc["Validation_NodeRequired"]);
            if (Deviation is < 0 or > 100) errors.Add(_loc["Validation_DeadbandRange"]);
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(TcpAddress ?? string.Empty, @"^[^:]+:\d+$"))
            errors.Add(_loc["Validation_TcpAddressFormat"]);

        // UA 服务器须为 opc.tcp:// 端点 URL；classic OPC 此字段是 ProgID，不校验前缀。
        if (Protocol == OpcProtocol.Ua
            && !string.IsNullOrWhiteSpace(Server)
            && !Server.TrimStart().StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(_loc["Validation_UaServerPrefix"]);
        }
        return errors;
    }

    public CollectorTask ToEntity() => new()
    {
        Id = OriginalId ?? string.Empty,
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
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
