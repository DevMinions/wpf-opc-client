namespace Dc.Domain.Entities;

public class Tag : EntityBase
{
    public string Item { get; set; } = string.Empty;
    public int DataType { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
}
