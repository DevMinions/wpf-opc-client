using System;
using System.Globalization;
using System.Windows.Data;
using Dc.App.Services.I18n;

namespace Dc.App.Views.Converters;

/// 本地化格式串。ConverterParameter=key(模板含 {0});values[0]=填入的参数。
/// values[1]=LocalizationManager 索引器值(内容无意义,仅触发 SetCulture 时 MultiBinding 重算 → 实时刷)。
/// 用于 AutomationProperties.Name 等 StringFormat 不能放 Binding 的场景。
public sealed class LocalizedFormatConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var arg = values is { Length: > 0 } ? values[0]?.ToString() ?? string.Empty : string.Empty;
        var key = parameter?.ToString() ?? string.Empty;
        var template = LocalizationManager.Instance[key];
        return string.Format(LocalizationManager.Instance.Culture, template, arg);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
