namespace Dc.Infrastructure.Messaging;

// 断网缓存配置。Disabled 时不创建文件，Publisher 退回原行为（失败即抛、不缓存）。
public sealed record OutboundQueueOptions
{
    public bool Enabled { get; init; } = false;

    // 文件存放目录。默认相对 AppContext.BaseDirectory。
    public string Directory { get; init; } = "queue";

    // 文件大小上限（字节）。超限时 drop-oldest（rewrite 文件去掉队首已读过 + 必要时再丢未读最旧帧）。
    public long MaxBytes { get; init; } = 100L * 1024 * 1024; // 100MB
}
