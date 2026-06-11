using System.Threading;
using Opc.Ua;
using Opc.Ua.Server;

namespace Dc.Integration.Tests.Ua.Fixtures;

// 最小 UA NodeManager：在自定义 namespace 下暴露一个 Folder + 一个可读 Int32 变量。
// 目的是给客户端订阅 / 浏览的最少有效对象，避开 ReferenceServer 的大体量。
internal sealed class MinimalUaNodeManager : CustomNodeManager2
{
    public const string TestNamespace = "urn:dc:integrationtest:ua";
    public const string DemoIntId = "Demo.Int32";

    private BaseDataVariableState? _demoInt;
    private Timer? _ticker;
    private readonly int _extraIntVars;

    public MinimalUaNodeManager(IServerInternal server, ApplicationConfiguration configuration, int extraIntVars = 0)
        : base(server, configuration, TestNamespace)
    {
        _extraIntVars = extraIntVars;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            }

            var folder = new FolderState(null)
            {
                NodeId = new NodeId("Demo", NamespaceIndex),
                BrowseName = new QualifiedName("Demo", NamespaceIndex),
                DisplayName = new LocalizedText("Demo"),
                TypeDefinitionId = ObjectTypeIds.FolderType,
                EventNotifier = EventNotifiers.None
            };
            folder.AddReference(ReferenceTypeIds.Organizes, isInverse: true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));

            _demoInt = new BaseDataVariableState(folder)
            {
                NodeId = new NodeId(DemoIntId, NamespaceIndex),
                BrowseName = new QualifiedName(DemoIntId, NamespaceIndex),
                DisplayName = new LocalizedText(DemoIntId),
                DataType = DataTypeIds.Int32,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                Value = 0,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow
            };
            folder.AddChild(_demoInt);

            for (var i = 0; i < _extraIntVars; i++)
            {
                var v = new BaseDataVariableState(folder)
                {
                    NodeId = new NodeId($"Bench.{i}", NamespaceIndex),
                    BrowseName = new QualifiedName($"Bench.{i}", NamespaceIndex),
                    DisplayName = new LocalizedText($"Bench.{i}"),
                    DataType = DataTypeIds.Int32,
                    ValueRank = ValueRanks.Scalar,
                    AccessLevel = AccessLevels.CurrentRead,
                    UserAccessLevel = AccessLevels.CurrentRead,
                    Value = i,
                    StatusCode = StatusCodes.Good,
                    Timestamp = DateTime.UtcNow
                };
                folder.AddChild(v);
            }

            AddPredefinedNode(SystemContext, folder);

            // 每 200ms 自增一次，确保订阅端能拿到变化通知
            _ticker = new Timer(_ =>
            {
                lock (Lock)
                {
                    if (_demoInt is null) return;
                    _demoInt.Value = ((int)_demoInt.Value!) + 1;
                    _demoInt.Timestamp = DateTime.UtcNow;
                    _demoInt.StatusCode = StatusCodes.Good;
                    _demoInt.ClearChangeMasks(SystemContext, includeChildren: false);
                }
            }, state: null, dueTime: TimeSpan.FromMilliseconds(200), period: TimeSpan.FromMilliseconds(200));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ticker?.Dispose();
            _ticker = null;
        }
        base.Dispose(disposing);
    }
}
