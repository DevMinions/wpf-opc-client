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
            useSecurity: _options.UseSecurity);

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
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(IReadOnlyCollection<string> tagItems, CancellationToken ct = default)
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

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var s = _session;
            if (s is null) continue;
            if (s.Connected)
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
