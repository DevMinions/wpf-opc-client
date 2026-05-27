namespace Dc.App.Services.Theme;

public interface IThemePreferenceWriter
{
    /// 把主题选择写入持久化（appsettings.json 的 Theme 键）。失败不应抛。
    void Write(AppTheme theme);
}
