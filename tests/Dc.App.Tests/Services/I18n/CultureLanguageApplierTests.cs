using Dc.App.Services.I18n;

namespace Dc.App.Tests.Services.I18n;

public class CultureLanguageApplierTests
{
    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh-TW", "zh-CN")]   // 简化:本版本仅简中,繁中暂归 zh-CN(升级路径:加 zh-Hant)
    [InlineData("en-US", "en")]
    [InlineData("en", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("de-DE", "en")]
    public void ResolveSupported_MapsByPrefix(string input, string expected)
        => Assert.Equal(expected, CultureLanguageApplier.ResolveSupported(input).Name);
}
