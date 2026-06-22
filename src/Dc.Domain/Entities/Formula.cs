using System.Text.Json.Serialization;

namespace Dc.Domain.Entities;

public class Formula : EntityBase
{
    public string Name { get; set; } = string.Empty;          // 任务内唯一；同时作为虚拟 Tag 的 Item
    public string Expression { get; set; } = string.Empty;     // DynamicExpresso 表达式
    public string OutputTagId { get; set; } = string.Empty;    // 产出的虚拟 Tag Id（一对一）
    public string? OutputUnit { get; set; }
    public string TaskId { get; set; } = string.Empty;

    [JsonIgnore]
    public List<FormulaInput> Inputs { get; set; } = new();
}
