using Dc.Domain.Entities;
using Dc.Opc.Abstractions;

namespace Dc.App.ViewModels.Workspace;

public interface IWorkspaceTaskSource
{
    Task<IReadOnlyList<CollectorTask>> LoadTasksAsync();

    /// <summary>
    /// 加载单个任务用于启动：真实 Tag 描述符（虚拟 Tag 已过滤，不进订阅器）+ 该任务的公式定义
    /// （供 DbTaskLauncher 组装 TransformConfig，缩放/公式在 WPF 启动路径同样生效）。
    /// </summary>
    Task<(CollectorTask? Task, IReadOnlyList<TagDescriptor> Tags, IReadOnlyList<Formula> Formulas)> GetTaskWithTagsAsync(string taskId);

    /// <summary>Persist a new task created via the editor dialog.</summary>
    Task SaveNewTaskAsync(CollectorTask task);

    /// <summary>Persist edits to an existing task (preserves CreatedAt).</summary>
    Task UpdateTaskAsync(CollectorTask task);

    /// <summary>Delete a task and cascade-delete its tags in one transaction.</summary>
    Task DeleteTaskCascadeAsync(string taskId);

    /// <summary>选中任务的 Tag 数（tab 计数 badge 用）。默认 0，便于测试 fake 不必实现。</summary>
    Task<int> GetCountsAsync(string taskId) => Task.FromResult(0);

    /// <summary>
    /// 批量取各任务的「已配置 Tag 数」(DB 口径,与运行状态无关)。用于任务列表 badge——
    /// 此前用诊断的 SubscribedTagCount,但诊断只对运行中任务存在,导致已停止但已配置 Tag 的任务
    /// 误显「未配置 Tag」。默认空字典,便于测试 fake 不必实现。
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetConfiguredTagCountsAsync(IReadOnlyCollection<string> taskIds)
        => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>(StringComparer.Ordinal));
}
