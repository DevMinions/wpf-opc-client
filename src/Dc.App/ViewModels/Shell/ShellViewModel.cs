using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Navigation;
using Dc.App.Services.I18n;
using Dc.App.Services.Theme;
using Dc.Infrastructure.Orchestration;
using Microsoft.Extensions.Configuration;

namespace Dc.App.ViewModels.Shell;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _nav;
    private readonly IThemeService _theme;
    private readonly ILocalizer _loc;
    private readonly TaskOrchestrator? _orchestrator;
    private readonly DispatcherTimer? _healthTimer;

    [ObservableProperty]
    private string _selectedRouteKey = string.Empty;

    [ObservableProperty]
    private object? _currentContent;

    [ObservableProperty]
    private string _currentTitle = string.Empty;

    // 状态栏：DB 路径 + 健康指示（对齐原型 statusbar 4 段）
    [ObservableProperty] private string _dbPath = "sqlite.db";
    [ObservableProperty] private string _healthText = string.Empty;
    [ObservableProperty] private bool _healthOk = true;

    public string CurrentThemeLabel => _theme.Current switch
    {
        AppTheme.Light => _loc["Theme_Light"],
        AppTheme.Dark => _loc["Theme_Dark"],
        _ => _loc["Theme_System"]
    };

    public string TitleBarText => _loc.Format("Shell_TitleBarFormat", CurrentTitle);

    public IReadOnlyList<NavigationRoute> Routes => _nav.Routes;
    public NavigationRoute? FooterAbout => _nav.FooterAbout;

    public IRelayCommand<string?> NavigateCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }

    public ShellViewModel(INavigationService nav, IThemeService theme,
        ILocalizer loc, ILanguageService language,
        TaskOrchestrator? orchestrator = null, IConfiguration? config = null)
    {
        _nav = nav;
        _theme = theme;
        _loc = loc;
        _orchestrator = orchestrator;
        DbPath = config?["Database:Path"] ?? "sqlite.db";
        NavigateCommand = new RelayCommand<string?>(Navigate);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        if (_nav.Routes.Count > 0)
        {
            Navigate(_nav.Routes[0].Key);
        }

        HealthText = _loc["Health_AllNormal"];

        // 主题/语言切换 → 刷新派生文案（主题标签、标题栏、健康文案、标题随路由 key 重解析）。
        _theme.ThemeChanged += _ => OnPropertyChanged(nameof(CurrentThemeLabel));
        language.LanguageChanged += _ =>
        {
            OnPropertyChanged(nameof(CurrentThemeLabel));
            CurrentTitle = _loc[_nav.Routes.FirstOrDefault(r => r.Key == SelectedRouteKey)?.Title ?? string.Empty];
            OnPropertyChanged(nameof(TitleBarText));
            RefreshHealth();
        };

        // 每 5s 轮询整体健康（任意任务发送错误 → 告警，否则全部正常）。无 orchestrator（测试）则跳过。
        if (_orchestrator is not null)
        {
            _healthTimer = new DispatcherTimer(DispatcherPriority.Background,
                Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _healthTimer.Tick += (_, _) => RefreshHealth();
            _healthTimer.Start();
            RefreshHealth();
        }
    }

    private void RefreshHealth()
    {
        if (_orchestrator is null) return;
        var diags = _orchestrator.GetDiagnostics();
        var errorTasks = diags.Count(d => d.PublishErrorCount > 0);
        if (errorTasks > 0)
        {
            HealthOk = false;
            HealthText = _loc.Format("Health_Warnings", errorTasks);
        }
        else
        {
            HealthOk = true;
            HealthText = _loc["Health_AllNormal"];
        }
    }

    private void Navigate(string? routeKey)
    {
        if (string.IsNullOrEmpty(routeKey)) return;
        if (routeKey == SelectedRouteKey) return;

        try
        {
            var vm = _nav.Resolve(routeKey);
            CurrentContent = vm;
            SelectedRouteKey = routeKey;
            CurrentTitle = _loc[_nav.Routes.FirstOrDefault(r => r.Key == routeKey)?.Title ?? string.Empty];
            OnPropertyChanged(nameof(TitleBarText));
        }
        catch (KeyNotFoundException)
        {
            // 未注册路由 - 静默保留当前 state（log 在 wireup 阶段补，S1 范围不引入 logger 依赖）
        }
    }

    private void ToggleTheme()
    {
        var next = _theme.Current switch
        {
            AppTheme.Light  => AppTheme.Dark,
            AppTheme.Dark   => AppTheme.System,
            AppTheme.System => AppTheme.Light,
            _ => AppTheme.System
        };
        _theme.Apply(next);
    }
}
