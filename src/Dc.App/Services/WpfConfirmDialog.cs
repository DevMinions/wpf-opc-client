using System.Windows;

namespace Dc.App.Services;

public sealed class WpfConfirmDialog : IConfirmDialog
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes;
}
