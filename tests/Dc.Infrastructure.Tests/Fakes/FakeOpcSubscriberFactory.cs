using System.Collections.Concurrent;
using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Tests.Fakes;

public sealed class FakeOpcSubscriberFactory : IOpcSubscriberFactory
{
    public OpcProtocol Protocol { get; }
    public ConcurrentQueue<FakeOpcSubscriber> Created { get; } = new();

    /// <summary>置 true 后，此工厂之后 Create 出的每个订阅器 ConnectAsync 都会抛——模拟 server 持续不可达。</summary>
    public bool ThrowOnConnectForFutureCreates { get; set; }

    public FakeOpcSubscriberFactory(OpcProtocol protocol) => Protocol = protocol;

    public IOpcSubscriber Create(string channelId, OpcConnectionOptions options)
    {
        var sub = new FakeOpcSubscriber(channelId, options) { ThrowOnConnect = ThrowOnConnectForFutureCreates };
        Created.Enqueue(sub);
        return sub;
    }
}
