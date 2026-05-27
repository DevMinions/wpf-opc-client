namespace Dc.Infrastructure.Messaging;

public interface IPublisherFactory
{
    IPublisher Create(string address);
}
