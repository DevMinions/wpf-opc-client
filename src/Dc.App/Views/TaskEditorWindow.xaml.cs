using System.Windows;
using Dc.App.Services;
using Dc.App.Services.I18n;
using Dc.App.ViewModels;

namespace Dc.App.Views;

public partial class TaskEditorWindow : ModalWindowBase
{
    public TaskEditorWindow() => InitializeComponent();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TaskEditorViewModel vm) return;
        var errors = vm.Validate();
        if (errors.Count > 0)
        {
            MessageDialog.Show(this, LocalizationManager.Instance["Dialog_ValidationErrorTitle"], string.Join("\n", errors), MessageDialogKind.Warning);
            return;
        }
        DialogResult = true;
    }
}
