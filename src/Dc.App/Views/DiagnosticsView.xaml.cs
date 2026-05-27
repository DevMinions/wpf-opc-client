using System.Windows.Controls;
using Dc.App.ViewModels;

namespace Dc.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
        // 仅页面可见时轮询（单例 VM，标准/工作区内嵌两处共用此 View）。
        Loaded += (_, _) => (DataContext as DiagnosticsViewModel)?.Start();
        Unloaded += (_, _) => (DataContext as DiagnosticsViewModel)?.Stop();
    }
}
