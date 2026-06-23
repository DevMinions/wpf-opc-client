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

    public string? PickNodeId(
        OpcProtocol? protocol = null,
        string? serverUri = null,
        string? serverProgId = null,
        string? serverClsid = null)
    {
        var vm = new BrowseViewModel(_factories);
        if (protocol is not null && vm.AvailableProtocols.Contains(protocol.Value))
            vm.Protocol = protocol.Value;
        if (!string.IsNullOrWhiteSpace(serverUri)) vm.ServerUri = serverUri;
        if (!string.IsNullOrWhiteSpace(serverProgId)) vm.ServerProgId = serverProgId;
        if (!string.IsNullOrWhiteSpace(serverClsid)) vm.ServerClsid = serverClsid;

        var window = new BrowseDialogWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        // 已给端点 → 自动连一次,用户直接看到地址树(ShowDialog 的嵌套消息循环里 async 续跑;
        // 连不上则对话框显示错误 + 保留预填信息供重试)。
        if (!string.IsNullOrWhiteSpace(serverUri) && vm.ConnectCommand.CanExecute(null))
            vm.ConnectCommand.Execute(null);

        var ok = window.ShowDialog() == true;
        var nodeId = ok ? vm.SelectedNode?.Node.Id : null;
        try { vm.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        return nodeId;
    }
}
