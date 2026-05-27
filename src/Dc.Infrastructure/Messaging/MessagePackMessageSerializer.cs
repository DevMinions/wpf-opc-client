using MessagePack;
using MessagePack.Resolvers;

namespace Dc.Infrastructure.Messaging;

public class MessagePackMessageSerializer : IMessageSerializer
{
    private static readonly MessagePackSerializerOptions Options =
        ContractlessStandardResolver.Options;

    public string FormatId => "msgpack";

    public byte[] Serialize<T>(T value) =>
        MessagePackSerializer.Serialize(value, Options);

    public T Deserialize<T>(byte[] data) =>
        MessagePackSerializer.Deserialize<T>(data, Options);
}
