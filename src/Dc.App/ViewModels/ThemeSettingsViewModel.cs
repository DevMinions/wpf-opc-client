using CommunityToolkit.Mvvm.ComponentModel;
using Dc.App.Services.Theme;

namespace Dc.App.ViewModels;

public sealed partial class ThemeSettingsViewModel : ObservableObject
{
    private readonly IThemeService _theme;
    private bool _syncing;

    [ObservableProperty] private AppTheme _selectedTheme;

    public ThemeSettingsViewModel(IThemeService theme)
    {
        _theme = theme;
        _selectedTheme = theme.Current;
        _theme.ThemeChanged += OnServiceThemeChanged;
    }

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        if (_syncing) return;
        if (_theme.Current == value) return;
        _theme.Apply(value);
    }

    private void OnServiceThemeChanged(AppTheme theme)
    {
        _syncing = true;
        SelectedTheme = theme;
        _syncing = false;
    }
}
