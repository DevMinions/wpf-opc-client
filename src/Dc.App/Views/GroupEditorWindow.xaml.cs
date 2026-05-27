using System.Windows;
using Dc.App.ViewModels;

namespace Dc.App.Views;

public partial class GroupEditorWindow : Window
{
    public GroupEditorWindow() => InitializeComponent();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GroupEditorViewModel vm) return;
        var errors = vm.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join("\n", errors), "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
