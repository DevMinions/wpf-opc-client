namespace Dc.Infrastructure.Messaging;

// wire 协议常量（v1.1）。完整规范见 docs/wire-format.md。
//
// 每帧结构：
//   [4B BE length][1B magic][1B format-id][payload]
//   length 包含 magic + format-id + payload 三段
//
// magic = 0xDC 用来跟 v1.0 raw 帧（直接 msgpack/json）区分；0xDC 在合法 msgpack
// 起始字节里是 bin-32（携带 4B 长度），实际从不在我们 TagValue 序列化里出现，
// 收端见到 0xDC 即可断定是 v1.1。
public static class WireFormat
{
    public const byte MagicV11 = 0xDC;

    // format-id 复用 IMessageSerializer.FormatId 但用单字节标识省解析成本
    public const byte FormatMsgpack = 0x01;
    public const byte FormatJson    = 0x02;

    public const int HeaderSize = 2; // magic + format-id

    public static byte FormatIdFor(string formatId) => formatId switch
    {
        "msgpack" => FormatMsgpack,
        "json"    => FormatJson,
        _ => throw new InvalidOperationException($"未知 format id '{formatId}' — 添加到 WireFormat.FormatIdFor")
    };

    public static string FormatNameFor(byte id) => id switch
    {
        FormatMsgpack => "msgpack",
        FormatJson    => "json",
        _ => $"unknown(0x{id:X2})"
    };
}
