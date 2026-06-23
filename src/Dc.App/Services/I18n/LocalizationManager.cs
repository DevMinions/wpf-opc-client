using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using Dc.App.Resources;

namespace Dc.App.Services.I18n;

public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    public CultureInfo Culture => _culture;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Strings.ResourceManager.GetString(key, _culture) ?? key;

    public void SetCulture(CultureInfo culture)
    {
        _culture = culture;
        // Binding.IndexerName == "Item[]" → 所有索引器绑定重取
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    }
}
