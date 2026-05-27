namespace Dc.Opc.Abstractions;

public sealed record HeartBeat(string ChannelId, DateTimeOffset Time, string? ServerInfo = null);
