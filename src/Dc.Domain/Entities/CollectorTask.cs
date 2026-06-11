using System.Text.Json.Serialization;

namespace Dc.Domain.Entities;

public class CollectorTask : EntityBase
{
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
    public List<Group> Groups { get; set; } = new();

    [JsonIgnore]
    public List<Tag> Tags { get; set; } = new();
}
