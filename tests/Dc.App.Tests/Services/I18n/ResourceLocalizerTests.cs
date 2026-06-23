using System.Globalization;
using Dc.App.Services.I18n;

namespace Dc.App.Tests.Services.I18n;

[Collection("I18nCulture")]
public class ResourceLocalizerTests
{
    [Fact]
    public void Indexer_FollowsManagerCulture()
    {
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        var loc = new ResourceLocalizer();
        Assert.Equal("Save", loc["Common_Save"]);
        LocalizationManager.Instance.SetCulture(new CultureInfo("zh-CN"));
        Assert.Equal("保存", loc["Common_Save"]);
    }

    [Fact]
    public void Format_SubstitutesArgs()
    {
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        var loc = new ResourceLocalizer();
        // Common_Save 无占位符,Format 后不变;占位符 key 在抽取批次加入后由集成行为覆盖。
        Assert.Equal("Save", loc.Format("Common_Save"));
    }
}
