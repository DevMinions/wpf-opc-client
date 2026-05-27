namespace Dc.Opc.Abstractions;

public interface IOpcSubscriberFactory
{
    OpcProtocol Protocol { get; }
    IOpcSubscriber Create(string channelId, OpcConnectionOptions options);
}
