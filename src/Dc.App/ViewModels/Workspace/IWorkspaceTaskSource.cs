using Dc.Domain.Entities;
using Dc.Opc.Abstractions;

namespace Dc.App.ViewModels.Workspace;

public interface IWorkspaceTaskSource
{
    Task<IReadOnlyList<CollectorTask>> LoadTasksAsync();

    /// <summary>Load a single task with its tag descriptors for starting.</summary>
    Task<(CollectorTask? Task, IReadOnlyList<TagDescriptor> Tags)> GetTaskWithTagsAsync(string taskId);

    /// <summary>Persist a new task created via the editor dialog.</summary>
    Task SaveNewTaskAsync(CollectorTask task);

    /// <summary>Persist edits to an existing task (preserves CreatedAt).</summary>
    Task UpdateTaskAsync(CollectorTask task);

    /// <summary>Delete a task and cascade-delete its groups and tags in one transaction.</summary>
    Task DeleteTaskCascadeAsync(string taskId);

    /// <summary>选中任务的分组数 / Tag 数（tab 计数 badge 用）。默认 0，便于测试 fake 不必实现。</summary>
    Task<(int Groups, int Tags)> GetCountsAsync(string taskId) => Task.FromResult((0, 0));
}
