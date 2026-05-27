using Microsoft.Win32;

namespace Dc.App.Services.Theme;

public sealed class SystemEventsThemeWatcher : ISystemThemeWatcher, IDisposable
{
    private bool _started;

    public event Action? SystemThemeChanged;

    public void Start()
    {
        if (_started) return; // 幂等：避免重复订阅静态 SystemEvents 事件
        _started = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            SystemThemeChanged?.Invoke();
    }

    // SystemEvents 是进程级静态事件，会强引用本实例 → 必须解绑，否则泄漏。
    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
