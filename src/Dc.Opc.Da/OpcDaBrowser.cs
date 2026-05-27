using Dc.Opc.Abstractions;
using Technosoftware.DaAeHdaClient;
using Technosoftware.DaAeHdaClient.Da;
using ComFactory = Technosoftware.DaAeHdaClient.Com.Factory;
using ServerEnumerator = Technosoftware.DaAeHdaClient.Com.ServerEnumerator;

namespace Dc.Opc.Da;

// OPC DA 浏览器实现：
//   - EnumerateServersAsync(host)：用 ServerEnumerator + IOpcDiscovery 走 DCOM/OPCEnum，按 host 列 DA 服务器
//   - ConnectAsync：TsCDaServer.Connect(opcda://host/progId)
//   - BrowseAsync(parentItemName)：TsCDaServer.Browse + BrowseNext 走完所有 position
// COM 串行用 _comLock 保护；UA 端是 async 的，这里强转 Task.FromResult 即可，DA SDK 本身同步。
public sealed class OpcDaBrowser : IOpcBrowser
{
    private readonly object _comLock = new();
    private TsCDaServer? _server;
    private bool _disposed;

    // DA SDK 全是同步 COM 调用，必须 Task.Run 丢线程池，否则在 WPF 调用方会卡 UI 线程。
    public Task ConnectAsync(OpcConnectionOptions options, CancellationToken ct = default)
    {
        // ⚠ vendor 的 OpcServer.Connect(string) 有 bug：第一行 Factory=null，后续 Factory.ForceDa20Usage 必 NRE。
        // 必须走 ctor 2（注入 Com.Factory + OpcUrl）+ 无参 Connect()。
        var opcUrl = new OpcUrl(BuildOpcDaUrl(options));
        return Task.Run(() =>
        {
            lock (_comLock)
            {
                _server = new TsCDaServer(new ComFactory(), opcUrl);
                _server.Connect();
            }
        }, ct);
    }

    public Task<IReadOnlyList<OpcNode>> BrowseAsync(string? parentNodeId = null, CancellationToken ct = default)
    {
        if (_server is null) throw new InvalidOperationException("ConnectAsync 必须先调用");

        return Task.Run<IReadOnlyList<OpcNode>>(() =>
        {
            var rootItem = new OpcItem(parentNodeId ?? string.Empty);
            var filters = new TsCDaBrowseFilters
            {
                BrowseFilter = TsCDaBrowseFilter.All,
                ReturnAllProperties = false,
                ReturnPropertyValues = false
            };

            var result = new List<OpcNode>();
            lock (_comLock)
            {
                TsCDaBrowsePosition? position;
                var elements = _server!.Browse(rootItem, filters, out position);
                AppendElements(elements, result);

                // BrowseNext 继续未取完的批次（大命名空间分页）
                while (position is not null)
                {
                    var more = _server.BrowseNext(ref position);
                    AppendElements(more, result);
                }
            }
            return result;
        }, ct);
    }

    public Task<IReadOnlyList<string>> EnumerateServersAsync(string? host = null, CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            using var discovery = new ServerEnumerator();
            // 走 DA 3.0；老服务器同时支持 2.0/3.0，扫到的几乎是同一组。host 为空时枚举本机。
            var servers = string.IsNullOrWhiteSpace(host)
                ? discovery.GetAvailableServers(OpcSpecification.OPC_DA_30)
                : discovery.GetAvailableServers(OpcSpecification.OPC_DA_30, host, null);

            return (servers ?? Array.Empty<OpcServer>())
                .Where(s => s?.Url is not null)
                .Select(s => s.Url.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);
    }

    private static void AppendElements(TsCDaBrowseElement[]? elements, List<OpcNode> sink)
    {
        if (elements is null) return;
        foreach (var e in elements)
        {
            var kind = e.IsItem ? OpcNodeKind.Item : OpcNodeKind.Folder;
            // DA Browse 给出的 ItemName 就是后续 AddItems 用的 OPC 项 ID，原样回传
            var id = string.IsNullOrEmpty(e.ItemName) ? e.Name : e.ItemName;
            var label = string.IsNullOrEmpty(e.Name) ? id : e.Name;
            sink.Add(new OpcNode(id, label, kind, e.HasChildren));
        }
    }

    private static string BuildOpcDaUrl(OpcConnectionOptions options)
    {
        var uri = options.ServerUri?.Trim() ?? string.Empty;
        if (uri.StartsWith("opcda://", StringComparison.OrdinalIgnoreCase))
            return uri;

        var progId = options.ServerProgId
            ?? throw new InvalidOperationException(
                "OPC DA 浏览需要 ServerProgId（或完整 opcda:// URL）");
        var host = string.IsNullOrWhiteSpace(uri) ? "localhost" : uri;

        // 显式 CLSID → vendor Factory.Connect 解析 path 时拆 progId/clsid，跳过 OPCEnum 查表
        // 用法：OPCEnum 不可用 / 注册表异常 / 强制锁定版本时
        var clsid = options.ServerClsid?.Trim();
        if (!string.IsNullOrEmpty(clsid))
        {
            if (!clsid.StartsWith("{")) clsid = "{" + clsid + "}";
            return $"opcda://{host}/{progId}/{clsid}";
        }
        return $"opcda://{host}/{progId}";
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        lock (_comLock)
        {
            try { _server?.Disconnect(); } catch { }
            try { _server?.Dispose(); } catch { }
            _server = null;
        }
        return ValueTask.CompletedTask;
    }
}
