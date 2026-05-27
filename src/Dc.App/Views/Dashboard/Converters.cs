using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Dc.App.Dashboard;

namespace Dc.App.Views.Dashboard;

/// <summary>Inverts a bool — used by segmented pause/resume buttons.</summary>
public sealed class BoolInverseConverter : IValueConverter
{
    public static readonly BoolInverseConverter Default = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

public sealed class HealthScoreToColorConverter : IValueConverter
{
    private static Brush Res(string key)
        => System.Windows.Application.Current.TryFindResource(key) as Brush
           ?? Brushes.Gray;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int score) return Res("SystemFillColorSuccessBrush");
        return score >= 90 ? Res("SystemFillColorSuccessBrush")
             : score >= 70 ? Res("SystemFillColorCautionBrush")
             : Res("SystemFillColorCriticalBrush");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class AlertSeverityToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Application.Current.TryFindResource("SubtleFillColorSecondaryBrush") as Brush
           ?? Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class AlertSeverityToAccentConverter : IValueConverter
{
    private static Brush Res(string key)
        => System.Windows.Application.Current.TryFindResource(key) as Brush
           ?? Brushes.Gray;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AlertSeverity sev && sev == AlertSeverity.Critical
            ? Res("SystemFillColorCriticalBrush")
            : Res("SystemFillColorCautionBrush");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class AlertSeverityToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AlertSeverity sev && sev == AlertSeverity.Critical ? "🛑" : "⚠";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TaskRowSeverityToBrushConverter : IValueConverter
{
    private static Brush Res(string key)
        => System.Windows.Application.Current.TryFindResource(key) as Brush
           ?? Brushes.Gray;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            AlertSeverity.Critical => Res("SystemFillColorCriticalBrush"),
            AlertSeverity.Warning  => Res("SystemFillColorCautionBrush"),
            _ => Res("TextFillColorTertiaryBrush")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>状态点配色：正常 → 绿（区别于速率文字的灰），警告 → 黄，严重 → 红。对齐原型 dot.good 绿点。</summary>
public sealed class TaskRowDotBrushConverter : IValueConverter
{
    private static Brush Res(string key)
        => System.Windows.Application.Current.TryFindResource(key) as Brush
           ?? Brushes.Gray;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            AlertSeverity.Critical => Res("SystemFillColorCriticalBrush"),
            AlertSeverity.Warning  => Res("SystemFillColorCautionBrush"),
            _ => Res("SystemFillColorSuccessBrush")
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public static readonly ZeroToVisibleConverter Default = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value switch
        {
            int i => i,
            long l => (int)l,
            _ => -1
        };
        return n == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
