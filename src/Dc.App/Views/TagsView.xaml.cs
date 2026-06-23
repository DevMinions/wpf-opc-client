using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dc.App.ViewModels;

namespace Dc.App.Views;

public partial class TagsView : UserControl
{
    public TagsView() => InitializeComponent();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is TagsViewModel vm)
        {
            vm.LoadCommand.Execute(null);
        }
    }
}
