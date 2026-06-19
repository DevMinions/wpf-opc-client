using System.Windows;
using System.Windows.Media;

namespace Dc.App.Views;

/// <summary>
/// 模态弹窗基类:全屏半透明遮罩盖满 Owner 工作区,卡片居中其上。
///
/// 解决两个问题:
/// 1. 透字——AllowsTransparency+Background=Transparent 窗口里,卡片用半透明
///    CardBackgroundFillColorDefaultBrush 时,卡片外的透明区透出 Owner 文字。
///    本基类窗口背景即遮罩色(#80000000),卡片用不透明底,杜绝透字。
/// 2. 居中——WindowStartupLocation=CenterOwner 在 Loaded 改 Width/Height 后失效
///    (尺寸变了但定位不重算)。本基类手动按 Owner 计算 Top/Left 居中。
///
/// 子类 XAML 根元素须是名为 Root 的 Grid(遮罩),内含居中容器+卡片。
/// </summary>
public abstract class ModalWindowBase : Window
{
    protected ModalWindowBase()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 遮罩铺满主屏工作区,卡片在遮罩内居中 → 屏中央。主窗口通常居中/铺满主屏,
        // 故卡片≈Owner 中央,且全屏遮罩彻底盖住一切背景(比精确对齐 Owner 更稳,
        // 不受 Owner 最大化/还原态、DPI、ActualWidth 时序影响)。
        var wa = SystemParameters.WorkArea;
        Width = wa.Width;
        Height = wa.Height;
        Left = wa.Left;
        Top = wa.Top;
    }
}
