using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;

namespace Dc.App.Services.Diagnostics;

/// <summary>
/// 进程内把当前激活窗口渲染成 PNG（RenderTargetBitmap）—— DevTools 式后台截图：
/// 不依赖物理屏幕、不受遮挡/最小化/锁屏影响、DPI 无关（按逻辑像素 96dpi 渲染，
/// 结果尺寸与显示器缩放无关、跨机一致）。供 MetricsHttpServer 的 /screenshot 注入。
/// </summary>
public static class WpfScreenshot
{
    /// <summary>渲染当前激活窗口为 PNG 字节；无窗口/未就绪/渲染失败返回 null（端点据此给 503）。</summary>
    public static byte[]? Capture()
    {
        var app = Application.Current;
        var dispatcher = app?.Dispatcher;
        if (app is null || dispatcher is null) return null;

        // HTTP 在后台线程触发，必须切到 UI 线程访问视觉树。
        return dispatcher.Invoke(() =>
        {
            // 截「当前激活窗口」：有模态对话框(新建任务/分组/Tag 编辑等)时截对话框，
            // 否则截主窗口 —— 这样后台截图能覆盖弹窗，不只是主界面。
            var window = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                         ?? app.MainWindow
                         ?? app.Windows.OfType<Window>().LastOrDefault();
            if (window is null) return null;

            var width = (int)Math.Ceiling(window.ActualWidth);
            var height = (int)Math.Ceiling(window.ActualHeight);
            if (width < 1 || height < 1) return null;   // 还没布局完成

            // 96dpi = 按逻辑像素渲染：DPI 无关、尺寸稳定。视觉树即使窗口被遮挡/最小化也已 arrange。
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(window);

            // 暗色主题的窗口底色来自 DWM Mica/backdrop（不在视觉树里），RenderTargetBitmap
            // 抓不到 → 透明区在 PNG 里发白。把渲染结果合成到「当前主题背景色」的不透明底上修正之。
            var bg = ResolveThemeBackground(window);
            var composed = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, width, height));
                dc.DrawImage(rtb, new Rect(0, 0, width, height));
            }
            composed.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(composed));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        });
    }

    /// <summary>取当前主题的不透明背景色：优先主题资源，缺失时按亮/暗兜底。</summary>
    private static Color ResolveThemeBackground(Window window)
    {
        // WPF-UI 主题字典定义了 ApplicationBackgroundColor（纯色）；强制不透明。
        if (window.TryFindResource("ApplicationBackgroundColor") is Color c)
            return Color.FromRgb(c.R, c.G, c.B);
        if (window.TryFindResource("ApplicationBackgroundBrush") is SolidColorBrush b)
            return Color.FromRgb(b.Color.R, b.Color.G, b.Color.B);

        return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? Color.FromRgb(0x20, 0x20, 0x20)
            : Color.FromRgb(0xFA, 0xFA, 0xFA);
    }
}
