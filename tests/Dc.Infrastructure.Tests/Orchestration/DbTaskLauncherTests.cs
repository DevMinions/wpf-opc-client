using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class DbTaskLauncherTests
{
    [Fact]
    public void ToStartRequest_MapsAllFields()
    {
        var task = new CollectorTask
        {
            Id = "t1",
            Type = (byte)OpcProtocol.Ua,
            Node = "opc.tcp://h:4840",
            Server = "Prog.Id",
            Clsid = "{guid}",
            Interval = 500,
            Deviation = 3,
            TcpAddress = "127.0.0.1:9000"
        };
        task.Tags.Add(new Tag { Id = "tag1", Item = "ns=2;s=X", DataType = 11 });
        task.Tags.Add(new Tag { Id = "tag2", Item = "ns=2;s=Y", DataType = 1 });

        var req = DbTaskLauncher.ToStartRequest(task);

        Assert.Equal("t1", req.TaskId);
        Assert.Equal(OpcProtocol.Ua, req.Protocol);
        Assert.Equal("opc.tcp://h:4840", req.OpcOptions.ServerUri);
        Assert.Equal("Prog.Id", req.OpcOptions.ServerProgId);
        Assert.Equal("{guid}", req.OpcOptions.ServerClsid);
        Assert.Equal(500, req.OpcOptions.SamplingInterval.TotalMilliseconds);
        Assert.Equal(3, req.OpcOptions.DeadbandPercent);
        Assert.Equal("127.0.0.1:9000", req.PublisherAddress);
        Assert.Equal(2, req.Tags.Count);
        Assert.Contains(req.Tags, t => t.Item == "ns=2;s=X" && t.DataType == 11);
    }

    [Fact]
    public void ToStartRequest_ClampsZeroIntervalToOneMs()
    {
        var task = new CollectorTask { Id = "t", Type = (byte)OpcProtocol.Ua, Node = "x", Interval = 0 };
        var req = DbTaskLauncher.ToStartRequest(task);
        Assert.Equal(1, req.OpcOptions.SamplingInterval.TotalMilliseconds);
    }

    [Fact]
    public void ToStartRequest_UaUrlInServerField_TakesServerAsServerUri()
    {
        // 编辑器「服务器」字段存 UA URL、「节点」字段留 localhost（用户实际输入形态）。
        // 回归：URL 必须进 ServerUri，不能把 localhost 当 discoveryUrl（曾致 UriFormatException）。
        var task = new CollectorTask
        {
            Id = "t",
            Type = (byte)OpcProtocol.Ua,
            Server = "opc.tcp://DESKTOP-KONUSAK:53530/OPCUA/SimulationServer",
            Node = "localhost",
            Interval = 1000
        };

        var req = DbTaskLauncher.ToStartRequest(task);

        Assert.Equal("opc.tcp://DESKTOP-KONUSAK:53530/OPCUA/SimulationServer", req.OpcOptions.ServerUri);
        Assert.Null(req.OpcOptions.ServerProgId);
    }

    [Fact]
    public void ToStartRequest_ExcludesVirtualTags_FromSubscriber()
    {
        var real = new Tag { Id = "r1", Item = "A", DataType = 6, IsVirtual = false };
        var virt = new Tag { Id = "v1", Item = "OUT", DataType = 6, IsVirtual = true };
        var task = TaskWithTags(real, virt);

        var req = DbTaskLauncher.ToStartRequest(task);

        Assert.Single(req.Tags);
        Assert.Equal("A", req.Tags.Single().Item);
    }

    [Fact]
    public void ToStartRequest_BuildsTransformConfig_WithFormulas()
    {
        var real = new Tag { Id = "r1", Item = "A", DataType = 6, IsVirtual = false, ScaleFactor = 0.1 };
        var virt = new Tag { Id = "v1", Item = "OUT", DataType = 6, IsVirtual = true };
        var task = TaskWithTags(real, virt);
        var formula = new Formula
        {
            Id = "f1",
            Name = "OUT",
            Expression = "A*2",
            OutputTagId = "v1",
            TaskId = "t1",
            Inputs = new()
            {
                new FormulaInput { Id = "fi1", FormulaId = "f1", Alias = "A", SourceTagId = "r1" }
            }
        };

        var req = DbTaskLauncher.ToStartRequest(task, new[] { formula });

        Assert.NotNull(req.TransformConfig);
        Assert.Single(req.TransformConfig!.Formulas);
        Assert.Equal("OUT", req.TransformConfig.Formulas[0].OutputItem);
        Assert.Equal(0.1, req.TransformConfig.ScaleByTagId["r1"].ScaleFactor);
    }

    [Fact]
    public void ToStartRequest_NoFormulas_NullTransformConfig()
    {
        var real = new Tag { Id = "r1", Item = "A", DataType = 6, IsVirtual = false };
        var task = TaskWithTags(real);

        var req = DbTaskLauncher.ToStartRequest(task);

        Assert.Null(req.TransformConfig);
    }

    private static CollectorTask TaskWithTags(params Tag[] tags)
    {
        var task = new CollectorTask
        {
            Id = "t1",
            Type = (byte)OpcProtocol.Ua,
            Node = "opc.tcp://localhost:4840",
            TcpAddress = "127.0.0.1:5000"
        };
        task.Tags = tags.ToList();
        foreach (var tag in tags)
        {
            tag.TaskId = task.Id;
        }
        return task;
    }
}
