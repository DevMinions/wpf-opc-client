namespace Dc.App.Services.I18n;

public sealed class ResourceLocalizer : ILocalizer
{
    public string this[string key] => LocalizationManager.Instance[key];

    public string Format(string key, params object[] args) =>
        string.Format(LocalizationManager.Instance.Culture, this[key], args);
}
