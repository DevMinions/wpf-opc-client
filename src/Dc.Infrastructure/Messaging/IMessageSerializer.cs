namespace Dc.Infrastructure.Messaging;

public interface IMessageSerializer
{
    string FormatId { get; }
    byte[] Serialize<T>(T value);
    T Deserialize<T>(byte[] data);
}
