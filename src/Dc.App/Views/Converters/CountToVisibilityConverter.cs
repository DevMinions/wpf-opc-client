using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dc.App.Views.Converters;

/// <summary>
/// 集合 Count → Visibility：0（空）→ Visible，非 0 → Collapsed。
/// ConverterParameter="Invert" 反向。null/非 int 按「空」兜底，不抛。
/// 绑 ObservableCollection.Count，集合增删会触发刷新。
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is not int n || n == 0;
        var invert = parameter as string == "Invert";
        var visibleWhenEmpty = !invert;
        return isEmpty == visibleWhenEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
