using System.Collections.Concurrent;
using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Tests.Fakes;

public sealed class FakeOpcSubscriberFactory : IOpcSubscriberFactory
{
    public OpcProtocol Protocol { get; }
    public ConcurrentQueue<FakeOpcSubscriber> Created { get; } = new();

    public FakeOpcSubscriberFactory(OpcProtocol protocol) => Protocol = protocol;

    public IOpcSubscriber Create(string channelId, OpcConnectionOptions options)
    {
        var sub = new FakeOpcSubscriber(channelId, options);
        Created.Enqueue(sub);
        return sub;
    }
}
