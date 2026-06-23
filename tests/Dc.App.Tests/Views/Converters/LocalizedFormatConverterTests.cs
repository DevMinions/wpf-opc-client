using System.Globalization;
using Dc.App.Services.I18n;
using Dc.App.Views.Converters;

namespace Dc.App.Tests.Views.Converters;

[Collection("I18nCulture")]
public class LocalizedFormatConverterTests
{
    private readonly LocalizedFormatConverter _c = new();

    private object Conv(object? arg, string key)
        => _c.Convert(new[] { arg, (object?)"culture" }, typeof(string), key, CultureInfo.InvariantCulture);

    [Fact]
    public void FormatsKeyTemplateWithArg_FollowsCulture()
    {
        // Browse_CheckA11y: zh=勾选 {0} / en=Check {0}
        LocalizationManager.Instance.SetCulture(new CultureInfo("zh-CN"));
        Assert.Equal("勾选 Temp1", Conv("Temp1", "Browse_CheckA11y"));
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        Assert.Equal("Check Temp1", Conv("Temp1", "Browse_CheckA11y"));
    }

    [Fact]
    public void NullArg_FormatsWithEmpty()
    {
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        Assert.Equal("Check ", Conv(null, "Browse_CheckA11y"));
    }
}
