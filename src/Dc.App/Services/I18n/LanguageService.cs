using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Dc.App.Services.I18n;

public sealed class LanguageService : ILanguageService
{
    private readonly IConfiguration _config;
    private readonly ILanguageApplier _applier;
    private readonly ILanguagePreferenceWriter? _writer;
    private AppLanguage _current = AppLanguage.System;

    public LanguageService(IConfiguration config, ILanguageApplier applier, ILanguagePreferenceWriter? writer = null)
    {
        _config = config;
        _applier = applier;
        _writer = writer;
    }

    public AppLanguage Current => _current;
    public event Action<AppLanguage>? LanguageChanged;

    public void Initialize()
    {
        var initial = ParseOrDefault(_config["Language"], AppLanguage.System);
        Apply(initial, raiseEvent: false);
    }

    public void Apply(AppLanguage language) => Apply(language, raiseEvent: true);

    private void Apply(AppLanguage language, bool raiseEvent)
    {
        var effective = language == AppLanguage.System ? _applier.DetectSystemCulture() : Map(language);
        _applier.Apply(effective);
        _current = language;
        if (raiseEvent)
        {
            _writer?.Write(language);
            LanguageChanged?.Invoke(language);
        }
    }

    private static CultureInfo Map(AppLanguage language) => language switch
    {
        AppLanguage.English => new CultureInfo("en"),
        _ => new CultureInfo("zh-CN")
    };

    private static AppLanguage ParseOrDefault(string? raw, AppLanguage fallback)
        => Enum.TryParse<AppLanguage>(raw, ignoreCase: true, out var v) ? v : fallback;
}
