using Dc.Domain.Entities;

namespace Dc.App.ViewModels;

/// <summary>
/// 分组列表行:Group 实体 + 人类可读的任务名 + 显隐该列的标志。
/// 此前 GroupsView「所属任务」列直接绑 TaskId(26 位 ULID);内嵌模式(工作区选了任务再看
/// 分组 tab)该列还是冗余的。现 GroupRow 暴露 TaskName + ShowTaskColumn,实体经 .Group 透传。
/// </summary>
public sealed class GroupListRow
{
    public Group Group { get; }
    public string Id => Group.Id;
    public string Name => Group.Name;
    public string TaskName { get; }
    public DateTime CreatedAt => Group.CreatedAt;

    public GroupListRow(Group group, string taskName)
    {
        Group = group;
        TaskName = taskName;
    }
}
