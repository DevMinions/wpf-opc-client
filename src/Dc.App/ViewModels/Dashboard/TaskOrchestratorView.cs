using Dc.Infrastructure.Orchestration;

namespace Dc.App.ViewModels.Dashboard;

public sealed class TaskOrchestratorView : IDashboardOrchestratorView
{
    private readonly TaskOrchestrator _orch;
    public TaskOrchestratorView(TaskOrchestrator orch) => _orch = orch;

    public IReadOnlyList<TaskDiagnostics> GetDiagnostics() => _orch.GetDiagnostics();
    public IReadOnlyCollection<string> RunningTaskIds => _orch.RunningTaskIds;
}
