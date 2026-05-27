using System.Windows.Controls;
using Dc.App.ViewModels;

namespace Dc.App.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        // 自动刷新/暂停由 XAML TwoWay 绑定 AutoRefresh 驱动（不再用 Checked 事件）。
        // 仅页面可见时轮询读日志文件（单例 VM，避免全程无谓 I/O）。
        Loaded += (_, _) => (DataContext as LogsViewModel)?.Start();
        Unloaded += (_, _) => (DataContext as LogsViewModel)?.Stop();
    }
}
