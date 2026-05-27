using Dc.Opc.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dc.Opc.Da;

public sealed class OpcDaSubscriberFactory(ILoggerFactory? loggerFactory = null) : IOpcSubscriberFactory
{
    public OpcProtocol Protocol => OpcProtocol.Da;

    public IOpcSubscriber Create(string channelId, OpcConnectionOptions options)
        => new OpcDaSubscriber(channelId, options, loggerFactory?.CreateLogger<OpcDaSubscriber>());
}
