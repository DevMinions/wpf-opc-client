using Dc.Infrastructure.Orchestration;

namespace Dc.App.ViewModels.Dashboard;

/// 用于解耦 DashboardViewModel 与 TaskOrchestrator —— 方便单测。
public interface IDashboardOrchestratorView
{
    IReadOnlyList<TaskDiagnostics> GetDiagnostics();
    IReadOnlyCollection<string> RunningTaskIds { get; }
}
