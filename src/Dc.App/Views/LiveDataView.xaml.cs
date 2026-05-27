using System.Windows.Controls;
using Dc.App.ViewModels;

namespace Dc.App.Views;

public partial class LiveDataView : UserControl
{
    public LiveDataView()
    {
        InitializeComponent();
        // 仅页面可见时订阅数据洪流 + 批处理（单例 VM，标准/工作区内嵌两处共用此 View）。
        Loaded += (_, _) => (DataContext as LiveDataViewModel)?.Start();
        Unloaded += (_, _) => (DataContext as LiveDataViewModel)?.Stop();
    }
}
