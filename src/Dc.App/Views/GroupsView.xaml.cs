using System.Windows;
using System.Windows.Controls;
using Dc.App.ViewModels;

namespace Dc.App.Views;

public partial class GroupsView : UserControl
{
    public GroupsView() => InitializeComponent();

    private void OnClearFilter(object sender, RoutedEventArgs e)
    {
        if (DataContext is GroupsViewModel vm) vm.TaskFilter = null;
    }
}
