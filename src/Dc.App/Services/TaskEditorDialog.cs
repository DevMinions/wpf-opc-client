using System.Windows;
using Dc.App.ViewModels;
using Dc.App.Views;
using Dc.Domain.Entities;
using Dc.Opc.Abstractions;

namespace Dc.App.Services;

public sealed class TaskEditorDialog : ITaskEditorDialog
{
    private readonly IEnumerable<IOpcBrowserFactory> _browserFactories;

    public TaskEditorDialog(IEnumerable<IOpcBrowserFactory> browserFactories)
    {
        _browserFactories = browserFactories;
    }

    public CollectorTask? Edit(CollectorTask? existing)
    {
        var vm = new TaskEditorViewModel(existing, _browserFactories);
        var window = new TaskEditorWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? vm.ToEntity() : null;
    }
}
