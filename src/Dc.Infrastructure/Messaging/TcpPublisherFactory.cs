namespace Dc.Infrastructure.Messaging;

public sealed class TcpPublisherFactory : IPublisherFactory
{
    private readonly IMessageSerializer _serializer;
    private readonly OutboundQueueOptions _queueOptions;

    /// <summary>批量发送间隔（毫秒），默认 50。</summary>
    public int BatchIntervalMs { get; init; } = 50;

    /// <summary>每批最大帧数，默认 64。</summary>
    public int BatchSize { get; init; } = 64;

    // queueOptions 可空，默认 Disabled — 兼容老调用方（包括单元测试 new TcpPublisherFactory(serializer)）
    public TcpPublisherFactory(IMessageSerializer serializer, OutboundQueueOptions? queueOptions = null)
    {
        _serializer = serializer;
        _queueOptions = queueOptions ?? new OutboundQueueOptions();
    }

    public IPublisher Create(string address)
    {
        // 每个 task 一个 queue 文件，按 publisher 地址命名避免冲撞
        IOutboundQueue? queue = null;
        if (_queueOptions.Enabled)
        {
            var safeName = address.Replace(':', '_').Replace('/', '_');
            var baseDir = Path.IsPathRooted(_queueOptions.Directory)
                ? _queueOptions.Directory
                : Path.Combine(AppContext.BaseDirectory, _queueOptions.Directory);
            var dataPath = Path.Combine(baseDir, $"{safeName}.bin");
            queue = new OutboundQueue(dataPath, _queueOptions.MaxBytes);
        }

        var idx = address.LastIndexOf(':');
        if (idx <= 0) throw new ArgumentException($"Invalid address '{address}', expected host:port", nameof(address));
        var host = address[..idx];
        if (!int.TryParse(address[(idx + 1)..], out var port))
            throw new ArgumentException($"Invalid port in '{address}'", nameof(address));

        return new BatchingTcpPublisher(host, port, _serializer, queue, BatchIntervalMs, BatchSize);
    }
}
