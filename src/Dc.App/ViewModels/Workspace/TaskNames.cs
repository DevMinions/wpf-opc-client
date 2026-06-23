using Dc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dc.App.ViewModels.Workspace;

/// <summary>
/// 任务 Id → 可读名(CollectorTask.DisplayName: Name→Server→Id)映射加载器。
/// 实时数据/诊断的「任务」列只拿得到 TaskId(ULID),用它解析成可读名,避免列里露 ULID。
/// </summary>
internal static class TaskNames
{
    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(IDbContextFactory<DcDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var tasks = await db.Tasks.AsNoTracking().ToListAsync();
        return tasks.ToDictionary(t => t.Id, t => t.DisplayName, StringComparer.Ordinal);
    }
}
