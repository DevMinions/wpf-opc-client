namespace Dc.Infrastructure.Messaging;

public interface IPublisher : IAsyncDisposable
{
    Task PublishAsync<T>(T message, CancellationToken ct = default);
}
