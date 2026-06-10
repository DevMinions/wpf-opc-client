using System.Globalization;
using System.Windows;
using Dc.App.Views.Converters;

namespace Dc.App.Tests.Views.Converters;

public class CountToVisibilityConverterTests
{
    private readonly CountToVisibilityConverter _c = new();

    [Fact]
    public void Zero_To_Visible()
        => Assert.Equal(Visibility.Visible, _c.Convert(0, typeof(Visibility), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Positive_To_Collapsed()
        => Assert.Equal(Visibility.Collapsed, _c.Convert(3, typeof(Visibility), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Invert_Zero_To_Collapsed()
        => Assert.Equal(Visibility.Collapsed, _c.Convert(0, typeof(Visibility), "Invert", CultureInfo.InvariantCulture));

    [Fact]
    public void Invert_Positive_To_Visible()
        => Assert.Equal(Visibility.Visible, _c.Convert(5, typeof(Visibility), "Invert", CultureInfo.InvariantCulture));

    [Fact]
    public void Null_Or_NonInt_Treated_As_Empty_Visible()
        => Assert.Equal(Visibility.Visible, _c.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
}
