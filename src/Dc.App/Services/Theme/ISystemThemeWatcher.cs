namespace Dc.App.Services.Theme;

/// 监听 OS 主题（亮/暗）变化。隔离 Windows-only SystemEvents 以便单测。
public interface ISystemThemeWatcher
{
    event Action? SystemThemeChanged;
    void Start();
}
