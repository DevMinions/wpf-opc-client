using System.Windows;
using Dc.App.Services;
using Dc.App.Services.I18n;
using Dc.App.ViewModels;

namespace Dc.App.Views;

public partial class ConfigEntryEditorWindow : ModalWindowBase
{
    public ConfigEntryEditorWindow() => InitializeComponent();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigEntryEditorViewModel vm) return;
        var errors = vm.Validate();
        if (errors.Count > 0)
        {
            MessageDialog.Show(this, LocalizationManager.Instance["Dialog_ValidationErrorTitle"], string.Join("\n", errors), MessageDialogKind.Warning);
            return;
        }
        DialogResult = true;
    }
}
