using Dc.App.Dashboard;
using Dc.App.ViewModels.Dashboard;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.Tests.ViewModels.Dashboard;

public class DashboardViewModelTests
{
    private sealed class FakeOrchestratorView : IDashboardOrchestratorView
    {
        public IReadOnlyList<TaskDiagnostics> Diagnostics { get; set; } = Array.Empty<TaskDiagnostics>();
        public IReadOnlyCollection<string> RunningTaskIds { get; set; } = Array.Empty<string>();

        IReadOnlyList<TaskDiagnostics> IDashboardOrchestratorView.GetDiagnostics() => Diagnostics;
        IReadOnlyCollection<string> IDashboardOrchestratorView.RunningTaskIds => RunningTaskIds;
    }

    private static readonly DateTimeOffset Now = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Refresh_NoTasks_Snapshot100()
    {
        var orch = new FakeOrchestratorView();
        var vm = new DashboardViewModel(orch, () => Now, TimeSpan.FromSeconds(120));

        vm.Refresh();

        Assert.Equal(100, vm.HealthScore);
        Assert.Empty(vm.Alerts);
        Assert.Equal("0", vm.RunningTasksDisplay);
        Assert.Equal("—", vm.UptimeDisplay);
    }

    [Fact]
    public void Refresh_RunningTask_PopulatesRow()
    {
        var diag = new TaskDiagnostics("t1", Now.AddMinutes(-5), Now.AddSeconds(-1),
            Now.AddSeconds(-1), 100, 0, 0, 50);
        var orch = new FakeOrchestratorView
        {
            Diagnostics = new[] { diag },
            RunningTaskIds = new[] { "t1" }
        };
        var vm = new DashboardViewModel(orch, () => Now, TimeSpan.FromSeconds(120));

        vm.Refresh();

        Assert.Equal(100, vm.HealthScore);
        Assert.Empty(vm.Alerts);
        Assert.Equal("1", vm.RunningTasksDisplay);
        Assert.Equal(50, vm.ActiveTags);
        Assert.Single(vm.Tasks);
    }

    [Fact]
    public void Refresh_StoppedTask_PopulatesAlert()
    {
        var diag = new TaskDiagnostics("t1", Now.AddMinutes(-5), Now.AddSeconds(-1),
            Now.AddSeconds(-1), 100, 0, 0, 50);
        var orch = new FakeOrchestratorView
        {
            Diagnostics = new[] { diag },
            RunningTaskIds = Array.Empty<string>()
        };
        var vm = new DashboardViewModel(orch, () => Now, TimeSpan.FromSeconds(120));

        vm.Refresh();

        Assert.Equal(85, vm.HealthScore);
        Assert.Single(vm.Alerts);
        Assert.Equal(AlertSeverity.Critical, vm.Alerts[0].Severity);
    }

    [Fact]
    public void Refresh_TwiceWithGrowingValueCount_ProducesRate()
    {
        var fakeNow = Now;
        DateTimeOffset Clock() => fakeNow;

        var diag1 = new TaskDiagnostics("t1", Now.AddMinutes(-5), Now.AddSeconds(-1),
            Now.AddSeconds(-1), 100, 0, 0, 50);

        var orch = new FakeOrchestratorView
        {
            Diagnostics = new[] { diag1 },
            RunningTaskIds = new[] { "t1" }
        };
        var vm = new DashboardViewModel(orch, Clock, TimeSpan.FromSeconds(120));

        vm.Refresh();
        Assert.Equal(0.0, vm.MessagesPerSecond, precision: 1);

        fakeNow = Now.AddSeconds(1);
        orch.Diagnostics = new[]
        {
            new TaskDiagnostics("t1", diag1.StartedAt, fakeNow, fakeNow, 180, 0, 0, 50)
        };

        vm.Refresh();
        Assert.Equal(80.0, vm.MessagesPerSecond, precision: 1);
    }
}
