namespace Dc.Infrastructure.Orchestration;

/// <summary>采集任务连接生命周期状态（编排器拥有，UI 与 /metrics 消费）。
/// 仅描述「运行集里活任务」的连接态；用户停止的任务直接从快照消失（=行消失），非此处某值。</summary>
public enum ConnectionState
{
    Connecting, // 初次/重连的 connect 阶段进行中
    Running,    // 已连接+订阅，心跳正常流动
    Restarting, // 心跳超时，看门狗正在原地重绑重连
    Faulted     // 连续 ≥FaultThreshold 次重启仍未恢复心跳（疑似 server 长断）
}
