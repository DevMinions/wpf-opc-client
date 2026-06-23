using Dc.App.Services.I18n;
using Dc.App.ViewModels;

namespace Dc.App.Tests.ViewModels;

public class SettingsViewModelLanguageTests
{
    private sealed class FakeLanguageService : ILanguageService
    {
        public AppLanguage Current { get; private set; } = AppLanguage.System;
        public event Action<AppLanguage>? LanguageChanged;
        public int ApplyCount;
        public void Initialize() { }
        public void Apply(AppLanguage lang) { ApplyCount++; Current = lang; LanguageChanged?.Invoke(lang); }
    }

    [Fact]
    public void Initial_SelectedLanguageMatchesService()
    {
        var svc = new FakeLanguageService();
        svc.Apply(AppLanguage.English);
        var vm = new LanguageSettingsViewModel(svc);
        Assert.Equal(AppLanguage.English, vm.SelectedLanguage);
    }

    [Fact]
    public void SettingSelectedLanguage_CallsApply()
    {
        var svc = new FakeLanguageService();
        var vm = new LanguageSettingsViewModel(svc);
        vm.SelectedLanguage = AppLanguage.English;
        Assert.Equal(AppLanguage.English, svc.Current);
        Assert.True(svc.ApplyCount >= 1);
    }

    [Fact]
    public void ChangedExternally_UpdatesSelectedLanguage()
    {
        var svc = new FakeLanguageService();
        var vm = new LanguageSettingsViewModel(svc);
        svc.Apply(AppLanguage.ChineseSimplified);
        Assert.Equal(AppLanguage.ChineseSimplified, vm.SelectedLanguage);
    }

    [Fact]
    public void SettingSameLanguage_DoesNotReapply()
    {
        var svc = new FakeLanguageService();
        var vm = new LanguageSettingsViewModel(svc);
        vm.SelectedLanguage = AppLanguage.English;
        var after = svc.ApplyCount;
        vm.SelectedLanguage = AppLanguage.English;
        Assert.Equal(after, svc.ApplyCount);
    }
}
