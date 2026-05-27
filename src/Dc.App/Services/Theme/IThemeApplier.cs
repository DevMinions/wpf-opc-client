namespace Dc.App.Services.Theme;

public interface IThemeApplier
{
    /// 把 effective theme（Light/Dark，不含 System）下发到 UI 库。
    /// 调用方负责把 System 解析成 Light/Dark 再传进来。
    void Apply(AppTheme effective);

    /// 返回当前系统主题（Light 或 Dark）。用于 AppTheme.System 解析。
    AppTheme DetectSystemTheme();
}
