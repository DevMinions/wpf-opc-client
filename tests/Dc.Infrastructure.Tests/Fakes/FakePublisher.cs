using System.Collections.Concurrent;
using Dc.Infrastructure.Messaging;

namespace Dc.Infrastructure.Tests.Fakes;

public sealed class FakePublisher : IPublisher
{
    public ConcurrentQueue<object> Published { get; } = new();
    public bool Disposed { get; private set; }

    // 可选：每次发布的人为延迟，用于让值在通道里积压（测试停止 drain）。默认 0 = 立即返回。
    public TimeSpan PublishDelay { get; set; } = TimeSpan.Zero;

    public async Task PublishAsync<T>(T message, CancellationToken ct = default)
    {
        if (PublishDelay > TimeSpan.Zero)
            await Task.Delay(PublishDelay, ct).ConfigureAwait(false);
        Published.Enqueue(message!);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakePublisherFactory : IPublisherFactory
{
    public ConcurrentQueue<(string Address, FakePublisher Publisher)> Created { get; } = new();

    public IPublisher Create(string address)
    {
        var pub = new FakePublisher();
        Created.Enqueue((address, pub));
        return pub;
    }
}
