namespace Dc.Opc.Abstractions;

public interface IOpcBrowser : IAsyncDisposable
{
    Task ConnectAsync(OpcConnectionOptions options, CancellationToken ct = default);
    Task<IReadOnlyList<OpcNode>> BrowseAsync(string? parentNodeId = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> EnumerateServersAsync(string? host = null, CancellationToken ct = default);

    // 读取单个节点的当前值与数据类型（节点详情面板用）。
    // 仅 UA 已 override；DA/AE 浏览器已实现 Browse/Enumerate，但尚未 override 本方法，
    // 故走默认返回 null（详情面板显示「—」）。后续可在 DA/AE 各自补 override。
    Task<OpcNodeValue?> ReadValueAsync(string nodeId, CancellationToken ct = default)
        => Task.FromResult<OpcNodeValue?>(null);

    // 一次读 N 个节点当前值；返回与入参等长、按序对应（读不到处为 null）。
    // 默认空实现（DA/AE 暂不 override，同 ReadValueAsync）。
    Task<IReadOnlyList<OpcNodeValue?>> ReadValuesAsync(IReadOnlyList<string> nodeIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OpcNodeValue?>>(new OpcNodeValue?[nodeIds.Count]);
}
