using System;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Dc.App.Services;

// 包 WPF-UI ISnackbarService;非模态 toast。SetSnackbarPresenter 在 ShellWindow 加载后调（任务 4）。
public sealed class SnackbarNotificationService : INotificationService
{
    private readonly ISnackbarService _snackbar;

    public SnackbarNotificationService(ISnackbarService snackbar) => _snackbar = snackbar;

    public void ShowError(string title, string message) =>
        _snackbar.Show(title, message, ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
}
