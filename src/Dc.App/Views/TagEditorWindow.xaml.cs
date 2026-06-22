using System.Windows;
using Dc.App.Services;
using Dc.App.ViewModels;

namespace Dc.App.Views;

public partial class TagEditorWindow : ModalWindowBase
{
    public TagEditorWindow() => InitializeComponent();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TagEditorViewModel vm) return;
        var errors = vm.Validate();
        if (errors.Count > 0)
        {
            MessageDialog.Show(this, "输入错误", string.Join("\n", errors), MessageDialogKind.Warning);
            return;
        }
        DialogResult = true;
    }
}
