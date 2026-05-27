using System.Windows;
using Dc.App.ViewModels;
using Dc.App.Views;
using Dc.Domain.Entities;

namespace Dc.App.Services;

public sealed class ConfigEditorDialog : IConfigEditorDialog
{
    public ConfigEntry? Edit(ConfigEntry? existing)
    {
        var vm = new ConfigEntryEditorViewModel(existing);
        var window = new ConfigEntryEditorWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? vm.ToEntity() : null;
    }
}
