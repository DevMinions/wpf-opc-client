using Dc.Opc.Abstractions;
using Technosoftware.DaAeHdaClient;
using Technosoftware.DaAeHdaClient.Ae;
using ComFactory = Technosoftware.DaAeHdaClient.Com.Factory;
using ServerEnumerator = Technosoftware.DaAeHdaClient.Com.ServerEnumerator;

namespace Dc.Opc.Ae;

// AE 浏览器：与 DA 不同，AE 的命名空间是 Area(容器) + Source(叶子) 两类。
//   - ConnectAsync: 连 TsCAeServer
//   - BrowseAsync(areaId): 同一个 areaId 上调两次 Browse（Area + Source），合并成 OpcNode 列表
//     * Area  → OpcNode.Folder（HasChildren=true，可下钻）
//     * Source → OpcNode.Item   （叶子，QualifiedName = SourceID，可直接当 Tag.Item 使用）
//   - EnumerateServersAsync: ServerEnumerator + OPC_AE_10 列同 IP/主机上的 AE 服务器
//
// AE 还有 Event Categories / Conditions 维度（QueryEventCategories/QueryConditionNames），
// 但首版用户最关心的是"我有哪些事件源" → 只暴露 Area/Source 树。Category/Condition 维度后续可加。
public sealed class OpcAeBrowser : IOpcBrowser
{
    private readonly object _comLock = new();
    private TsCAeServer? _server;
    private bool _disposed;

    public Task ConnectAsync(OpcConnectionOptions options, CancellationToken ct = default)
    {
        var opcUrl = new OpcUrl(BuildOpcAeUrl(options));
        return Task.Run(() =>
        {
            lock (_comLock)
            {
                _server = new TsCAeServer(new ComFactory(), opcUrl);
                _server.Connect();
            }
        }, ct);
    }

    public Task<IReadOnlyList<OpcNode>> BrowseAsync(string? parentNodeId = null, CancellationToken ct = default)
    {
        if (_server is null) throw new InvalidOperationException("ConnectAsync 必须先调用");

        return Task.Run<IReadOnlyList<OpcNode>>(() =>
        {
            var area = parentNodeId ?? string.Empty; // 空串 = 根
            var result = new List<OpcNode>();
            lock (_comLock)
            {
                // 子区域（可下钻）
                foreach (var e in _server!.Browse(area, TsCAeBrowseType.Area, string.Empty) ?? Array.Empty<TsCAeBrowseElement>())
                    result.Add(ToNode(e, isFolder: true));
                // 子源（叶子，QualifiedName 即 SourceID）
                foreach (var e in _server!.Browse(area, TsCAeBrowseType.Source, string.Empty) ?? Array.Empty<TsCAeBrowseElement>())
                    result.Add(ToNode(e, isFolder: false));
            }
            return result;
        }, ct);
    }

    public Task<IReadOnlyList<string>> EnumerateServersAsync(string? host = null, CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            using var discovery = new ServerEnumerator();
            var servers = string.IsNullOrWhiteSpace(host)
                ? discovery.GetAvailableServers(OpcSpecification.OPC_AE_10)
                : discovery.GetAvailableServers(OpcSpecification.OPC_AE_10, host, null);

            return (servers ?? Array.Empty<OpcServer>())
                .Where(s => s?.Url is not null)
                .Select(s => s.Url.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);
    }

    private static OpcNode ToNode(TsCAeBrowseElement e, bool isFolder)
    {
        // 用 QualifiedName 当 NodeId — 这是 SourceID 的正式形式（如 "ReactorA/CoolingLoop/HiTempSensor"）
        var id = string.IsNullOrEmpty(e.QualifiedName) ? e.Name : e.QualifiedName;
        var label = string.IsNullOrEmpty(e.Name) ? id : e.Name;
        return new OpcNode(id, label, isFolder ? OpcNodeKind.Folder : OpcNodeKind.Item, HasChildren: isFolder);
    }

    private static string BuildOpcAeUrl(OpcConnectionOptions options)
    {
        var uri = options.ServerUri?.Trim() ?? string.Empty;
        if (uri.StartsWith("opcae://", StringComparison.OrdinalIgnoreCase))
            return uri;

        var progId = options.ServerProgId
            ?? throw new InvalidOperationException(
                "OPC AE 浏览需要 ServerProgId（或完整 opcae:// URL）");
        var host = string.IsNullOrWhiteSpace(uri) ? "localhost" : uri;

        var clsid = options.ServerClsid?.Trim();
        if (!string.IsNullOrEmpty(clsid))
        {
            if (!clsid.StartsWith("{")) clsid = "{" + clsid + "}";
            return $"opcae://{host}/{progId}/{clsid}";
        }
        return $"opcae://{host}/{progId}";
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
