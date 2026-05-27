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
}
