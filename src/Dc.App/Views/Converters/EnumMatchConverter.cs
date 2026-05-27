using System.Globalization;
using System.Windows.Data;

namespace Dc.App.Views.Converters;

/// RadioButton.IsChecked ↔ enum：ConverterParameter 传枚举名，匹配则 true；选中时 ConvertBack 返回该枚举值。
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null)
        {
            if (targetType.IsEnum) return Enum.Parse(targetType, parameter.ToString()!);
            if (targetType == typeof(string)) return parameter.ToString(); // 支持 string 状态（如 tab key）
        }
        return Binding.DoNothing;
    }
}
