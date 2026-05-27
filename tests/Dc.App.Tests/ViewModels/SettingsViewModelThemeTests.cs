using Dc.App.Services.Theme;
using Dc.App.ViewModels;

namespace Dc.App.Tests.ViewModels;

public class SettingsViewModelThemeTests
{
    private sealed class FakeThemeService : IThemeService
    {
        public AppTheme Current { get; private set; } = AppTheme.System;
        public event Action<AppTheme>? ThemeChanged;
        public int ApplyCount;
        public void Initialize() { }
        public void Apply(AppTheme theme) { ApplyCount++; Current = theme; ThemeChanged?.Invoke(theme); }
    }

    [Fact]
    public void Initial_SelectedThemeMatchesService()
    {
        var svc = new FakeThemeService();
        svc.Apply(AppTheme.Dark);
        var vm = new ThemeSettingsViewModel(svc);
        Assert.Equal(AppTheme.Dark, vm.SelectedTheme);
    }

    [Fact]
    public void SettingSelectedTheme_CallsApply()
    {
        var svc = new FakeThemeService();
        var vm = new ThemeSettingsViewModel(svc);
        vm.SelectedTheme = AppTheme.Light;
        Assert.Equal(AppTheme.Light, svc.Current);
        Assert.True(svc.ApplyCount >= 1);
    }

    [Fact]
    public void ThemeChangedExternally_UpdatesSelectedTheme()
    {
        var svc = new FakeThemeService();
        var vm = new ThemeSettingsViewModel(svc);
        svc.Apply(AppTheme.Dark);
        Assert.Equal(AppTheme.Dark, vm.SelectedTheme);
    }

    [Fact]
    public void SettingSameTheme_DoesNotReapplyInfinitely()
    {
        var svc = new FakeThemeService();
        var vm = new ThemeSettingsViewModel(svc);
        vm.SelectedTheme = AppTheme.Light;
        var countAfterFirst = svc.ApplyCount;
        vm.SelectedTheme = AppTheme.Light;
        Assert.Equal(countAfterFirst, svc.ApplyCount);
    }
}
