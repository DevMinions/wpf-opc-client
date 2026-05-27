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

    [JsonIgnore]
    public List<Group> Groups { get; set; } = new();

    [JsonIgnore]
    public List<Tag> Tags { get; set; } = new();
}
