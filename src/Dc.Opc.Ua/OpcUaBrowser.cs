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

        var endpointDescription = CoreClientUtils.SelectEndpoint(appConfig, options.ServerUri, useSecurity: options.UseSecurity && OpcUaApplicationConfig.UseSecurity);
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

    public Task<OpcNodeValue?> ReadValueAsync(string nodeId, CancellationToken ct = default)
    {
        if (_session is null) throw new InvalidOperationException("ConnectAsync must be called first");

        var id = new NodeId(nodeId);
        var toRead = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = id, AttributeId = Attributes.Value },
            new ReadValueId { NodeId = id, AttributeId = Attributes.DataType }
        };
        _session.Read(null, 0, TimestampsToReturn.Source, toRead,
            out DataValueCollection results, out DiagnosticInfoCollection _);

        var valueDv = results[0];
        var dataTypeDv = results.Count > 1 ? results[1] : null;

        var result = BuildNodeValue(valueDv, dataTypeDv);
        return Task.FromResult<OpcNodeValue?>(result);
    }

    public async Task<IReadOnlyList<OpcNodeValue?>> ReadValuesAsync(
        IReadOnlyList<string> nodeIds, CancellationToken ct = default)
    {
        if (_session is null) throw new InvalidOperationException("ConnectAsync must be called first");
        var results = new OpcNodeValue?[nodeIds.Count];
        if (nodeIds.Count == 0) return results;

        await Task.Run(() => ReadValuesCore(nodeIds, results), ct).ConfigureAwait(false);
        return results;
    }

    // 同步分块读（在后台线程跑，避免阻塞 UI）。结果写入传入的 results 数组。
    private void ReadValuesCore(IReadOnlyList<string> nodeIds, OpcNodeValue?[] results)
    {
        // 每节点 2 个 ReadValueId（Value+DataType）；按服务器 MaxNodesPerRead/2 分块，未知则 500。
        uint maxNodes = _session!.OperationLimits?.MaxNodesPerRead ?? 0u;
        int chunk = maxNodes > 1 ? (int)(maxNodes / 2) : 500;
        if (chunk < 1) chunk = 500;

        for (var start = 0; start < nodeIds.Count; start += chunk)
        {
            var end = Math.Min(start + chunk, nodeIds.Count);
            var toRead = new ReadValueIdCollection();
            for (var i = start; i < end; i++)
            {
                var id = new NodeId(nodeIds[i]);
                toRead.Add(new ReadValueId { NodeId = id, AttributeId = Attributes.Value });
                toRead.Add(new ReadValueId { NodeId = id, AttributeId = Attributes.DataType });
            }
            _session.Read(null, 0, TimestampsToReturn.Source, toRead,
                out DataValueCollection dvs, out DiagnosticInfoCollection _);
            for (var i = start; i < end; i++)
            {
                var k = (i - start) * 2;
                results[i] = BuildNodeValue(dvs[k], k + 1 < dvs.Count ? dvs[k + 1] : null);
            }
        }
    }

    // 由一对 Value/DataType DataValue 组装 OpcNodeValue（质量位运算 + 类型解析）。
    private OpcNodeValue BuildNodeValue(DataValue valueDv, DataValue? dataTypeDv)
    {
        // 质量码同 TagValue 的位运算约定
        ushort quality;
        if (StatusCode.IsBad(valueDv.StatusCode)) quality = 0x00;
        else if (StatusCode.IsUncertain(valueDv.StatusCode)) quality = 0x40;
        else quality = 0xC0;

        DateTimeOffset? ts = valueDv.SourceTimestamp == DateTime.MinValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(valueDv.SourceTimestamp, DateTimeKind.Utc));

        return new OpcNodeValue(ResolveDataType(valueDv, dataTypeDv), valueDv.Value, quality, ts);
    }

    // 优先用值自带的类型信息（含标量/数组）；值空或坏时退而解析 DataType 属性（一个数据类型 NodeId）。
    private string ResolveDataType(DataValue valueDv, DataValue? dataTypeDv)
    {
        var ti = valueDv.WrappedValue.TypeInfo;
        if (ti is not null && ti.BuiltInType != BuiltInType.Null)
        {
            var suffix = ti.ValueRank >= ValueRanks.OneDimension ? "[]" : "";
            return ti.BuiltInType.ToString() + suffix;
        }
        if (dataTypeDv?.Value is NodeId dtId && _session is not null)
        {
            var builtIn = TypeInfo.GetBuiltInType(dtId, _session.TypeTree);
            if (builtIn != BuiltInType.Null) return builtIn.ToString();
            var node = _session.NodeCache.Find(dtId);
            if (node?.DisplayName?.Text is { Length: > 0 } name) return name;
            return dtId.ToString();
        }
        return "Unknown";
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
