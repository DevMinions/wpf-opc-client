using Dc.Domain.Entities;

namespace Dc.App.ViewModels;

/// <summary>
/// Tag 列表行:包装 Tag 实体 + 人类可读的任务名。
/// 此前「任务」列直接绑 TaskId(26 位 ULID);现网格显名,Tag 实体经 .Tag 透传给编辑/删除/热同步。
/// </summary>
public sealed class TagRow
{
    public Tag Tag { get; }
    public string Id => Tag.Id;
    public string Item => Tag.Item;
    public int DataType => Tag.DataType;
    public string TaskName { get; }
    public DateTime CreatedAt => Tag.CreatedAt;

    public TagRow(Tag tag, string taskName)
    {
        Tag = tag;
        TaskName = taskName;
    }
}
