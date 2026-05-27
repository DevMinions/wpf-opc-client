using System.Text.Json.Serialization;

namespace Dc.Domain.Entities;

public class Group : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;

    [JsonIgnore]
    public List<Tag> Tags { get; set; } = new();
}
