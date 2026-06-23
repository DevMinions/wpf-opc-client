using System.ComponentModel;
using System.Windows;
using Dc.App.Views;
using Dc.App.ViewModels.Shell;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Wpf.Ui.Controls;

namespace Dc.App.Views.Shell;

public partial class ShellWindow : FluentWindow
{
    private readonly ShellViewModel _vm;
    private readonly Dc.App.Services.I18n.ILocalizer _loc;
    private bool _reallyExit;     // 仅托盘「退出」时置真，让关闭真正退出而非进托盘
    private bool _trayHintShown;  // 关闭进托盘的气泡提示一进程只弹一次

    public ShellWindow(ShellViewModel vm, Dc.App.Services.I18n.ILocalizer loc, Dc.App.Services.I18n.ILanguageService language)
    {
        InitializeComponent();
        _vm = vm;
        _loc = loc;
        DataContext = vm;

        BuildMenuItems();
        WireFooter();
        // 初始选中 + 导航(仅一次,不放进 BuildMenuItems,以免重建时重复导航)
        if (_vm.Routes.Count > 0)
        {
            SelectNavItemByKey(_vm.Routes[0].Key);
            _vm.NavigateCommand.Execute(_vm.Routes[0].Key);
        }
        // 语言切换 → 重建导航项(文字按新 culture 取),保持当前选中
        language.LanguageChanged += _ => Dispatcher.Invoke(RebuildMenuItems);

        Closing += OnClosing;   // 关闭(X) → 隐藏到托盘（最小化保持留任务栏的默认行为）
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void BuildMenuItems()
    {
        string? lastGroup = null;
        foreach (var route in _vm.Routes)
        {
            if (route.GroupHeader is not null && route.GroupHeader != lastGroup)
            {
                // 文字分组头（采集/全局监控/系统），对齐原型 .nav .grp：左缩进 12、上间距、小字号。
                // 不设缩进时 wpfui 默认贴左边框，与原型不一致。
                RootNav.MenuItems.Add(new NavigationViewItemHeader
                {
                    Text = _loc[route.GroupHeader],
                    Margin = new Thickness(12, 10, 0, 2),
                    FontSize = 11
                });
                lastGroup = route.GroupHeader;
            }
            var item = new NavigationViewItem
            {
                Content = _loc[route.Title],
                Tag = route.Key,
                Icon = ResolveIcon(route.Icon)
            };
            // wpfui 的 ItemInvoked/SelectionChanged 绑定到其页面导航管线（需 TargetPageType），
            // 我们走 ContentControl 投影故收不到 —— 直接挂 PreviewMouseLeftButtonUp（普通 WPF 输入事件，点击必触发）。
            item.PreviewMouseLeftButtonUp += OnNavItemClicked;
            RootNav.MenuItems.Add(item);
        }
    }

    private void WireFooter()
    {
        // footer「关于」item 点击处理(一次性;Content 由 XAML {loc:Loc} 实时刷,无需重建)
        foreach (var obj in RootNav.FooterMenuItems)
            if (obj is NavigationViewItem fi)
                fi.PreviewMouseLeftButtonUp += OnNavItemClicked;
    }

    private void RebuildMenuItems()
    {
        foreach (var obj in RootNav.MenuItems)
            if (obj is NavigationViewItem nvi)
                nvi.PreviewMouseLeftButtonUp -= OnNavItemClicked;
        RootNav.MenuItems.Clear();
        BuildMenuItems();
        if (!string.IsNullOrEmpty(_vm.SelectedRouteKey))
            SelectNavItemByKey(_vm.SelectedRouteKey);
    }

    private void OnNavItemClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is NavigationViewItem item && item.Tag is string key)
        {
            Serilog.Log.Debug("Nav clicked (mouse): {Key}", key);
            HandleNavKey(key);
        }
    }

    private void HandleNavKey(string key)
    {
        if (key == "about")
        {
            // Footer「关于」不是路由 — 弹 About 对话框
            OnTrayAbout(this, new RoutedEventArgs());
            return;
        }

        // 手动同步 NavigationView 选中状态
        // 因为我们走 ContentControl 自定义导航，不走 wpfui 的 TargetPageType 管线，
        // 所以需要手动将点击的 item 设为 SelectedItem，否则无选中高亮效果。
        SelectNavItemByKey(key);

        _vm.NavigateCommand.Execute(key);
    }

    private void SelectNavItemByKey(string key)
    {
        NavigationViewItem? target = null;
        foreach (var obj in RootNav.MenuItems)
        {
            if (obj is NavigationViewItem item && item.Tag as string == key)
            {
                target = item;
                break;
            }
        }
        if (target is null)
        {
            foreach (var obj in RootNav.FooterMenuItems)
            {
                if (obj is NavigationViewItem item && item.Tag as string == key)
                {
                    target = item;
                    break;
                }
            }
        }

        if (target is null) return;

        // 选中高亮：wpfui 走 ContentControl 自定义导航不进内部 pipeline，仅设 SelectedItem
        // 不会把项的 IsActive 置真（IsActive 才驱动选中视觉：accent 条 + 软底）。
        // 故手动给目标项设 IsActive、其余清零（IsActiveProperty 公开、setter internal → SetCurrentValue）。
        foreach (var obj in RootNav.MenuItems)
            if (obj is NavigationViewItem nvi)
                nvi.SetCurrentValue(NavigationViewItem.IsActiveProperty, ReferenceEquals(nvi, target));
        foreach (var obj in RootNav.FooterMenuItems)
            if (obj is NavigationViewItem nvi)
                nvi.SetCurrentValue(NavigationViewItem.IsActiveProperty, ReferenceEquals(nvi, target));

        // 仍同步 SelectedItem（保持 NavigationView 内部状态一致）。
        var dpField = typeof(NavigationView).GetField(
            "SelectedItemProperty",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (dpField?.GetValue(null) is System.Windows.DependencyProperty dp)
        {
            RootNav.SetCurrentValue(dp, target);
        }
    }

    private static IconElement? ResolveIcon(string symbolName)
    {
        return Enum.TryParse<SymbolRegular>(symbolName, out var s)
            ? new SymbolIcon { Symbol = s }
            : null;
    }

    // 备用：wpfui ItemInvoked（若它在某些版本确实触发，与鼠标处理器二选一生效，靠 Navigate 内的 guard 防重复）
    private void OnNavigationItemInvoked(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem item && item.Tag is string key)
        {
            Serilog.Log.Debug("Nav invoked (wpfui): {Key}", key);
            HandleNavKey(key);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 点 X 不退出，隐藏到托盘；真正退出只走托盘菜单「退出」（_reallyExit）。
        if (_reallyExit) return;
        e.Cancel = true;
        Hide();

        // 首次进托盘提示用户程序仍在运行、如何唤回/退出，避免误以为已关闭。
        if (!_trayHintShown && TrayIcon is not null)
        {
            _trayHintShown = true;
            TrayIcon.ShowNotification(
                title: _loc["Tray_BalloonTitle"],
                message: _loc["Tray_BalloonMessage"],
                icon: NotificationIcon.Info);
        }
    }

    private void OnTrayShow(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnTrayToggleTheme(object sender, RoutedEventArgs e)
    {
        _vm.ToggleThemeCommand.Execute(null);
    }

    private void OnTrayAbout(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;        // 放行 OnClosing，真正退出
        Application.Current.Shutdown();
    }

    private Dc.App.ViewModels.Dashboard.DashboardViewModel? _dashboardVm;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 监听 ViewModel 导航变化，同步 NavigationView 选中状态
        _vm.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.SelectedRouteKey))
            {
                var key = _vm.SelectedRouteKey;
                if (!string.IsNullOrEmpty(key)) SelectNavItemByKey(key);
            }
        };

        if (_vm.CurrentContent is Dc.App.ViewModels.Dashboard.DashboardViewModel dashVm)
        {
            dashVm.Start(Dispatcher);
            _dashboardVm = dashVm;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _dashboardVm?.Stop();
        // 必须显式 Dispose，否则进程退出后系统托盘里会残留幽灵图标，
        // 直到用户鼠标划过托盘区域才被 Shell 清理。
        TrayIcon?.Dispose();
    }
}
