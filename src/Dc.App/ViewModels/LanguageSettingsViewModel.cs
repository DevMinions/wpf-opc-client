using CommunityToolkit.Mvvm.ComponentModel;
using Dc.App.Services.I18n;

namespace Dc.App.ViewModels;

public sealed partial class LanguageSettingsViewModel : ObservableObject
{
    private readonly ILanguageService _language;
    private bool _syncing;

    [ObservableProperty] private AppLanguage _selectedLanguage;

    public LanguageSettingsViewModel(ILanguageService language)
    {
        _language = language;
        _selectedLanguage = language.Current;
        _language.LanguageChanged += OnServiceLanguageChanged;
    }

    partial void OnSelectedLanguageChanged(AppLanguage value)
    {
        if (_syncing) return;
        if (_language.Current == value) return;
        _language.Apply(value);
    }

    private void OnServiceLanguageChanged(AppLanguage lang)
    {
        _syncing = true;
        SelectedLanguage = lang;
        _syncing = false;
    }
}
