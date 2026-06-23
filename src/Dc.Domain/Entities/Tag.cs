namespace Dc.Domain.Entities;

public class Tag : EntityBase
{
    public string Item { get; set; } = string.Empty;
    public int DataType { get; set; }
    public string TaskId { get; set; } = string.Empty;

    // 真实 Tag 的工程量映射；null 表示不缩放。虚拟 Tag 忽略。
    public double? ScaleFactor { get; set; }
    public double? Offset { get; set; }

    // true = 虚拟测点（公式产出），不进订阅器；Item = 公式名（任务内唯一）。
    public bool IsVirtual { get; set; }
}
