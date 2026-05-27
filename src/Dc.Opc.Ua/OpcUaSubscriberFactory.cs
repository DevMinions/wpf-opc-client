using Dc.Opc.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dc.Opc.Ua;

public sealed class OpcUaSubscriberFactory(ILoggerFactory? loggerFactory = null) : IOpcSubscriberFactory
{
    public OpcProtocol Protocol => OpcProtocol.Ua;

    public IOpcSubscriber Create(string channelId, OpcConnectionOptions options)
        => new OpcUaSubscriber(channelId, options, loggerFactory?.CreateLogger<OpcUaSubscriber>());
}
