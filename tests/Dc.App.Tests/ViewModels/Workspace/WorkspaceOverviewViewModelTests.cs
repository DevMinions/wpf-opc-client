using Dc.App.ViewModels.Dashboard;
using Dc.App.ViewModels.Workspace;
using Dc.Infrastructure.Orchestration;

namespace Dc.App.Tests.ViewModels.Workspace;

public class WorkspaceOverviewViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeOrchView : IDashboardOrchestratorView
    {
        public IReadOnlyList<TaskDiagnostics> Diags { get; set; } = Array.Empty<TaskDiagnostics>();
        public IReadOnlyCollection<string> Running { get; set; } = Array.Empty<string>();
        public IReadOnlyList<TaskDiagnostics> GetDiagnostics() => Diags;
        public IReadOnlyCollection<string> RunningTaskIds => Running;
    }

    private static TaskDiagnostics Diag(string id, long vc, DateTimeOffset start, DateTimeOffset hb)
        => new(id, start, hb, hb, vc, 0, 0, 42);

    [Fact]
    public void Sample_NoTask_KpisZero()
    {
        var orch = new FakeOrchView();
        var vm = new WorkspaceOverviewViewModel(orch, () => Now);
        vm.SetTask("missing");
        vm.Sample();
        Assert.Equal("—", vm.UptimeDisplay);
        Assert.Equal(0, vm.TotalMessages);
        Assert.Empty(vm.SparklineRates);
    }

    [Fact]
    public void Sample_PopulatesKpisFromDiagnostics()
    {
        var orch = new FakeOrchView
        {
            Diags = new[] { Diag("t1", 1000, Now.AddMinutes(-10), Now.AddSeconds(-1)) },
            Running = new[] { "t1" }
        };
        var vm = new WorkspaceOverviewViewModel(orch, () => Now);
        vm.SetTask("t1");
        vm.Sample();
        Assert.Equal(1000, vm.TotalMessages);
        Assert.Equal(42, vm.SubscribedTags);
        Assert.True(vm.IsRunning);
    }

    [Fact]
    public void Sample_TwiceGrowingValueCount_AppendsRatePoint()
    {
        var t = Now;
        DateTimeOffset Clock() => t;
        var orch = new FakeOrchView
        {
            Diags = new[] { Diag("t1", 100, Now.AddMinutes(-10), Now) },
            Running = new[] { "t1" }
        };
        var vm = new WorkspaceOverviewViewModel(orch, Clock);
        vm.SetTask("t1");
        vm.Sample();
        Assert.Empty(vm.SparklineRates);
        t = Now.AddSeconds(1);
        orch.Diags = new[] { Diag("t1", 180, Now.AddMinutes(-10), t) };
        vm.Sample();
        Assert.Single(vm.SparklineRates);
        Assert.Equal(80.0, vm.SparklineRates[0], precision: 1);
    }

    [Fact]
    public void SparklineRates_CappedAt60()
    {
        var t = Now;
        DateTimeOffset Clock() => t;
        var orch = new FakeOrchView { Running = new[] { "t1" } };
        var vm = new WorkspaceOverviewViewModel(orch, Clock);
        vm.SetTask("t1");
        long vc = 0;
        for (int i = 0; i < 80; i++)
        {
            orch.Diags = new[] { Diag("t1", vc, Now.AddMinutes(-10), t) };
            vm.Sample();
            vc += 10;
            t = t.AddSeconds(1);
        }
        Assert.Equal(60, vm.SparklineRates.Count);
    }

    [Fact]
    public void SetTask_Switching_ResetsBuffer()
    {
        var t = Now;
        DateTimeOffset Clock() => t;
        var orch = new FakeOrchView { Running = new[] { "t1", "t2" } };
        var vm = new WorkspaceOverviewViewModel(orch, Clock);
        vm.SetTask("t1");
        orch.Diags = new[] { Diag("t1", 100, Now, t) };
        vm.Sample();
        t = Now.AddSeconds(1);
        orch.Diags = new[] { Diag("t1", 200, Now, t) };
        vm.Sample();
        Assert.Single(vm.SparklineRates);
        vm.SetTask("t2");
        Assert.Empty(vm.SparklineRates);
    }
}
