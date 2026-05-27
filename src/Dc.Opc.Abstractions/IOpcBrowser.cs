namespace Dc.Opc.Abstractions;

public interface IOpcBrowser : IAsyncDisposable
{
    Task ConnectAsync(OpcConnectionOptions options, CancellationToken ct = default);
    Task<IReadOnlyList<OpcNode>> BrowseAsync(string? parentNodeId = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> EnumerateServersAsync(string? host = null, CancellationToken ct = default);

    // 读取单个节点的当前值与数据类型（节点详情面板用）。
    // 默认未实现返回 null：UA 已实现；DA/AE 待 Windows 端补齐（届时各自 override）。
    Task<OpcNodeValue?> ReadValueAsync(string nodeId, CancellationToken ct = default)
        => Task.FromResult<OpcNodeValue?>(null);
}
