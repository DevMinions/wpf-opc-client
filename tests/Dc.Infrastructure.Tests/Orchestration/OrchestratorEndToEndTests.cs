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
}
