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

    public BrowsePick? PickNodeId(
        OpcProtocol? protocol = null,
        string? serverUri = null,
        string? serverProgId = null,
        string? serverClsid = null,
        bool useSecurity = true)
    {
        var vm = new BrowseViewModel(_factories);
        if (protocol is not null && vm.AvailableProtocols.Contains(protocol.Value))
            vm.Protocol = protocol.Value;
        if (!string.IsNullOrWhiteSpace(serverUri)) vm.ServerUri = serverUri;
        if (!string.IsNullOrWhiteSpace(serverProgId)) vm.ServerProgId = serverProgId;
        if (!string.IsNullOrWhiteSpace(serverClsid)) vm.ServerClsid = serverClsid;
        vm.UseSecurity = useSecurity;   // 与任务安全设置同步:无安全任务浏览也走无安全,否则报安全错

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
        // 带回 NodeId + 节点真实类型码(复用 BrowseViewModel.MapDataType),单选取点与批量加 Tag 一样自动填类型
        BrowsePick? pick = ok && vm.SelectedNode is not null
            ? new BrowsePick(vm.SelectedNode.Node.Id, vm.SelectedNodeDataTypeCode)
            : null;
        try { vm.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        return pick;
    }
}
