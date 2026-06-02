namespace Dc.Infrastructure.Messaging;

/// <summary>
/// 可选诊断接口：异步/批量 Publisher 的发送在后台进行，PublishAsync 立即返回，
/// 调用方（TaskOrchestrator）无法通过 try/catch 观测后台发送失败。实现此接口暴露
/// 后台发送错误累计数，供诊断/健康评估折入统计。
/// </summary>
public interface IPublisherHealth
{
    /// 后台发送失败累计次数（每次 flush 批失败计一次）。
    long SendErrorCount { get; }

    /// 当前离线队列未发字节数（无队列时 0）。
    long PendingBytes { get; }

    /// 累计因队列溢出被 drop-oldest 丢弃的帧数（无队列时 0）。
    long DroppedFrameCount { get; }
}
