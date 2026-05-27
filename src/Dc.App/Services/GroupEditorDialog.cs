using System.Windows;
using Dc.App.ViewModels;
using Dc.App.Views;
using Dc.Domain.Entities;

namespace Dc.App.Services;

public sealed class GroupEditorDialog : IGroupEditorDialog
{
    public Group? Edit(IEnumerable<CollectorTask> availableTasks, Group? existing, CollectorTask? defaultTask = null)
    {
        var vm = new GroupEditorViewModel(availableTasks, existing, defaultTask);
        var window = new GroupEditorWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? vm.ToEntity() : null;
    }
}
