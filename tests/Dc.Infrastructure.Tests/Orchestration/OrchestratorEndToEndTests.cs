using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Infrastructure.Tests.Fakes;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Infrastructure.Tests.Orchestration;

public class OrchestratorEndToEndTests
{
    [Fact]
    public async Task FakeSubscriber_RealTcpPublisher_DeliversMessagePackBytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serializer = new MessagePackMessageSerializer();
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ns = client.GetStream();
            var lenBuf = new byte[4];
            await ns.ReadExactlyAsync(lenBuf);
            var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            var frame = new byte[len];
            await ns.ReadExactlyAsync(frame);
            // v1.1: 跳过 2 字节 header (magic + format-id)
            return serializer.Deserialize<TagValue>(frame[2..]);
        });

        var subFactory = new FakeOpcSubscriberFactory(OpcProtocol.Da);
        var pubFactory = new TcpPublisherFactory(serializer);

        await using var orch = new TaskOrchestrator(
            new[] { (IOpcSubscriberFactory)subFactory },
            pubFactory);

        var req = new TaskStartRequest(
            "e2e-1",
            OpcProtocol.Da,
            new OpcConnectionOptions { ServerUri = "unused" },
            $"127.0.0.1:{port}",
            Array.Empty<TagDescriptor>());
        await orch.StartAsync(req);

        var fakeSub = subFactory.Created.First();
        var sample = new TagValue("Random.Real8", 42.5, 0xC0, DateTimeOffset.UtcNow);
        fakeSub.EmitValue(sample);

        var received = await serverTask;
        Assert.Equal("Random.Real8", received.Item);
        Assert.Equal((ushort)0xC0, received.Quality);
        Assert.True(received.IsGood);
    }

    [Fact]
    public async Task TagValueReceivedEvent_FiresForEveryValue()
    {
        var subFactory = new FakeOpcSubscriberFactory(OpcProtocol.Da);
        var pubFactory = new FakePublisherFactory();
        await using var orch = new TaskOrchestrator(
            new[] { (IOpcSubscriberFactory)subFactory },
            pubFactory);

        var received = new List<(string TaskId, TagValue Value)>();
        orch.TagValueReceived += (taskId, v) => received.Add((taskId, v));

        await orch.StartAsync(new TaskStartRequest(
            "evt-1",
            OpcProtocol.Da,
            new OpcConnectionOptions { ServerUri = "x" },
            "127.0.0.1:5000",
            Array.Empty<TagDescriptor>()));

        var sub = subFactory.Created.First();
        for (int i = 0; i < 3; i++)
            sub.EmitValue(new TagValue($"item-{i}", i, 0xC0, DateTimeOffset.UtcNow));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (received.Count < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.Equal(3, received.Count);
        Assert.All(received, r => Assert.Equal("evt-1", r.TaskId));
        Assert.Equal(new[] { "item-0", "item-1", "item-2" }, received.Select(r => r.Value.Item));
    }

    [Fact]
    public async Task EndToEnd_ScaleAndFormula_PublishesEngineeringAndVirtual()
    {
        var daFactory = new FakeOpcSubscriberFactory(OpcProtocol.Da);
        var pubFactory = new FakePublisherFactory();
        await using var orch = new TaskOrchestrator(
            new[] { (IOpcSubscriberFactory)daFactory },
            pubFactory,
            transformFactory: new TagValueTransformFactory());

        var cfg = new TransformConfig(
            new Dictionary<string, ScaleConfig>
            {
                ["t1"] = new(0.1, 0),
                ["t2"] = new(1.0, 0)
            },
            new Dictionary<string, string> { ["t1"] = "A", ["t2"] = "B" },
            new[]
            {
                new FormulaConfig("f1", "OUT", "A + B",
                    new[] { new FormulaInputConfig("A", "t1"), new FormulaInputConfig("B", "t2") })
            });

        await orch.StartAsync(new TaskStartRequest(
            "e2e-scale-formula",
            OpcProtocol.Da,
            new OpcConnectionOptions { ServerUri = "opc.tcp://localhost:4840" },
            "127.0.0.1:5000",
            new[] { new TagDescriptor("t1", "A", 6), new TagDescriptor("t2", "B", 6) },
            cfg));

        var sub = daFactory.Created.First();
        var pub = pubFactory.Created.First().Publisher;

        sub.EmitValue(new TagValue("A", 100.0, 0xC0, DateTimeOffset.UtcNow));
        sub.EmitValue(new TagValue("B", 5.0, 0xC0, DateTimeOffset.UtcNow));

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!pub.Published.OfType<TagValue>().Any(v => v.Item == "OUT") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var published = pub.Published.OfType<TagValue>().ToArray();
        Assert.Contains(published, v => v.Item == "A" && (double)v.Value! == 10.0);

        var virt = published.Last(v => v.Item == "OUT");
        Assert.Equal(15.0, virt.Value);
        Assert.Equal((ushort)0xC0, virt.Quality);
    }
}
