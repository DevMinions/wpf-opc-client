using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dc.Infrastructure.Messaging;

// System.Text.Json 序列化器：人类可读、调试/抓包友好，但比 MessagePack 大 2~3 倍。
// TagValue.Value 是 object?（AE 事件时是 Dictionary<string,object?>），STJ 默认能写出
// 嵌套对象/字典；反序列化时 object 会变 JsonElement，需要消费方自行二次解析。生产侧主要写出，
// 反序列化是订阅端的事，所以这里默认行为足够。
public class JsonMessageSerializer : IMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // 默认 camelCase，方便其他语言的订阅端消费
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // null 字段写出（消费方按需过滤），方便统一 schema
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // 调试可读，但代价是包大；要更小可改 false
        WriteIndented = false,
        // DateTimeOffset 用 ISO 8601（默认）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string FormatId => "json";

    public byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public T Deserialize<T>(byte[] data) =>
        JsonSerializer.Deserialize<T>(data, Options)
        ?? throw new InvalidOperationException("反序列化得到 null");
}
