using System.Globalization;

namespace Dc.App.Services.I18n;

public sealed class CultureLanguageApplier : ILanguageApplier
{
    public void Apply(CultureInfo effective)
    {
        // 只动 UICulture(界面语言),不动 CurrentCulture(日期/数值格式与 OPC 数值解析保持稳定)
        CultureInfo.DefaultThreadCurrentUICulture = effective;
        Thread.CurrentThread.CurrentUICulture = effective;
        LocalizationManager.Instance.SetCulture(effective);
    }

    public CultureInfo DetectSystemCulture() => ResolveSupported(CultureInfo.InstalledUICulture.Name);

    // 纯函数,便于单测:zh* → zh-CN,其它 → en
    public static CultureInfo ResolveSupported(string cultureName) =>
        cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? new CultureInfo("zh-CN")
            : new CultureInfo("en");
}
