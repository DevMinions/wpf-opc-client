using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace Dc.App.Services.Theme;

public sealed class WpfUiThemeApplier : IThemeApplier
{
    public void Apply(AppTheme effective)
    {
        var target = effective switch
        {
            AppTheme.Dark  => ApplicationTheme.Dark,
            AppTheme.Light => ApplicationTheme.Light,
            _ => throw new ArgumentException(
                "WpfUiThemeApplier.Apply 只接受 Light/Dark；System 需先解析。", nameof(effective))
        };
        ApplicationThemeManager.Apply(target);
    }

    public AppTheme DetectSystemTheme()
    {
        // Win11/10：注册表 AppsUseLightTheme == 0 即深色
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var value = key?.GetValue("AppsUseLightTheme");
        if (value is int i && i == 0) return AppTheme.Dark;
        return AppTheme.Light;
    }
}
