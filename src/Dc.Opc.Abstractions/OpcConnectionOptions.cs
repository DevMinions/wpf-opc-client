namespace Dc.Opc.Abstractions;

public sealed record OpcConnectionOptions
{
    public required string ServerUri { get; init; }
    public string? ServerProgId { get; init; }
    // DA 兜底：直接指定 CLSID。给值时 BuildOpcDaUrl 拼成 opcda://host/progId/{clsid}，vendor 直接吃 GUID 跳过 OPCEnum
    public string? ServerClsid { get; init; }
    public TimeSpan SamplingInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int DeadbandPercent { get; init; } = 0;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    // OPC UA：是否要求带签名/加密的安全端点。默认 true（Phase 8b 安全基线，不可降级）。
    // 仅 dev 连接到只暴露 SecurityPolicy=None 的测试 server 时显式置 false。
    public bool UseSecurity { get; init; } = true;

    // OPC UA：会话 KeepAlive 探测间隔。服务器不可达/重启时据此最快发现并触发自动重连。默认 10s。
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);
    // OPC UA：断线后 SessionReconnectHandler 的重连重试间隔。默认 5s。
    public TimeSpan ReconnectPeriod { get; init; } = TimeSpan.FromSeconds(5);
}
