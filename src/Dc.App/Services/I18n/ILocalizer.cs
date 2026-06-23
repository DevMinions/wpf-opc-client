namespace Dc.App.Services.I18n;

public interface ILocalizer
{
    string this[string key] { get; }
    string Format(string key, params object[] args);
}
