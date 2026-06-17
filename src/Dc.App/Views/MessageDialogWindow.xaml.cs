using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dc.App.Services;

namespace Dc.App.Views;

public partial class MessageDialogWindow : Window
{
    public MessageDialogWindow() => InitializeComponent();

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
