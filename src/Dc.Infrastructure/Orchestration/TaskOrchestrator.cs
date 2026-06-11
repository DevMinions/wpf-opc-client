using System.Collections.Concurrent;
using Dc.Infrastructure.Messaging;
using Dc.Opc.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dc.Infrastructure.Orchestration;

public sealed class TaskOrchestrator : IAsyncDisposable
{
    private sealed class TaskRuntime
    {
        public required string TaskId { get; init; }
        public required TaskStartRequest Request { get; set; }
        public required IOpcSubscriber Subscriber { get; set; }
        public required IPublisher Publisher { get; init; }
        public required CancellationTokenSource Cts { get; set; }
        public required Task PipelineTask { get; set; }
        public required Dictionary<string, TagDescriptor> Tags { get; init; }
        public DateTimeOffset LastHeartbeat { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastValueAt { get; set; }
        public long ValueCount;
        public long PublishErrorCount;
        public int RestartCount { get; set; }
        public ConnectionState State { get; set; } = ConnectionState.Connecting;
        public int ConsecutiveStaleRestarts { get; set; }
        public DateTimeOffset? LastRestartAt { get; set; }
    }

    private readonly IReadOnlyDictionary<OpcProtocol, IOpcSubscriberFactory> _factories;
    private readonly IPublisherFactory _publisherFactory;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<TaskOrchestrator>? _logger;
    private readonly ConcurrentDictionary<string, TaskRuntime> _running = new();
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly CancellationTokenSource _hostCts = new();
    private readonly object _stateLock = new();
    private readonly Task _watchdogTask;
    private bool _disposed;

    private void SetState(TaskRuntime rt, ConnectionState s)
    {
        lock (_stateLock) rt.State = s;
    }

    public TaskOrchestrator(
        IEnumerable<IOpcSubscriberFactory> factories,
        IPublisherFactory publisherFactory,
        OrchestratorOptions? options = null,
        ILogger<TaskOrchestrator>? logger = null)
    {
        _factories = factories.ToDictionary(f => f.Protocol);
        _publisherFactory = publisherFactory;
        _options = options ?? new OrchestratorOptions();
        _logger = logger;
        _watchdogTask = Task.Run(WatchdogLoopAsync);
    }

    public IReadOnlyCollection<string> RunningTaskIds => _running.Keys.ToArray();

    public event Action<string, TagValue>? TagValueReceived;

    /// <summary>调试用合成注入：直接触发 TagValueReceived，与真值同路径。仅门控后被调用。</summary>
    internal void InjectSynthetic(string taskId, TagValue v) => TagValueReceived?.Invoke(taskId, v);

    public IReadOnlyList<TaskDiagnostics> GetDiagnostics()
    {
        return _running.Values.Select(rt =>
        {
            // 批量/异步 Publisher 的发送失败发生在后台，PublishAsync 不抛 → 折入这里，
            // 否则 broker 宕机时 PublishErrorCount 恒 0、Dashboard 假健康。
            var health = rt.Publisher as IPublisherHealth;
            var bgErrors = health?.SendErrorCount ?? 0;
            return new TaskDiagnostics(
                rt.TaskId,
                rt.StartedAt,
                rt.LastValueAt,
                rt.LastHeartbeat,
                Interlocked.Read(ref rt.ValueCount),
                Interlocked.Read(ref rt.PublishErrorCount) + bgErrors,
                rt.RestartCount,
                rt.Tags.Count,
                health?.PendingBytes ?? 0,
                health?.DroppedFrameCount ?? 0,
                rt.State);
        }).ToArray();
    }

    public async Task StartAsync(TaskStartRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_factories.TryGetValue(request.Protocol, out var factory))
        {
            var hint = request.Protocol switch
            {
                OpcProtocol.Da => "DA 需 Windows + COM SDK（TitaniumAS 或 Technosoftware），当前构建未启用。",
                OpcProtocol.Ae => "AE 需 Windows + COM SDK，当前构建未启用。",
                _ => "请确认协议工厂已在 DI 中注册。"
            };
            throw new InvalidOperationException($"协议 {request.Protocol} 的订阅器未注册。{hint}");
        }

        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopUnlockedAsync(request.TaskId).ConfigureAwait(false);
            await StartUnlockedAsync(request, factory, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task StopAsync(string taskId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopUnlockedAsync(taskId).ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<bool> AddTagsAsync(string taskId, IReadOnlyCollection<TagDescriptor> tags, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_running.TryGetValue(taskId, out var rt)) return false;
            var added = tags.Where(t => !rt.Tags.ContainsKey(t.Item)).ToArray();
            if (added.Length == 0) return true;
            await rt.Subscriber.SubscribeAsync(added, ct).ConfigureAwait(false);
            foreach (var t in added) rt.Tags[t.Item] = t;
            return true;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<bool> RemoveTagsAsync(string taskId, IReadOnlyCollection<string> tagItems, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_running.TryGetValue(taskId, out var rt)) return false;
            var present = tagItems.Where(rt.Tags.ContainsKey).ToArray();
            if (present.Length == 0) return true;
            await rt.Subscriber.UnsubscribeAsync(present, ct).ConfigureAwait(false);
            foreach (var item in present) rt.Tags.Remove(item);
            return true;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task StartUnlockedAsync(TaskStartRequest request, IOpcSubscriberFactory factory, CancellationToken ct)
    {
        var subscriber = factory.Create(request.TaskId, request.OpcOptions);
        var publisher = _publisherFactory.Create(request.PublisherAddress);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);

        var runtime = new TaskRuntime
        {
            TaskId = request.TaskId,
            Request = request,
            Subscriber = subscriber,
            Publisher = publisher,
            Cts = cts,
            PipelineTask = Task.CompletedTask,
            Tags = request.Tags.ToDictionary(t => t.Item),
            State = ConnectionState.Connecting
        };
        _running[request.TaskId] = runtime; // 先入运行集 → GetDiagnostics 立即可见「连接中」

        try
        {
            await subscriber.ConnectAsync(ct).ConfigureAwait(false);
            await subscriber.SubscribeAsync(request.Tags, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "任务 {TaskId} ({Protocol}) 连接/订阅失败：{Message}",
                request.TaskId, request.Protocol, ex.Message);
            _running.TryRemove(request.TaskId, out _);
            await SafeDisposeAsync(subscriber).ConfigureAwait(false);
            await SafeDisposeAsync(publisher).ConfigureAwait(false);
            cts.Dispose();
            throw;
        }

        runtime.LastHeartbeat = DateTimeOffset.UtcNow;
        SetState(runtime, ConnectionState.Running);
        runtime.PipelineTask = Task.Run(() => RunPipelineAsync(runtime, cts.Token));
        _logger?.LogInformation("任务 {TaskId} ({Protocol}) 已启动，订阅 {TagCount} 个 tag → {Publisher}",
            request.TaskId, request.Protocol, request.Tags.Count, request.PublisherAddress);
    }

    private async Task StopUnlockedAsync(string taskId)
    {
        if (!_running.TryRemove(taskId, out var rt)) return;
        _logger?.LogInformation("任务 {TaskId} ({Protocol}) 停止中", taskId, rt.Request.Protocol);

        // 优雅停止：先 Dispose 订阅器（其 DisposeAsync 会 TryComplete TagValues/Heartbeats writer），
        // 让 RunPipelineAsync 的 ReadAllAsync 把通道里残余值 drain 完并发出后自然结束，避免丢数据。
        await SafeDisposeAsync(rt.Subscriber).ConfigureAwait(false);

        // 给 pipeline 一个上限把残余值发完；超时（publisher 卡死等）则强制取消兜底。
        try
        {
            await rt.PipelineTask.WaitAsync(_options.StopDrainTimeout).ConfigureAwait(false);
        }
        catch
        {
            rt.Cts.Cancel();
            try { await rt.PipelineTask.ConfigureAwait(false); } catch { }
        }

        await SafeDisposeAsync(rt.Publisher).ConfigureAwait(false);
        rt.Cts.Dispose();
    }

    private async Task RunPipelineAsync(TaskRuntime rt, CancellationToken ct)
    {
        var valuesTask = ConsumeAsync(rt.Subscriber.TagValues, async v =>
        {
            Interlocked.Increment(ref rt.ValueCount);
            rt.LastValueAt = DateTimeOffset.UtcNow;
            TagValueReceived?.Invoke(rt.TaskId, v);
            try { await rt.Publisher.PublishAsync(v, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; } // 正常停止/重启，不计为发布错误
            catch { Interlocked.Increment(ref rt.PublishErrorCount); }
        }, ct);

        var heartTask = ConsumeAsync(rt.Subscriber.Heartbeats, h =>
        {
            rt.LastHeartbeat = h.Time;
            // 心跳到达 → 视为恢复：清零连续陈旧计数、状态回 Running。
            // 与重启路径（SetState）共用 _stateLock 串行，避免状态/计数写竞态。
            lock (_stateLock)
            {
                if (rt.ConsecutiveStaleRestarts > 0
                    || rt.State is ConnectionState.Faulted or ConnectionState.Restarting)
                {
                    rt.ConsecutiveStaleRestarts = 0;
                    rt.State = ConnectionState.Running;
                }
            }
            return ValueTask.CompletedTask;
        }, ct);

        try { await Task.WhenAll(valuesTask, heartTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static async Task ConsumeAsync<T>(System.Threading.Channels.ChannelReader<T> reader, Func<T, ValueTask> handler, CancellationToken ct)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(ct).ConfigureAwait(false))
                await handler(item).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task WatchdogLoopAsync()
    {
        while (!_hostCts.IsCancellationRequested)
        {
            try { await Task.Delay(_options.WatchdogInterval, _hostCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var now = DateTimeOffset.UtcNow;
            // 仅取候选 id（无锁）；真正的 staleness 判定与重启在锁内重新校验，避免按陈旧快照
            // 复活用户已 StopAsync 的任务，并让 RestartCount 与重启动作原子。
            var candidates = _running
                .Where(kv => kv.Value.State != ConnectionState.Connecting
                          && now - kv.Value.LastHeartbeat > _options.HeartbeatTimeout)
                .Select(kv => kv.Key)
                .ToArray();

            foreach (var taskId in candidates)
            {
                try { await RestartIfStaleAsync(taskId).ConfigureAwait(false); }
                catch { /* swallow — next watchdog tick retries */ }
            }
        }
    }

    private async Task RestartIfStaleAsync(string taskId)
    {
        await _mutationLock.WaitAsync(_hostCts.Token).ConfigureAwait(false);
        try
        {
            // 锁内重新校验：任务可能已被用户 StopAsync 移除，或心跳已恢复 → 不重启（不复活）。
            if (!_running.TryGetValue(taskId, out var rt)) return;
            if (DateTimeOffset.UtcNow - rt.LastHeartbeat <= _options.HeartbeatTimeout) return;
            if (!_factories.TryGetValue(rt.Request.Protocol, out var factory)) return;

            var req = rt.Request;
            // 原地重绑：rt 全程留在 _running，重连失败也不移除 → GetDiagnostics 持续发该行，
            // 修复「重连失败任务凭空消失」缺陷，并让 Restarting/Faulted 状态可观测。
            // 状态与连续陈旧计数一并在 _stateLock 内原子完成：与心跳恢复路径（同锁写 =0）
            // 互斥，避免「++（重启开始）」与并发心跳回调 =0 的竞态。
            lock (_stateLock)
            {
                rt.State = ConnectionState.Restarting;
                rt.ConsecutiveStaleRestarts++;
            }
            rt.LastRestartAt = DateTimeOffset.UtcNow;   // 心跳路径不写，无需进锁
            rt.RestartCount++;                          // 同上
            _logger?.LogWarning("任务 {TaskId} ({Protocol}) 心跳超时（>{Timeout}），看门狗原地重连（第 {Count} 次）",
                taskId, req.Protocol, _options.HeartbeatTimeout, rt.RestartCount);

            // 拆旧管道（rt 保留在 _running）
            rt.Cts.Cancel();
            try { await rt.PipelineTask.WaitAsync(_options.StopDrainTimeout).ConfigureAwait(false); } catch { /* 取消/超时正常 */ }
            await SafeDisposeAsync(rt.Subscriber).ConfigureAwait(false);
            try { rt.Cts.Dispose(); } catch { }

            // 重连
            var subscriber = factory.Create(req.TaskId, req.OpcOptions);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);
            try
            {
                await subscriber.ConnectAsync(_hostCts.Token).ConfigureAwait(false);
                await subscriber.SubscribeAsync(req.Tags, _hostCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "任务 {TaskId} 重连失败 → 标记故障，留在运行集等下次看门狗重试", taskId);
                await SafeDisposeAsync(subscriber).ConfigureAwait(false);
                cts.Dispose();
                // 占位 Cts/PipelineTask 供后续 Stop 安全；推回心跳让下次 tick 再试；置 Faulted。
                rt.PipelineTask = Task.CompletedTask;
                rt.Cts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);
                SetState(rt, ConnectionState.Faulted);
                rt.LastHeartbeat = DateTimeOffset.UtcNow - _options.HeartbeatTimeout - TimeSpan.FromSeconds(1);
                return;
            }

            // 重绑成功
            rt.Subscriber = subscriber;
            rt.Cts = cts;
            rt.LastHeartbeat = DateTimeOffset.UtcNow;
            // 在同一 _stateLock 内读 ConsecutiveStaleRestarts + 写 State，保持与心跳路径一致。
            lock (_stateLock)
            {
                rt.State = rt.ConsecutiveStaleRestarts >= _options.FaultThreshold
                    ? ConnectionState.Faulted   // 重连上了但反复超时 → 仍标故障，待心跳确认恢复
                    : ConnectionState.Running;
            }
            rt.PipelineTask = Task.Run(() => RunPipelineAsync(rt, cts.Token));
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private static async ValueTask SafeDisposeAsync(IAsyncDisposable d)
    {
        try { await d.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _hostCts.Cancel();
        try { await _watchdogTask.ConfigureAwait(false); } catch { }

        foreach (var taskId in _running.Keys.ToArray())
            await StopUnlockedAsync(taskId).ConfigureAwait(false);

        _mutationLock.Dispose();
        _hostCts.Dispose();
    }
}
