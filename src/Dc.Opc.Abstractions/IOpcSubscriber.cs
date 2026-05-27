using System.Threading.Channels;

namespace Dc.Opc.Abstractions;

public interface IOpcSubscriber : IAsyncDisposable
{
    string ChannelId { get; }
    ChannelReader<TagValue> TagValues { get; }
    ChannelReader<HeartBeat> Heartbeats { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task SubscribeAsync(IReadOnlyCollection<TagDescriptor> tags, CancellationToken ct = default);
    Task UnsubscribeAsync(IReadOnlyCollection<string> tagItems, CancellationToken ct = default);
}
