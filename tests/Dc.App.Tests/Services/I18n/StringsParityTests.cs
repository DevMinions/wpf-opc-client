using System.Collections;
using System.Globalization;
using Dc.App.Resources;

namespace Dc.App.Tests.Services.I18n;

public class StringsParityTests
{
    private static HashSet<string> Keys(CultureInfo culture, bool tryParents)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: tryParents)
                  ?? throw new Xunit.Sdk.XunitException($"找不到 {culture.Name} 资源集");
        return set.Cast<DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
    }

    [Fact]
    public void Zh_And_En_HaveIdenticalKeySets()
    {
        var zh = Keys(new CultureInfo("zh-CN"), tryParents: true);   // 中性=主程序集
        var en = Keys(new CultureInfo("en"), tryParents: false);     // 仅 en 卫星,不回退父级

        var missingInEn = zh.Except(en).OrderBy(x => x).ToList();
        var extraInEn = en.Except(zh).OrderBy(x => x).ToList();

        Assert.True(missingInEn.Count == 0, "en 缺译: " + string.Join(", ", missingInEn));
        Assert.True(extraInEn.Count == 0, "en 多余: " + string.Join(", ", extraInEn));
    }
}
