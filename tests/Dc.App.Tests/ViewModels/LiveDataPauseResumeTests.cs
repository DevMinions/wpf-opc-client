using System.Globalization;
using System.Windows.Threading;
using Dc.App.Services.I18n;
using Dc.App.ViewModels;
using Dc.Infrastructure.Messaging;
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;

namespace Dc.App.Tests.ViewModels;

[Collection("I18nCulture")]
public class LiveDataPauseResumeTests
{
    private sealed class FakePublisherFactory : IPublisherFactory
    {
        public IPublisher Create(string address) => throw new NotSupportedException();
    }

    private static LiveDataViewModel NewVm()
    {
        var orch = new TaskOrchestrator(
            Array.Empty<IOpcSubscriberFactory>(), new FakePublisherFactory(), new OrchestratorOptions(), null);
        return new LiveDataViewModel(orch, Dispatcher.CurrentDispatcher);
    }

    [Fact]
    public void PauseResumeText_TogglesWithPaused_AndFollowsCulture()
    {
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        var vm = NewVm();

        Assert.Equal("⏸ Pause", vm.PauseResumeText);   // 未暂停 → 显示「暂停」
        vm.Paused = true;
        Assert.Equal("▶ Resume", vm.PauseResumeText);   // 暂停后 → 显示「继续」

        LocalizationManager.Instance.SetCulture(new CultureInfo("zh-CN"));
        // 注:语言切换的实时刷由 VM 订阅 LanguageChanged 触发 OnPropertyChanged;
        // 这里直接断言索引器取值已随 culture 改变(属性 getter 实时读 _loc)。
        Assert.Equal("▶ 继续", vm.PauseResumeText);
    }

    [Fact]
    public void PauseResumeText_RaisesPropertyChanged_OnPausedToggle()
    {
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        var vm = NewVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Paused = true;
        Assert.Contains(nameof(LiveDataViewModel.PauseResumeText), raised);
    }
}
