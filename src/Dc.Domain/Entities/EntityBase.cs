namespace Dc.Domain.Entities;

public abstract class EntityBase
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
