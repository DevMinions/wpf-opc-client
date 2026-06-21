using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dc.App.Views.Converters;

/// <summary>
/// 反相布尔→可见性:false→Visible,true→Collapsed。用于"IsVirtual==false 时显示真实面板"。
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
