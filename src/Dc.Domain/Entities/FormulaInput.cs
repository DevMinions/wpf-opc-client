namespace Dc.Domain.Entities;

public class FormulaInput : EntityBase
{
    public string FormulaId { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;       // 表达式里的变量名，如 "T"
    public string SourceTagId { get; set; } = string.Empty; // 同任务真实 Tag Id
}
