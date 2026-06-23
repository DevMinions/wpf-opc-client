using System.Globalization;
using System.Linq;
using Dc.App.Services.I18n;
using Dc.App.ViewModels;

namespace Dc.App.Tests.ViewModels;

// 读/写进程级 LocalizationManager.Instance(SetCulture + 按 culture 取串),纳入串行集合避免 culture 互染。
[Collection("I18nCulture")]
public class OpcDataTypeOptionTests
{
    [Fact]
    public void All_Code0_LocalizedByCurrentCulture()
    {
        LocalizationManager.Instance.SetCulture(new CultureInfo("zh-CN"));
        Assert.Equal("默认", OpcDataTypeOption.FromCode(0).DisplayName);

        // All 是按当前 culture 重建的属性:切到 en 后下次访问即反映英文。
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        Assert.Equal("Default", OpcDataTypeOption.FromCode(0).DisplayName);
    }

    [Fact]
    public void FromCode_Unknown_LocalizedWithCodeSubstituted()
    {
        LocalizationManager.Instance.SetCulture(new CultureInfo("zh-CN"));
        Assert.Equal("未知(999)", OpcDataTypeOption.FromCode(999).DisplayName);

        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        Assert.Equal("Unknown(999)", OpcDataTypeOption.FromCode(999).DisplayName);
    }

    [Fact]
    public void All_NonDefaultTypeNames_StayInvariant()
    {
        // 非 code-0 的类型名是通用英文,不随语言变。抽查 Boolean/Float64。
        LocalizationManager.Instance.SetCulture(new CultureInfo("zh-CN"));
        Assert.Equal("Boolean", OpcDataTypeOption.FromCode(11).DisplayName);
        Assert.Equal("Float64", OpcDataTypeOption.FromCode(5).DisplayName);
    }
}
