using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Dc.App.Views.Workspace;

public sealed class SparklineConverter : IValueConverter
{
    private const double W = 200, H = 40;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pts = new PointCollection();
        if (value is not IEnumerable en) return pts;
        var rates = en.Cast<double>().ToList();
        if (rates.Count < 2) return pts;
        double max = Math.Max(1.0, rates.Max());
        for (int i = 0; i < rates.Count; i++)
        {
            double x = W * i / (rates.Count - 1);
            double y = H - (rates[i] / max * H);
            pts.Add(new Point(x, y));
        }
        return pts;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
