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

    /// <summary>选中任务的分组数 / Tag 数（tab 计数 badge 用）。默认 0，便于测试 fake 不必实现。</summary>
    Task<(int Groups, int Tags)> GetCountsAsync(string taskId) => Task.FromResult((0, 0));

    /// <summary>
    /// 批量取各任务的「已配置 Tag 数」(DB 口径,与运行状态无关)。用于任务列表 badge——
    /// 此前用诊断的 SubscribedTagCount,但诊断只对运行中任务存在,导致已停止但已配置 Tag 的任务
    /// 误显「未配置 Tag」。默认空字典,便于测试 fake 不必实现。
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetConfiguredTagCountsAsync(IReadOnlyCollection<string> taskIds)
        => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>(StringComparer.Ordinal));
}
