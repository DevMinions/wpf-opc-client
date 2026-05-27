using System.Threading.Channels;
using Dc.Opc.Abstractions;

namespace Dc.Infrastructure.Tests.Fakes;

public sealed class FakeOpcSubscriber : IOpcSubscriber
{
    private readonly Channel<TagValue> _values = Channel.CreateUnbounded<TagValue>();
    private readonly Channel<HeartBeat> _heartbeats = Channel.CreateUnbounded<HeartBeat>();

    public string ChannelId { get; }
    public OpcConnectionOptions Options { get; }
    public ChannelReader<TagValue> TagValues => _values.Reader;
    public ChannelReader<HeartBeat> Heartbeats => _heartbeats.Reader;

    public int ConnectCalls { get; private set; }
    public List<TagDescriptor> Subscribed { get; } = new();
    public List<string> Unsubscribed { get; } = new();
    public bool Disposed { get; private set; }
    public bool ThrowOnConnect { get; set; }

    public FakeOpcSubscriber(string channelId, OpcConnectionOptions options)
    {
        ChannelId = channelId;
        Options = options;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        ConnectCalls++;
        if (ThrowOnConnect) throw new InvalidOperationException("ThrowOnConnect");
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(IReadOnlyCollection<TagDescriptor> tags, CancellationToken ct = default)
    {
        Subscribed.AddRange(tags);
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(IReadOnlyCollection<string> tagItems, CancellationToken ct = default)
    {
        Unsubscribed.AddRange(tagItems);
        return Task.CompletedTask;
    }

    public void EmitValue(TagValue v) => _values.Writer.TryWrite(v);
    public void EmitHeartbeat(HeartBeat h) => _heartbeats.Writer.TryWrite(h);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _values.Writer.TryComplete();
        _heartbeats.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
