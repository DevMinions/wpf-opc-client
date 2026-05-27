namespace Dc.App.Services.Theme;

public interface IThemeService
{
    AppTheme Current { get; }
    event Action<AppTheme>? ThemeChanged;

    /// 启动时调用一次：读 IConfiguration["Theme"] → Apply 一次。
    void Initialize();

    /// 用户切换主题。System 会被解析为 effective Light/Dark 再下发。
    void Apply(AppTheme theme);
}
