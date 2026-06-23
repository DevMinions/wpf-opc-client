using System.Globalization;
using System.Windows.Threading;
using Dc.App.Services.I18n;
using Dc.App.ViewModels;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;

namespace Dc.App.Tests.ViewModels;

[Collection("I18nCulture")]
public class NavigateCtaTests
{
    // CTA 文案本地化后按 culture 取值;断言中文字面量须锁定中文(VM 未注入 localizer → 回退 ResourceLocalizer 读 Instance.Culture)。
    public NavigateCtaTests() => LocalizationManager.Instance.SetCulture(new CultureInfo("zh-CN"));

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
