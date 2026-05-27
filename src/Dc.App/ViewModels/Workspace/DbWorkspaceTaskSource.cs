using Dc.Domain.Entities;
using Dc.Infrastructure.Persistence;
using Dc.Opc.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dc.App.ViewModels.Workspace;

public sealed class DbWorkspaceTaskSource : IWorkspaceTaskSource
{
    private readonly IDbContextFactory<DcDbContext> _dbFactory;

    public DbWorkspaceTaskSource(IDbContextFactory<DcDbContext> dbFactory) =>
        _dbFactory = dbFactory;

    public async Task<IReadOnlyList<CollectorTask>> LoadTasksAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Tasks.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();
    }

    public async Task<(CollectorTask? Task, IReadOnlyList<TagDescriptor> Tags)> GetTaskWithTagsAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.Tasks
            .AsNoTracking()
            .Include(t => t.Tags)
            .FirstOrDefaultAsync(t => t.Id == taskId);

        if (task is null)
            return (null, Array.Empty<TagDescriptor>());

        var descriptors = task.Tags
            .Select(t => new TagDescriptor(t.Id, t.Item, t.DataType))
            .ToList();

        return (task, descriptors);
    }

    public async Task<(int Groups, int Tags)> GetCountsAsync(string taskId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var groups = await db.Groups.AsNoTracking().CountAsync(g => g.TaskId == taskId);
        var tags = await db.Tags.AsNoTracking().CountAsync(t => t.TaskId == taskId);
        return (groups, tags);
    }

    public async Task SaveNewTaskAsync(CollectorTask task)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
    }
}
