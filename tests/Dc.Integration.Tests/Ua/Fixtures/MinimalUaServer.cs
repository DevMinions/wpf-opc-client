using Opc.Ua;
using Opc.Ua.Server;

namespace Dc.Integration.Tests.Ua.Fixtures;

// 极简 UA Server：只挂一个 MinimalUaNodeManager。其他能力按 Foundation StandardServer 默认。
internal sealed class MinimalUaServer : StandardServer
{
    private readonly int _extraIntVars;

    public MinimalUaServer(int extraIntVars = 0) => _extraIntVars = extraIntVars;

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        var nodeManagers = new INodeManager[]
        {
            new MinimalUaNodeManager(server, configuration, _extraIntVars)
        };
        return new MasterNodeManager(server, configuration, dynamicNamespaceUri: null, nodeManagers);
    }
}
