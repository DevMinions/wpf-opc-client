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

    [JsonIgnore]
    public List<Group> Groups { get; set; } = new();

    [JsonIgnore]
    public List<Tag> Tags { get; set; } = new();
}
