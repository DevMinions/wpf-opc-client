using System.Collections.Concurrent;
using System.Threading.Channels;
using Dc.Opc.Abstractions;
using Microsoft.Extensions.Logging;
using Technosoftware.DaAeHdaClient;
using Technosoftware.DaAeHdaClient.Da;
using ComFactory = Technosoftware.DaAeHdaClient.Com.Factory;

namespace Dc.Opc.Da;

// OPC DA 订阅器实现。参考 opcda/article.md 第 84~161 行的模式：
//   1. 所有 COM 调用串行在 _comLock 内（DA COM 单线程公寓敏感）
//   2. _subscribedTags 字典对 AddItems 幂等（同 item 重复订阅会报错）
//   3. ClientHandle 直接用 ItemName，DataChangedEvent 回调里 valueResult.ItemName 即可定位
//   4. 质量码按位运算判断：Good=0xC0、Uncertain=0x40、Bad=0x00（与 IsGood 一致）
public sealed class OpcDaSubscriber : IOpcSubscriber
{
    private readonly Channel<TagValue> _values = Channel.CreateUnbounded<TagValue>();
    private readonly Channel<HeartBeat> _heartbeats = Channel.CreateUnbounded<HeartBeat>();
    private readonly OpcConnectionOptions _options;
    private readonly ILogger? _logger;
    private readonly object _comLock = new();
    private readonly ConcurrentDictionary<string, byte> _subscribedTags = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private TsCDaServer? _server;
    private TsCDaSubscription? _subscription;
    private Task? _heartbeatTask;
    private bool _disposed;

    public string ChannelId { get; }
    public ChannelReader<TagValue> TagValues => _values.Reader;
    public ChannelReader<HeartBeat> Heartbeats => _heartbeats.Reader;

    public OpcDaSubscriber(string channelId, OpcConnectionOptions options, ILogger? logger = null)
    {
        ChannelId = channelId;
        _options = options;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        // ⚠ vendor 的 OpcServer.Connect(string) 有 bug（Factory=null 后 NRE）。
        // 用 ctor 2 注入 Com.Factory + OpcUrl，再无参 Connect()。
        var opcUrl = new OpcUrl(BuildOpcDaUrl());
        lock (_comLock)
        {
            _server = new TsCDaServer(new ComFactory(), opcUrl);
            _server.Connect();

            var state = new TsCDaSubscriptionState
            {
                Name = $"DcDaSubscription-{ChannelId}",
                Active = true,
                UpdateRate = (int)_options.SamplingInterval.TotalMilliseconds,
                Deadband = _options.DeadbandPercent
            };
            _subscription = (TsCDaSubscription)_server.CreateSubscription(state);
            _subscription.DataChangedEvent += OnDataChanged;
        }
        _logger?.LogInformation("OPC DA 已连接 {Url}（{ChannelId}）", BuildOpcDaUrl(), ChannelId);
        _heartbeatTask = HeartbeatLoopAsync(_disposeCts.Token);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(IReadOnlyCollection<TagDescriptor> tags, CancellationToken ct = default)
    {
        if (_subscription is null) throw new InvalidOperationException("ConnectAsync 必须先调用");

        lock (_comLock)
        {
            var pending = tags
                .Where(t => !_subscribedTags.ContainsKey(t.Item))
                .Select(t => new TsCDaItem
                {
                    ItemName = t.Item,
                    ClientHandle = t.Item, // 用 ItemName 作 ClientHandle，DataChange 时 valueResult.ItemName 即可定位
                    Active = true,
                    ActiveSpecified = true
                })
                .ToArray();

            if (pending.Length == 0) return Task.CompletedTask;

            var results = _subscription.AddItems(pending);
            int ok = 0, failed = 0;
            foreach (var added in results)
            {
                if (added.Result.IsSuccess()) { _subscribedTags[added.ItemName] = 0; ok++; }
                else failed++;
            }
            _logger?.LogInformation("OPC DA 新增监控项 成功 {Ok}、失败 {Failed}（{ChannelId}）", ok, failed, ChannelId);
            if (failed > 0)
                _logger?.LogWarning("OPC DA 有 {Failed} 个监控项 AddItems 失败（{ChannelId}）", failed, ChannelId);
        }
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(IReadOnlyCollection<string> tagItems, CancellationToken ct = default)
    {
        // v1: 标记移除（_subscribedTags 删条目），不调底层 RemoveItems。
        // 理由：RemoveItems 需要 ServerHandle，要存 AddItems 返回的 result.ServerHandle，
        // 增加内存结构和并发同步复杂度。当前热卸载场景少，先用"再连一次"兜底。
        // 若使用方频繁热卸载，再补 RemoveItems 实现。
        foreach (var item in tagItems) _subscribedTags.TryRemove(item, out _);
        _logger?.LogInformation("OPC DA 移除监控项 {Count}（{ChannelId}）", tagItems.Count, ChannelId);
        return Task.CompletedTask;
    }

    private void OnDataChanged(object subscriptionHandle, object requestHandle, TsCDaItemValueResult[] values)
    {
        foreach (var v in values)
        {
            if (v.Result.IsError()) continue;
            // 热卸载（UnsubscribeAsync）只删 _subscribedTags、未调底层 RemoveItems，
            // server 仍会推已移除的 item；这里按成员过滤，确保被移除的 tag 立即停止下发。
            if (!_subscribedTags.ContainsKey(v.ItemName)) continue;

            ushort quality = MapQuality(v.Quality);
            DateTimeOffset ts = v.Timestamp == default
                ? DateTimeOffset.UtcNow
                : new DateTimeOffset(DateTime.SpecifyKind(v.Timestamp, DateTimeKind.Utc));

            _values.Writer.TryWrite(new TagValue(v.ItemName, v.Value, quality, ts));
        }
    }

    private static ushort MapQuality(TsCDaQuality q)
    {
        // vendor 枚举只列了 Good + 各种 Bad*，Uncertain 子状态被合到原始 byte 里。
        // 把 QualityBits 转回原始 byte（int 强转），TagValue.IsGood/Uncertain 由 0xC0/0x40 位运算判断。
        return (ushort)(int)q.QualityBits;
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // 简易心跳：订阅对象还在就发；要深度探活可改成 _server.GetStatus() 调用
            if (_subscription is not null)
                _heartbeats.Writer.TryWrite(new HeartBeat(ChannelId, DateTimeOffset.UtcNow, "OPC DA"));
        }
    }

    private string BuildOpcDaUrl()
    {
        var uri = _options.ServerUri?.Trim() ?? string.Empty;
        if (uri.StartsWith("opcda://", StringComparison.OrdinalIgnoreCase))
            return uri;

        var progId = _options.ServerProgId
            ?? throw new InvalidOperationException(
                "OPC DA 需要 ServerProgId（Server ProgID，如 Matrikon.OPC.Simulation.1）");
        var host = string.IsNullOrWhiteSpace(uri) ? "localhost" : uri;

        var clsid = _options.ServerClsid?.Trim();
        if (!string.IsNullOrEmpty(clsid))
        {
            if (!clsid.StartsWith("{")) clsid = "{" + clsid + "}";
            return $"opcda://{host}/{progId}/{clsid}";
        }
        return $"opcda://{host}/{progId}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();

        try { if (_heartbeatTask is not null) await _heartbeatTask.ConfigureAwait(false); } catch { }

        lock (_comLock)
        {
            try { if (_subscription is not null) _subscription.DataChangedEvent -= OnDataChanged; } catch { }
            try { _subscription?.Dispose(); } catch { }
            try { _server?.Disconnect(); } catch { }
            try { _server?.Dispose(); } catch { }
            _subscription = null;
            _server = null;
            _subscribedTags.Clear();
        }

        _values.Writer.TryComplete();
        _heartbeats.Writer.TryComplete();
        _disposeCts.Dispose();
    }
}
