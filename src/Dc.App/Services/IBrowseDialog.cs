using Dc.Opc.Abstractions;

namespace Dc.App.Services;

/// <summary>浏览选点结果:NodeId + 该节点数据类型码(0=未识别/默认,落兜底)。</summary>
public sealed record BrowsePick(string NodeId, int DataType);

public interface IBrowseDialog
{
    /// <summary>
    /// 打开浏览节点对话框选一个节点。给了连接信息(协议 + 端点 + DA ProgID/CLSID)就预填并自动连接,
    /// 用户直接看到地址树;连不上则显示错误 + 保留信息供重试。返回 NodeId + 节点数据类型(取消则 null)。
    /// </summary>
    BrowsePick? PickNodeId(
        OpcProtocol? protocol = null,
        string? serverUri = null,
        string? serverProgId = null,
        string? serverClsid = null,
        bool useSecurity = true);
}
