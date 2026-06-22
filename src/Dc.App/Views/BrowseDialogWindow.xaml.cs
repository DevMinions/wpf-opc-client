using System.Windows;
using Dc.App.Services;
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
            MessageDialog.Show(this, "提示", "请先选中一个节点", MessageDialogKind.Warning);
            return;
        }
        if (vm.SelectedNode.Node.Kind != OpcNodeKind.Item)
        {
            MessageDialog.Show(this, "提示", "请选择叶子节点（Variable），文件夹节点不可作为 Tag", MessageDialogKind.Warning);
            return;
        }
        DialogResult = true;
    }
}
