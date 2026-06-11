using System.Windows;
using Dc.App.ViewModels;
using Dc.App.Views;
using Dc.Opc.Abstractions;

namespace Dc.App.Services;

public sealed class WpfBrowseDialog : IBrowseDialog
{
    private readonly IEnumerable<IOpcBrowserFactory> _factories;

    public WpfBrowseDialog(IEnumerable<IOpcBrowserFactory> factories)
    {
        _factories = factories;
    }

    public string? PickNodeId(string? initialServerUri = null)
    {
        var vm = new BrowseViewModel(_factories);
        if (!string.IsNullOrWhiteSpace(initialServerUri))
            vm.ServerUri = initialServerUri;

        var window = new BrowseDialogWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        var ok = window.ShowDialog() == true;
        var nodeId = ok ? vm.SelectedNode?.Node.Id : null;
        try { vm.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        return nodeId;
    }
}
