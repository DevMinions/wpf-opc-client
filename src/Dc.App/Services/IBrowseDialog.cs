using Dc.Opc.Abstractions;

namespace Dc.App.Services;

public interface IBrowseDialog
{
    /// <summary>
    /// 打开浏览节点对话框选一个 NodeId。给了连接信息(协议 + 端点 + DA ProgID/CLSID)就预填并自动连接,
    /// 用户直接看到地址树;连不上则显示错误 + 保留信息供重试。
    /// </summary>
    string? PickNodeId(
        OpcProtocol? protocol = null,
        string? serverUri = null,
        string? serverProgId = null,
        string? serverClsid = null,
        bool useSecurity = true);
}
