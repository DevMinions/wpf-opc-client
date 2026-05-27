namespace Dc.Opc.Abstractions;

public interface IOpcBrowser : IAsyncDisposable
{
    Task ConnectAsync(OpcConnectionOptions options, CancellationToken ct = default);
    Task<IReadOnlyList<OpcNode>> BrowseAsync(string? parentNodeId = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> EnumerateServersAsync(string? host = null, CancellationToken ct = default);
}
