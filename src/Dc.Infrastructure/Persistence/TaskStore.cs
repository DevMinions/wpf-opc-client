using Dc.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dc.Infrastructure.Persistence;

// 任务写路径单一来源（WPF App 与测试共用，跨平台）。仿 DbTaskLauncher 静态助手先例。
public static class TaskStore
{
    // 更新可编辑字段。禁用 db.Tasks.Update(task)：编辑器实体 CreatedAt=default，
    // 全量 Modified 会覆盖 created_at。改 load-then-copy 只改可编辑列，CreatedAt 保留、
    // UpdatedAt 由 DcDbContext.ApplyAutoFields 自动刷。任务不存在则静默返回。
    public static async Task UpdateAsync(DcDbContext db, CollectorTask task)
    {
        var existing = await db.Tasks.FirstOrDefaultAsync(t => t.Id == task.Id);
        if (existing is null) return;
        existing.Name = task.Name;          // 可读名称(可空):编辑时漏拷会导致填了名又存不进去
        existing.Server = task.Server;
        existing.Node = task.Node;
        existing.Clsid = task.Clsid;
        existing.Type = task.Type;
        existing.Interval = task.Interval;
        existing.Deviation = task.Deviation;
        existing.TcpAddress = task.TcpAddress;
        existing.UseSecurity = task.UseSecurity;
        await db.SaveChangesAsync();
    }

    // 安全删除：一个事务里按 task_id 删 tags → task,不留孤儿。
    // EF 关系是 NoAction + ForeignKeys=false，故必须显式删子项。
    public static async Task DeleteCascadeAsync(DcDbContext db, string taskId)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Tags.Where(t => t.TaskId == taskId).ExecuteDeleteAsync();
        await db.Tasks.Where(t => t.Id == taskId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }
}
