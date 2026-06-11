using System.Windows;
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
            MessageBox.Show("请先选中一个节点", "提示");
            return;
        }
        if (vm.SelectedNode.Node.Kind != OpcNodeKind.Item)
        {
            MessageBox.Show("请选择叶子节点（Variable），文件夹节点不可作为 Tag", "提示");
            return;
        }
        DialogResult = true;
    }
}
