using System.Windows.Threading;
using Dc.App.ViewModels;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;

namespace Dc.App.Tests.ViewModels;

public class NavigateCtaTests
{
    private static TaskOrchestrator Orch()
        => new(Array.Empty<IOpcSubscriberFactory>(), new FakePublisherFactory(), new OrchestratorOptions(), null);

    private sealed class FakePublisherFactory : IPublisherFactory
    {
        public IPublisher Create(string address) => throw new NotSupportedException();
    }

    [Fact]
    public void LiveData_Standalone_ShowsCta_And_Navigates()
    {
        string? navigated = null;
        var vm = new LiveDataViewModel(Orch(), Dispatcher.CurrentDispatcher,
            navigate: key => navigated = key, showNavigateCta: true);

        Assert.True(vm.ShowNavigateCta);
        Assert.Equal("去采集任务", vm.NavigateCtaText);
        vm.NavigateToWorkspaceCommand.Execute(null);
        Assert.Equal("workspace", navigated);
    }

    [Fact]
    public void LiveData_Embedded_NoCta()
    {
        var vm = new LiveDataViewModel(Orch(), Dispatcher.CurrentDispatcher);
        Assert.False(vm.ShowNavigateCta);
        Assert.Null(vm.NavigateCtaText);
    }

    [Fact]
    public void Diagnostics_Standalone_ShowsCta_And_Navigates()
    {
        string? navigated = null;
        var vm = new DiagnosticsViewModel(Orch(),
            navigate: key => navigated = key, showNavigateCta: true);

        Assert.True(vm.ShowNavigateCta);
        vm.NavigateToWorkspaceCommand.Execute(null);
        Assert.Equal("workspace", navigated);
    }

    [Fact]
    public void Diagnostics_Embedded_NoCta()
    {
        var vm = new DiagnosticsViewModel(Orch());
        Assert.False(vm.ShowNavigateCta);
        Assert.Null(vm.NavigateCtaText);
    }
}
