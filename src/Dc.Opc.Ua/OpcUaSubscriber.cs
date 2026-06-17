using System.Threading.Channels;
using Dc.Opc.Abstractions;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Dc.Opc.Ua;

public sealed class OpcUaSubscriber : IOpcSubscriber
{
    private readonly Channel<TagValue> _values = Channel.CreateUnbounded<TagValue>();
    private readonly Channel<HeartBeat> _heartbeats = Channel.CreateUnbounded<HeartBeat>();
    private readonly OpcConnectionOptions _options;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, MonitoredItem> _items = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private Session? _session;
    private Subscription? _subscription;
    private SessionReconnectHandler? _reconnectHandler;
    // 护住 _session/_subscription/_items 的跨线程访问：重连回调在 SDK 后台线程触发，
    // 而 Subscribe/Unsubscribe 由编排器线程调用。
    private readonly object _reconnectLock = new();
    private Task? _heartbeatTask;
    private bool _disposed;

    public string ChannelId { get; }
    public ChannelReader<TagValue> TagValues => _values.Reader;
    public ChannelReader<HeartBeat> Heartbeats => _heartbeats.Reader;

    public OpcUaSubscriber(string channelId, OpcConnectionOptions options, ILogger? logger = null)
    {
        ChannelId = channelId;
        _options = options;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var appConfig = OpcUaApplicationConfig.Build(_options.ConnectTimeout);
        await appConfig.Validate(ApplicationType.Client).ConfigureAwait(false);

        var appInstance = new ApplicationInstance
        {
            ApplicationName = appConfig.ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = appConfig
        };
        await appInstance.CheckApplicationInstanceCertificate(
            silent: true,
            minimumKeySize: OpcUaApplicationConfig.MinimumCertificateKeySize).ConfigureAwait(false);

        var endpointDescription = CoreClientUtils.SelectEndpoint(
            appConfig,
            _options.ServerUri,
            useSecurity: _options.UseSecurity && OpcUaApplicationConfig.UseSecurity);

        _logger?.LogInformation("OPC UA 连接 {ServerUri}，安全策略 {Policy}/{Mode}（{ChannelId}）",
            _options.ServerUri, endpointDescription.SecurityPolicyUri, endpointDescription.SecurityMode, ChannelId);

        var configuredEndpoint = new ConfiguredEndpoint(
            collection: null,
            description: endpointDescription,
            configuration: EndpointConfiguration.Create(appConfig));

        _session = await Session.Create(
            configuration: appConfig,
            endpoint: configuredEndpoint,
            updateBeforeConnect: false,
            sessionName: $"DcCollector-{ChannelId}",
            sessionTimeout: 60000,
            identity: new UserIdentity(),
            preferredLocales: null).ConfigureAwait(false);

        // KeepAlive 探测会话健康；探到异常即触发自动重连（见 OnKeepAlive）。
        _session.KeepAliveInterval = (int)_options.KeepAliveInterval.TotalMilliseconds;
        _session.KeepAlive += OnKeepAlive;

        _subscription = new Subscription(_session.DefaultSubscription)
        {
            DisplayName = $"DcSubscription-{ChannelId}",
            PublishingInterval = (int)_options.SamplingInterval.TotalMilliseconds,
            KeepAliveCount = 10,
            LifetimeCount = 100,
            MaxNotificationsPerPublish = 1000,
            Priority = 0
        };
        _session.AddSubscription(_subscription);
        _subscription.Create();

        _heartbeatTask = HeartbeatLoopAsync(_disposeCts.Token);
    }

    public Task SubscribeAsync(IReadOnlyCollection<TagDescriptor> tags, CancellationToken ct = default)
    {
        lock (_reconnectLock)
        {
            if (_subscription is null) throw new InvalidOperationException("ConnectAsync must be called first");

            var newItems = new List<MonitoredItem>();
            foreach (var tag in tags)
            {
                if (_items.ContainsKey(tag.Item)) continue;
                var item = new MonitoredItem(_subscription.DefaultItem)
                {
                    DisplayName = tag.Item,
                    StartNodeId = tag.Item,
                    AttributeId = Attributes.Value,
                    SamplingInterval = (int)_options.SamplingInterval.TotalMilliseconds,
                    QueueSize = 1,
                    DiscardOldest = true,
                    MonitoringMode = MonitoringMode.Reporting
                };
                item.Notification += OnNotification;
                _subscription.AddItem(item);
                _items[tag.Item] = item;
                newItems.Add(item);
            }
            if (newItems.Count > 0)
            {
                _subscription.ApplyChanges();
                _logger?.LogInformation("OPC UA 新增监控项 {Count}（{ChannelId}）", newItems.Count, ChannelId);
            }
        }
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(IReadOnlyCollection<string> tagItems, CancellationToken ct = default)
    {
        lock (_reconnectLock)
        {
            if (_subscription is null) return Task.CompletedTask;
            bool changed = false;
            foreach (var item in tagItems)
            {
                if (_items.TryGetValue(item, out var mi))
                {
                    mi.Notification -= OnNotification;
                    _subscription.RemoveItem(mi);
                    _items.Remove(item);
                    changed = true;
                }
            }
            if (changed)
            {
                _subscription.ApplyChanges();
                _logger?.LogInformation("OPC UA 移除监控项 {Count}（{ChannelId}）", tagItems.Count, ChannelId);
            }
        }
        return Task.CompletedTask;
    }

    private void OnNotification(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
    {
        foreach (var notification in monitoredItem.DequeueValues())
        {
            ushort quality;
            if (StatusCode.IsBad(notification.StatusCode)) quality = 0x00;
            else if (StatusCode.IsUncertain(notification.StatusCode)) quality = 0x40;
            else quality = 0xC0;

            var ts = notification.SourceTimestamp == DateTime.MinValue
                ? DateTimeOffset.UtcNow
                : new DateTimeOffset(DateTime.SpecifyKind(notification.SourceTimestamp, DateTimeKind.Utc));

            _values.Writer.TryWrite(new TagValue(monitoredItem.DisplayName, notification.Value, quality, ts));
        }
    }

    // KeepAlive 探到会话异常（服务器不可达/重启）→ 启动 SessionReconnectHandler 秒级自动恢复，
    // 由 SDK 迁移或重建会话+订阅；无需等 TaskOrchestrator 的 150s 看门狗做全量重启。
    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (_disposed || !ServiceResult.IsBad(e.Status)) return;

        lock (_reconnectLock)
        {
            if (_disposed || _reconnectHandler is not null) return;   // 已在重连中
            _logger?.LogWarning("OPC UA KeepAlive 异常 {Status}，启动自动重连（{ChannelId}）", e.Status, ChannelId);
            _reconnectHandler = new SessionReconnectHandler();
            _reconnectHandler.BeginReconnect(
                _session!, (int)_options.ReconnectPeriod.TotalMilliseconds, OnReconnectComplete);
        }
    }

    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        lock (_reconnectLock)
        {
            // 过期/已释放回调直接丢弃
            if (_disposed || _reconnectHandler is null || !ReferenceEquals(sender, _reconnectHandler)) return;

            var recovered = _reconnectHandler.Session as Session;
            _reconnectHandler.Dispose();
            _reconnectHandler = null;

            // 服务器重启后旧会话失效，SDK 会换一个新会话：换引用并把 KeepAlive 重挂到新会话
            if (recovered is not null && !ReferenceEquals(recovered, _session))
            {
                if (_session is not null) _session.KeepAlive -= OnKeepAlive;
                _session = recovered;
                _session.KeepAlive += OnKeepAlive;
            }

            // 订阅可能被迁移（网络抖动）或重建（服务器重启）：重抓 subscription、重挂通知、重建 _items 索引，
            // 兼容两种路径——无论 MonitoredItem 对象被复用还是新建，通知处理器都保证已挂上。
            var sub = _session?.Subscriptions?.FirstOrDefault();
            if (sub is not null)
            {
                _subscription = sub;
                _items.Clear();
                foreach (var mi in sub.MonitoredItems)
                {
                    mi.Notification -= OnNotification;
                    mi.Notification += OnNotification;
                    if (!string.IsNullOrEmpty(mi.DisplayName)) _items[mi.DisplayName] = mi;
                }
            }
            _logger?.LogInformation("OPC UA 自动重连完成（{ChannelId}），监控项 {Count}", ChannelId, _items.Count);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var s = _session;
            if (s is null) continue;
            // 活性门控:Session.Connected 是缓存标志(server 断开后不翻假),必须叠加 !KeepAliveStopped
            // (SDK keepalive 活性信号:keepalive 在阈值窗口内无响应即翻真,通信恢复翻假)。
            // 真掉线+自动重连持续失败时它保持真→心跳停写→编排器看门狗按设计进 Restarting/Faulted;
            // 瞬断重连进行中它会短暂为真(短暂停心跳),但生产默认 HeartbeatTimeout 远大于单次心跳间隔,不会误触发重启。
            if (s.Connected && !s.KeepAliveStopped)
            {
                _heartbeats.Writer.TryWrite(new HeartBeat(ChannelId, DateTimeOffset.UtcNow, s.Endpoint?.EndpointUrl));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();

        // 停掉可能在途的自动重连，并摘掉 KeepAlive（_disposed 已置真，OnKeepAlive 不会再启新重连）
        lock (_reconnectLock)
        {
            try { _reconnectHandler?.Dispose(); } catch { }
            _reconnectHandler = null;
            if (_session is not null) _session.KeepAlive -= OnKeepAlive;
        }

        try { if (_heartbeatTask is not null) await _heartbeatTask.ConfigureAwait(false); } catch { }

        try
        {
            if (_subscription is not null)
            {
                foreach (var mi in _items.Values) mi.Notification -= OnNotification;
                _items.Clear();
                _subscription.Delete(silent: true);
                _subscription.Dispose();
            }
        }
        catch { }

        try
        {
            if (_session is not null)
            {
                _session.Close();
                _session.Dispose();
            }
        }
        catch { }

        _values.Writer.TryComplete();
        _heartbeats.Writer.TryComplete();
        _disposeCts.Dispose();
    }
}
