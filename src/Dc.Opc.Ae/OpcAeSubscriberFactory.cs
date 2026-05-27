using Dc.Opc.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dc.Opc.Ae;

public sealed class OpcAeSubscriberFactory(ILoggerFactory? loggerFactory = null) : IOpcSubscriberFactory
{
    public OpcProtocol Protocol => OpcProtocol.Ae;

    public IOpcSubscriber Create(string channelId, OpcConnectionOptions options)
        => new OpcAeSubscriber(channelId, options, loggerFactory?.CreateLogger<OpcAeSubscriber>());
}
