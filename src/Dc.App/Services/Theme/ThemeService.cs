using Microsoft.Extensions.Configuration;

namespace Dc.App.Services.Theme;

public sealed class ThemeService : IThemeService
{
    private readonly IConfiguration _config;
    private readonly IThemeApplier _applier;
    private readonly IThemePreferenceWriter? _writer;
    private readonly ISystemThemeWatcher? _watcher;
    private AppTheme _current = AppTheme.System;

    public ThemeService(IConfiguration config, IThemeApplier applier, IThemePreferenceWriter? writer = null, ISystemThemeWatcher? watcher = null)
    {
        _config = config;
        _applier = applier;
        _writer = writer;
        _watcher = watcher;
        if (_watcher is not null) _watcher.SystemThemeChanged += OnSystemThemeChanged;
    }

    public AppTheme Current => _current;

    public event Action<AppTheme>? ThemeChanged;

    public void Initialize()
    {
        var configured = _config["Theme"];
        var initial = ParseOrDefault(configured, AppTheme.System);
        Apply(initial, raiseEvent: false);
        _watcher?.Start();
    }

    public void Apply(AppTheme theme) => Apply(theme, raiseEvent: true);

    private void Apply(AppTheme theme, bool raiseEvent)
    {
        var effective = theme == AppTheme.System
            ? _applier.DetectSystemTheme()
            : theme;
        _applier.Apply(effective);
        _current = theme;
        if (raiseEvent)
        {
            _writer?.Write(theme);
            ThemeChanged?.Invoke(theme);
        }
    }

    private void OnSystemThemeChanged()
    {
        if (_current != AppTheme.System) return;
        _applier.Apply(_applier.DetectSystemTheme());
    }

    private static AppTheme ParseOrDefault(string? raw, AppTheme fallback)
        => Enum.TryParse<AppTheme>(raw, ignoreCase: true, out var t) ? t : fallback;
}
