using System.Windows;
using Dc.App.Views;

namespace Dc.App.Services;

public enum MessageDialogKind { Info, Warning, Error, Success }

/// <summary>
/// 原生 Win32 MessageBox(#32770 灰窗)与圆角编辑器风格撕裂的统一替代。
/// 用与编辑器同款的无边框圆角 + 阴影窗口,语义色图标点题。阻塞式(ShowDialog),
/// 保持调用处同步语义,便于从 MessageBox.Show 平迁。
/// </summary>
public static class MessageDialog
{
    public static void Show(string title, string message, MessageDialogKind kind = MessageDialogKind.Info)
        => Show(owner: null, title, message, kind);

    public static void Show(Window? owner, string title, string message, MessageDialogKind kind = MessageDialogKind.Info)
    {
        var w = Build(owner, title, message, kind, confirm: false);
        w.ShowDialog();
    }

    public static bool Confirm(string title, string message, MessageDialogKind kind = MessageDialogKind.Warning)
        => Confirm(owner: null, title, message, kind);

    public static bool Confirm(Window? owner, string title, string message, MessageDialogKind kind = MessageDialogKind.Warning)
    {
        var w = Build(owner, title, message, kind, confirm: true);
        return w.ShowDialog() == true;
    }

    private static MessageDialogWindow Build(Window? owner, string title, string message, MessageDialogKind kind, bool confirm)
    {
        var w = new MessageDialogWindow();
        w.SetContent(title, message, kind);
        w.Owner = owner ?? Application.Current?.MainWindow;
        if (confirm)
        {
            w.AddButton("取消", isPrimary: false, isCancel: true, isDefault: false);
            w.AddButton("确定", isPrimary: true, isCancel: false, isDefault: true);
        }
        else
        {
            w.AddButton("知道了", isPrimary: true, isCancel: true, isDefault: true);
        }
        return w;
    }
}
