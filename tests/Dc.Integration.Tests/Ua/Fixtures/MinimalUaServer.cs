using Opc.Ua;
using Opc.Ua.Server;

namespace Dc.Integration.Tests.Ua.Fixtures;

// 极简 UA Server：只挂一个 MinimalUaNodeManager。其他能力按 Foundation StandardServer 默认。
internal sealed class MinimalUaServer : StandardServer
{
    private readonly int _extraIntVars;
    private readonly int _stressNodes;
    private readonly TimeSpan _stressTick;
    private MinimalUaNodeManager? _nodeManager;

    public MinimalUaServer(int extraIntVars = 0, int stressNodes = 0, TimeSpan stressTick = default)
    {
        _extraIntVars = extraIntVars;
        _stressNodes = stressNodes;
        _stressTick = stressTick;
    }

    public int StressTickCount => _nodeManager?.StressTickCount ?? 0;

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        _nodeManager = new MinimalUaNodeManager(server, configuration, _extraIntVars, _stressNodes, _stressTick);
        var nodeManagers = new INodeManager[] { _nodeManager };
        return new MasterNodeManager(server, configuration, dynamicNamespaceUri: null, nodeManagers);
    }
}
