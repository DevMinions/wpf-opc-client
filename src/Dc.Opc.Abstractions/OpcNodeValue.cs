namespace Dc.Opc.Abstractions;

// 浏览时读取单个节点的当前值结果（节点详情面板用）。
// Quality 沿用 TagValue 约定：0xC0 GOOD / 0x40 UNCERTAIN / 0x00 BAD。
public sealed record OpcNodeValue(string DataType, object? Value, ushort Quality, DateTimeOffset? SourceTimestamp);
