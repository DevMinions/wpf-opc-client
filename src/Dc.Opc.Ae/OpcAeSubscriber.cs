using System.Collections.Concurrent;
using System.Threading.Channels;
using Dc.Opc.Abstractions;
using Microsoft.Extensions.Logging;
using Technosoftware.DaAeHdaClient;
using Technosoftware.DaAeHdaClient.Ae;
using ComFactory = Technosoftware.DaAeHdaClient.Com.Factory;

namespace Dc.Opc.Ae;

// OPC AE 订阅器：把 TsCAeEventNotification 映射成 TagValue 走同一条下游管线。
//   - Item     = event.SourceID（任务的 Tag.Item 用源 ID 匹配；"*" 表示全收）
//   - Value    = Dictionary<string,object?> 把事件字段序列化进去（severity/message/condition…）
//   - Quality  = 0xC0 (Good)，因为事件是已发生的事实；severity 用 Value.severity 表达
//   - Timestamp= event.Time
//
// 设计取舍：AE 没有"按 item 订阅"的概念，TsCAeSubscription 是订阅级 + filter。
// 我们订阅时不下 filter（取全部），用 _allowedSources 在回调里筛源 ID。这样：
//   * Tag.Item="*"  → 全收
//   * Tag.Item="ReactorA/HiTemp"     → 严格匹配 SourceID
//   * 多个 Tag 项     → 任一匹配即收
// 简单且贴合既有 Tag/Group 数据模型。
public sealed class OpcAeSubscriber : IOpcSubscriber
{
    private readonly Channel<TagValue> _values = Channel.CreateUnbounded<TagValue>();
    private readonly Channel<HeartBeat> _heartbeats = Channel.CreateUnbounded<HeartBeat>();
    private readonly OpcConnectionOptions _options;
    private readonly object _comLock = new();
    private readonly ConcurrentDictionary<string, byte> _allowedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposeCts = new();
    private TsCAeServer? _server;
    private TsCAeSubscription? _subscription;
    private Task? _heartbeatTask;
    // volatile：写在订阅线程（orchestrator 锁内串行），读在 COM 回调线程 OnEvents，需内存可见性。
    // 注意：当前为布尔语义——同一任务多个 group 都用 "*" 时，移除其一会清掉全部通配（已知边角，
    // 受 Tag 唯一索引 (task,group,item) 影响极少见；如需精确计数后续改 refcount）。
    private volatile bool _acceptAll;
    private bool _disposed;

    public string ChannelId { get; }
    public ChannelReader<TagValue> TagValues => _values.Reader;
    public ChannelReader<HeartBeat> Heartbeats => _heartbeats.Reader;

    private readonly ILogger? _logger;

    public OpcAeSubscriber(string channelId, OpcConnectionOptions options, ILogger? logger = null)
    {
        ChannelId = channelId;
        _options = options;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        var opcUrl = new OpcUrl(BuildOpcAeUrl());
        return Task.Run(() =>
        {
            lock (_comLock)
            {
                _server = new TsCAeServer(new ComFactory(), opcUrl);
                _server.Connect();

                var state = new TsCAeSubscriptionState
                {
                    Name = $"DcAeSubscription-{ChannelId}",
                    Active = true,
                    // BufferTime: 服务器在多长时间内最多攒一次回调（0=不缓冲；UpdateRate 概念）
                    BufferTime = (int)_options.SamplingInterval.TotalMilliseconds,
                    MaxSize = 100,
                    // KeepAlive: 在 BufferTime 长时间无事件时的心跳保活（防止"看似断了")
                    KeepAlive = (int)_options.HeartbeatInterval.TotalMilliseconds
                };
                _subscription = (TsCAeSubscription)_server.CreateSubscription(state);
                _subscription.DataChangedEvent += OnEvents;
            }
            _logger?.LogInformation("OPC AE 已连接 {Url}（{ChannelId}）", BuildOpcAeUrl(), ChannelId);
            _heartbeatTask = HeartbeatLoopAsync(_disposeCts.Token);
        }, ct);
    }

    public Task SubscribeAsync(IReadOnlyCollection<TagDescriptor> tags, CancellationToken ct = default)
    {
        // 把 Tag.Item 作为源 ID 白名单加入；"*" 表示放行全部
        foreach (var t in tags)
        {
            if (string.IsNullOrWhiteSpace(t.Item)) continue;
            if (t.Item.Trim() == "*") _acceptAll = true;
            else _allowedSources[t.Item.Trim()] = 0;
        }
        _logger?.LogInformation("OPC AE 订阅更新：acceptAll={AcceptAll}，源白名单 {Count} 项（{ChannelId}）",
            _acceptAll, _allowedSources.Count, ChannelId);
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(IReadOnlyCollection<string> tagItems, CancellationToken ct = default)
    {
        foreach (var item in tagItems)
        {
            if (item.Trim() == "*") _acceptAll = false;
            else _allowedSources.TryRemove(item.Trim(), out _);
        }
        return Task.CompletedTask;
    }

    private void OnEvents(TsCAeEventNotification[] notifications, bool refresh, bool lastRefresh)
    {
        if (notifications is null) return;
        foreach (var n in notifications)
        {
            var source = n.SourceID ?? string.Empty;
            // 没注册任何源 = 全收（兼容空配置）；否则按白名单匹配
            if (!_acceptAll && _allowedSources.Count > 0 && !_allowedSources.ContainsKey(source))
                continue;

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["source"]         = source,
                ["message"]        = n.Message,
                ["severity"]       = n.Severity,
                ["event_type"]     = n.EventType.ToString(),
                ["category"]       = n.EventCategory,
                ["condition"]      = n.ConditionName,
                ["sub_condition"]  = n.SubConditionName,
                ["change_mask"]    = n.ChangeMaskAsText,
                ["new_state"]      = n.NewStateAsText,
                ["ack_required"]   = n.AckRequired,
                ["active_time"]    = n.ActiveTime == default ? null : new DateTimeOffset(DateTime.SpecifyKind(n.ActiveTime, DateTimeKind.Utc)),
                ["cookie"]         = n.Cookie,
                ["actor_id"]       = n.ActorID,
                ["refresh"]        = refresh,
                ["last_refresh"]   = lastRefresh
            };

            var ts = n.Time == default
                ? DateTimeOffset.UtcNow
                : new DateTimeOffset(DateTime.SpecifyKind(n.Time, DateTimeKind.Utc));

            // Quality 固定 0xC0 (Good)：事件本身代表"已发生的事实"，不存在质量降级语义
            _values.Writer.TryWrite(new TagValue(source, payload, 0xC0, ts));
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // 活性门控:主动探活,不再"订阅对象还在就发"——那样死服务器也一直发心跳→徽章赖着「运行正常」→
            // 断线不可见(与 DA 同款 bug,DA 已活体复现)。AE 事件天生稀疏(没报警≠死了),更不能靠事件到达推断活性,
            // 故主动 GetServerStatus()(虽 AE 订阅已设 KeepAlive,但那只防服务器侧"看似断",客户端仍需主动探活)。
            if (IsServerOperational())
                _heartbeats.Writer.TryWrite(new HeartBeat(ChannelId, DateTimeOffset.UtcNow, "OPC AE"));
        }
    }

    // 主动探活:GetServerStatus 成功且 Operational 才算活。COM 调用进 _comLock;任何异常(断连/RPC)即判死。
    private bool IsServerOperational()
    {
        try
        {
            lock (_comLock)
            {
                return _server?.GetServerStatus()?.ServerState == OpcServerState.Operational;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "OPC AE 探活失败(疑似断连)（{ChannelId}）", ChannelId);
            return false;
        }
    }

    private string BuildOpcAeUrl()
    {
        var uri = _options.ServerUri?.Trim() ?? string.Empty;
        if (uri.StartsWith("opcae://", StringComparison.OrdinalIgnoreCase))
            return uri;

        var progId = _options.ServerProgId
            ?? throw new InvalidOperationException(
                "OPC AE 需要 ServerProgId（Server ProgID，如 SampleCompany.AeSample）");
        var host = string.IsNullOrWhiteSpace(uri) ? "localhost" : uri;

        var clsid = _options.ServerClsid?.Trim();
        if (!string.IsNullOrEmpty(clsid))
        {
            if (!clsid.StartsWith("{")) clsid = "{" + clsid + "}";
            return $"opcae://{host}/{progId}/{clsid}";
        }
        return $"opcae://{host}/{progId}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();

        try { if (_heartbeatTask is not null) await _heartbeatTask.ConfigureAwait(false); } catch { }

        lock (_comLock)
        {
            try { if (_subscription is not null) _subscription.DataChangedEvent -= OnEvents; } catch { }
            try { _subscription?.Dispose(); } catch { }
            try { _server?.Disconnect(); } catch { }
            try { _server?.Dispose(); } catch { }
            _subscription = null;
            _server = null;
            _allowedSources.Clear();
        }

        _values.Writer.TryComplete();
        _heartbeats.Writer.TryComplete();
        _disposeCts.Dispose();
    }
}
