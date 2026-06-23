namespace Dc.App.Services;

// 走主题化 MessageDialog(圆角卡片+遮罩),与分组/Tag 删除确认一致;不再用原生灰窗 MessageBox。
public sealed class WpfConfirmDialog : IConfirmDialog
{
    public bool Confirm(string title, string message) =>
        MessageDialog.Confirm(title, message);
}
