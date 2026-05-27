using Dc.Opc.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Dc.Opc.Ua;

public sealed class OpcUaBrowser : IOpcBrowser
{
    private Session? _session;
    private bool _disposed;

    public async Task ConnectAsync(OpcConnectionOptions options, CancellationToken ct = default)
    {
        var appConfig = OpcUaApplicationConfig.Build(options.ConnectTimeout);
        await appConfig.Validate(ApplicationType.Client).ConfigureAwait(false);

        var appInstance = new ApplicationInstance
        {
            ApplicationName = appConfig.ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = appConfig
        };
        await appInstance.CheckApplicationInstanceCertificate(
            silent: true,
            minimumKeySize: OpcUaApplicationConfig.MinimumCertificateKeySize).ConfigureAwait(false);

        var endpointDescription = CoreClientUtils.SelectEndpoint(appConfig, options.ServerUri, useSecurity: options.UseSecurity);
        var configuredEndpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(appConfig));

        _session = await Session.Create(
            appConfig,
            configuredEndpoint,
            updateBeforeConnect: false,
            sessionName: "DcBrowser",
            sessionTimeout: 60000,
            identity: new UserIdentity(),
            preferredLocales: null).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<OpcNode>> BrowseAsync(string? parentNodeId = null, CancellationToken ct = default)
    {
        if (_session is null) throw new InvalidOperationException("ConnectAsync must be called first");

        var startNode = string.IsNullOrWhiteSpace(parentNodeId)
            ? ObjectIds.ObjectsFolder
            : new NodeId(parentNodeId);

        var browseDescriptions = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = startNode,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable | NodeClass.View | NodeClass.ObjectType | NodeClass.VariableType),
                ResultMask = (uint)BrowseResultMask.All
            }
        };

        _session.Browse(null, null, 0u, browseDescriptions,
            out BrowseResultCollection results,
            out DiagnosticInfoCollection _);

        var nodes = new List<OpcNode>();
        if (results.Count > 0 && results[0].References is { } refs)
        {
            foreach (var refDesc in refs)
            {
                var kind = refDesc.NodeClass == NodeClass.Variable ? OpcNodeKind.Item : OpcNodeKind.Folder;
                var nodeId = refDesc.NodeId.ToString();
                nodes.Add(new OpcNode(nodeId, refDesc.DisplayName?.Text ?? nodeId, kind, kind == OpcNodeKind.Folder));
            }
        }
        return Task.FromResult<IReadOnlyList<OpcNode>>(nodes);
    }

    public Task<IReadOnlyList<string>> EnumerateServersAsync(string? host = null, CancellationToken ct = default)
    {
        // UA server discovery requires LDS or known discovery endpoint per host.
        // Skipped in v1 — caller passes opc.tcp://host:port directly.
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _session?.Close(); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
        return ValueTask.CompletedTask;
    }
}
