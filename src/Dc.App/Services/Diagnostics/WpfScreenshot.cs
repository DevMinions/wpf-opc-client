using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dc.App.Services.Diagnostics;

/// <summary>
/// 进程内把主窗口渲染成 PNG（RenderTargetBitmap）—— DevTools 式后台截图：
/// 不依赖物理屏幕、不受遮挡/最小化/锁屏影响、DPI 无关（按逻辑像素 96dpi 渲染，
/// 结果尺寸与显示器缩放无关、跨机一致）。供 MetricsHttpServer 的 /screenshot 注入。
/// </summary>
public static class WpfScreenshot
{
    /// <summary>渲染主窗口为 PNG 字节；无窗口/未就绪/渲染失败返回 null（端点据此给 503）。</summary>
    public static byte[]? Capture()
    {
        var app = Application.Current;
        var dispatcher = app?.Dispatcher;
        if (app is null || dispatcher is null) return null;

        // HTTP 在后台线程触发，必须切到 UI 线程访问视觉树。
        return dispatcher.Invoke(() =>
        {
            var window = app.MainWindow;
            if (window is null) return null;

            var width = (int)Math.Ceiling(window.ActualWidth);
            var height = (int)Math.Ceiling(window.ActualHeight);
            if (width < 1 || height < 1) return null;   // 还没布局完成

            // 96dpi = 按逻辑像素渲染：DPI 无关、尺寸稳定。视觉树即使窗口被遮挡/最小化也已 arrange。
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        });
    }
}
