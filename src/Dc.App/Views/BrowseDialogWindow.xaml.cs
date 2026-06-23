using System.Windows;
using Dc.App.Services;
using Dc.App.Services.I18n;
using Dc.App.ViewModels;
using Dc.Opc.Abstractions;

namespace Dc.App.Views;

public partial class BrowseDialogWindow : Window
{
    public BrowseDialogWindow() => InitializeComponent();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BrowseViewModel vm) return;
        if (vm.SelectedNode is null)
        {
            MessageDialog.Show(this, LocalizationManager.Instance["Dialog_Notice"], LocalizationManager.Instance["BrowseDialog_SelectNodeFirst"], MessageDialogKind.Warning);
            return;
        }
        if (vm.SelectedNode.Node.Kind != OpcNodeKind.Item)
        {
            MessageDialog.Show(this, LocalizationManager.Instance["Dialog_Notice"], LocalizationManager.Instance["BrowseDialog_LeafNodeOnly"], MessageDialogKind.Warning);
            return;
        }
        DialogResult = true;
    }
}
