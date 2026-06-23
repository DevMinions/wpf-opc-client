using System;
using System.Globalization;
using System.Windows.Data;
using Dc.App.Services.I18n;

namespace Dc.App.Views.Converters;

/// 状态值 → 本地化串。values[0]=状态值(enum/string),ConverterParameter=key 前缀 → 查 "前缀_状态"。
/// values[1]=LocalizationManager 索引器值(内容无意义,仅用于 SetCulture 发 Item[] 时触发 MultiBinding 重算 → 语言切换实时刷)。
public sealed class LocalizedStateConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = values is { Length: > 0 } ? values[0]?.ToString() : null;
        if (string.IsNullOrEmpty(state)) return string.Empty;
        var prefix = parameter?.ToString() ?? string.Empty;
        return LocalizationManager.Instance[$"{prefix}{state}"];
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
