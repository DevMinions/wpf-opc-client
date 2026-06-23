using System.Text.Json.Serialization;

namespace Dc.Domain.Entities;

public class CollectorTask : EntityBase
{
    // 用户可读名称(可选)。为空时列表/下拉回落 Server(DA ProgID / UA URL)。
    public string? Name { get; set; }

    /// <summary>列表/下拉/确认文案统一用的可读名:Name → Server → Id。</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name!
        : !string.IsNullOrWhiteSpace(Server) ? Server
        : Id;

    public string Server { get; set; } = string.Empty;
    public string Node { get; set; } = string.Empty;
    // DA 兜底：可选 CLSID。给值时连接 URL 拼成 opcda://host/progId/{clsid}，跳过 OPCEnum
    public string? Clsid { get; set; }
    public byte Type { get; set; }
    public int Interval { get; set; }
    public int Deviation { get; set; }
    public string TcpAddress { get; set; } = string.Empty;

    // OPC UA 安全连接开关。true → SelectEndpoint 选最高安全策略（需双向证书信任）；
    // false → None 端点直连。默认 true（产线安全优先，CLAUDE.md 约束）。仅 UA 生效。
    public bool UseSecurity { get; set; } = true;

    [JsonIgnore]
    public List<Tag> Tags { get; set; } = new();
}
