using System.Windows;
using System.Windows.Media;

namespace Dc.App.Views;

/// <summary>
/// 模态弹窗基类:半透明遮罩只盖 Owner 窗口范围(非全屏),卡片居中其上。
///
/// 解决两个问题:
/// 1. 透字——AllowsTransparency+Background=Transparent 窗口里,卡片用半透明
///    CardBackgroundFillColorDefaultBrush 时,卡片外透明区透出 Owner 文字。
///    本基类窗口背景即遮罩色(#80000000),卡片用不透明底,杜绝透字。
/// 2. 居中——WindowStartupLocation=CenterOwner 在改 Width/Height 后失效。
///    本基类手动对齐 Owner 范围,卡片在遮罩内居中 → 正中 Owner。
///
/// 遮罩范围 = Owner 窗口的屏幕矩形(只盖软件窗口,桌面/其他 app 不受影响)。
/// 用 ContentRendered 而非 Loaded:此时 Owner 已完成布局,ActualWidth/Height 准确
/// (Loaded 早期 ActualWidth 可能未就绪,导致遮罩尺寸错位)。
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
        ContentRendered += OnContentRendered;
    }

    private void OnContentRendered(object? sender, System.EventArgs e)
    {
        AlignToOwner();

        // Owner 移动/缩放时同步遮罩位置尺寸(用户拖动主窗口时弹窗跟随)。
        var owner = Owner;
        if (owner is not null)
        {
            owner.LocationChanged += (_, _) => AlignToOwner();
            owner.SizeChanged += (_, _) => AlignToOwner();
        }
    }

    private void AlignToOwner()
    {
        var owner = Owner;
        if (owner is null || owner.ActualWidth <= 0)
        {
            // 无 Owner 或 Owner 未就绪:退化为屏幕工作区(异常态兜底)。
            var wa = SystemParameters.WorkArea;
            Width = wa.Width;
            Height = wa.Height;
            Left = wa.Left;
            Top = wa.Top;
            return;
        }

        // 遮罩对齐 Owner 窗口矩形。Left/Top/ActualWidth/ActualHeight 均为 DIP(逻辑像素),
        // 与本窗口 Left/Top/Width/Height 同尺度,直接赋值即可。不用 PointToScreen
        // (它返回物理像素,与 DIP 的 Left/Top 在非 100% DPI 下不一致,导致遮罩错位)。
        Left = owner.Left;
        Top = owner.Top;
        Width = owner.ActualWidth;
        Height = owner.ActualHeight;
    }
}
