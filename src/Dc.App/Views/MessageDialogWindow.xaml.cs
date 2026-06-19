using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dc.App.Services;

namespace Dc.App.Views;

public partial class MessageDialogWindow : Window
{
    public MessageDialogWindow()
    {
        InitializeComponent();
        // 遮罩需盖满 Owner 工作区:监听 SizeChanged 同步窗口尺寸。
        // 不能用 SizeToContent(那会缩到卡片大小,遮罩盖不住背后内容)。
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 窗口尺寸 = Owner 工作区,遮罩即铺满 Owner,弹窗视觉上居中其上。
        // 无 Owner(异常态)则退化为屏幕工作区。
        var area = Owner is Window owner ? owner.RenderSize
            : SystemParameters.WorkArea.Size;
        Width = area.Width;
        Height = area.Height;
        // 已 CenterOwner,但尺寸变了重定位确保居中。
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
    }

    /// <summary>加入一个底部按钮,点击后以 DialogResult=isPrimary 关闭。</summary>
    public Button AddButton(string text, bool isPrimary, bool isCancel, bool isDefault)
    {
        var btn = new Button
        {
            Content = text,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource(isPrimary ? "DcBtnPrimarySm" : "DcBtnGhostSm"),
            IsCancel = isCancel,
            IsDefault = isDefault,
            MinWidth = 80
        };
        btn.Click += (_, _) => DialogResult = isPrimary;
        FooterPanel.Children.Add(btn);
        return btn;
    }

    /// <summary>填充标题/正文/语义色图标。命名元素在 InitializeComponent 后即可访问。</summary>
    public void SetContent(string title, string message, MessageDialogKind kind)
    {
        HeaderText.Text = title;
        BodyText.Text = message;
        // 用语义色图标点题,代替原生 MessageBox 的 Information/Warning/Error 灰窗。
        IconText.Text = kind switch
        {
            MessageDialogKind.Warning => "⚠",
            MessageDialogKind.Error => "✕",
            MessageDialogKind.Success => "✓",
            _ => "ℹ"
        };
        IconText.Foreground = (Brush)FindResource(kind switch
        {
            MessageDialogKind.Warning => "SystemFillColorCautionBrush",
            MessageDialogKind.Error => "SystemFillColorCriticalBrush",
            MessageDialogKind.Success => "SystemFillColorSuccessBrush",
            _ => "AccentTextFillColorPrimaryBrush"
        });
    }
}
