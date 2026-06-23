using Dc.Domain.Entities;
using Dc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dc.App.ViewModels.Workspace;

/// <summary>
/// 分组层已对用户隐藏(Tag 直接挂任务):每个任务隐式持有一个「默认分组」,承载其全部 Tag。
/// Tag 新建(TagsViewModel)与浏览批量加 Tag(BrowseViewModel)共用此兜底,避免重复实现。
/// </summary>
internal static class DefaultTaskGroup
{
    public const string Name = "默认分组";

    /// <summary>取任务最早的分组作默认;没有则建一个(SaveChanges 自动补时间戳)。</summary>
    public static async Task<Group> EnsureAsync(IDbContextFactory<DcDbContext> dbFactory, string taskId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.Groups.AsNoTracking()
            .Where(g => g.TaskId == taskId)
            .OrderBy(g => g.CreatedAt)
            .FirstOrDefaultAsync();
        if (existing is not null) return existing;

        var grp = new Group { Id = UlidGenerator.NewId(), Name = Name, TaskId = taskId };
        db.Groups.Add(grp);
        await db.SaveChangesAsync();
        return grp;
    }
}
