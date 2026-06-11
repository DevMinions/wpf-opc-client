using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.ViewModels.Workspace;
using Dc.Infrastructure.Orchestration;
using Dc.Opc.Abstractions;

namespace Dc.App.ViewModels;

public partial class LiveDataViewModel : ObservableObject, IDisposable, IEmbeddableLivePanel
{
    private readonly TaskOrchestrator _orchestrator;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, LiveDataRowViewModel> _rowIndex = new();
    private readonly ConcurrentQueue<(string TaskId, TagValue Value)> _buffer = new();
    private readonly LiveValueCoalescer<(string TaskId, TagValue Value)> _coalescer = new();
    private readonly DispatcherTimer _batchTimer;
    private bool _disposed;

    /// <summary>最大保留行数，超限删除最旧行。</summary>
    private const int MaxRows = 5000;

    /// <summary>批量应用间隔（毫秒）。</summary>
    private const int BatchIntervalMs = 100;

    /// <summary>搜索框防抖间隔（毫秒）：静默该时长后才刷新一次视图。</summary>
    private const int SearchDebounceMs = 250;

    private DispatcherTimer? _searchDebounce;

    internal int RefreshCountForTest { get; private set; }

    internal void ResetRefreshCountForTest() => RefreshCountForTest = 0;

    private void DoRefresh()
    {
        RowsView.Refresh();
        RefreshCountForTest++;
    }

    // 测试用：模拟 250ms 静默后的 Tick（直接触发刷新，绕过真实计时，避免 flaky）
    internal void DebounceTickForTest()
    {
        _searchDebounce?.Stop();
        DoRefresh();
    }

    private readonly Action<string>? _navigate;

    [ObservableProperty] private string _title = "实时数据";
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private string? _taskFilter;
    [ObservableProperty] private string _searchText = string.Empty; // 按 Item 子串过滤
    [ObservableProperty] private int _rowCount;                     // 当前行数（工具栏显示）
    [ObservableProperty] private double _updatesPerSecond;          // 更新速率 /s（工具栏显示）
    [ObservableProperty] private bool _showNavigateCta;

    public string? NavigateCtaText => ShowNavigateCta ? "去采集任务" : null;

    partial void OnShowNavigateCtaChanged(bool value) => OnPropertyChanged(nameof(NavigateCtaText));

    private long _updatesAccum;
    private DateTimeOffset _lastRateAt = DateTimeOffset.UtcNow;

    // flush 统计：合并比累计 + flush 耗时环形缓冲（p50/p95）
    private long _totalCoalesceIn;
    private long _totalCoalesceOut;
    private readonly double[] _flushMsRing = new double[128];
    private int _flushMsCount;
    private int _flushMsHead;

    // flush 统计快照：UI 线程算好后原子发布，供 /metrics 后台线程无锁读取。
    private volatile LiveFlushStats _statsSnapshot = new(0, 0, 0, 0, 0);

    public ObservableCollection<LiveDataRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> AvailableTaskIds { get; } = new();
    public ICollectionView RowsView { get; }

    private bool _running;

    public LiveDataViewModel(TaskOrchestrator orchestrator, Dispatcher dispatcher,
        Action<string>? navigate = null, bool showNavigateCta = false)
    {
        _orchestrator = orchestrator;
        _dispatcher = dispatcher;
        _navigate = navigate;
        ShowNavigateCta = showNavigateCta;

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = item =>
        {
            if (item is not LiveDataRowViewModel r) return false;
            if (!string.IsNullOrEmpty(TaskFilter) && r.TaskId != TaskFilter) return false;
            if (!string.IsNullOrWhiteSpace(SearchText) &&
                r.Item.IndexOf(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        };

        // 批量定时器：每 100ms 将缓冲区所有值一次性应用到 UI（仅在页面可见时运行，见 Start/Stop）
        _batchTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(BatchIntervalMs)
        };
        _batchTimer.Tick += (_, _) => FlushBuffer();

        // 搜索防抖定时器：连续输入只在静默 250ms 后刷新一次视图（5000 行打字不顿）
        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(SearchDebounceMs)
        };
        _searchDebounce.Tick += (_, _) => { _searchDebounce!.Stop(); DoRefresh(); };
    }

    /// <summary>页面可见时调用（view Loaded）：订阅数据 + 启动批处理。幂等。</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _orchestrator.TagValueReceived += OnTagValueReceived;
        _batchTimer.Start();
    }

    /// <summary>页面隐藏时调用（view Unloaded）：退订 + 停批处理，避免不可见时空跑数据洪流。幂等。</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _orchestrator.TagValueReceived -= OnTagValueReceived;
        _batchTimer.Stop();
        _searchDebounce?.Stop();
    }

    private void OnTagValueReceived(string taskId, TagValue v)
    {
        if (Paused) return;
        // 入缓冲区（线程安全，无 Dispatcher 调度开销）
        _buffer.Enqueue((taskId, v));
    }

    /// <summary>将缓冲区所有值批量应用到 UI 行。</summary>
    private void FlushBuffer()
    {
        var sw = Stopwatch.StartNew();

        _coalescer.Coalesce(
            tryDequeue: () => _buffer.TryDequeue(out var it)
                ? (true, $"{it.TaskId}::{it.Value.Item}", it)
                : (false, string.Empty, default),
            apply: (_, it, n) => Apply(it.TaskId, it.Value, n));

        var rawCount = _coalescer.LastInputCount; // 原始流入条数（速率用）

        // 超限淘汰：最旧恒为 Rows[0]（只末尾 Add、只最旧端淘汰），key 由行重建 → 无线性查找
        while (Rows.Count > MaxRows)
        {
            var victim = Rows[0];
            Rows.RemoveAt(0);
            _rowIndex.Remove($"{victim.TaskId}::{victim.Item}");
        }

        sw.Stop();

        // flush 统计：仅在本批有工作时记录（空 flush 不污染 p50/p95）
        if (rawCount > 0)
        {
            _totalCoalesceIn += _coalescer.LastInputCount;
            _totalCoalesceOut += _coalescer.LastOutputCount;
            _flushMsRing[_flushMsHead] = sw.Elapsed.TotalMilliseconds;
            _flushMsHead = (_flushMsHead + 1) % _flushMsRing.Length;
            _flushMsCount++;
        }

        if (rawCount > 0 || RowCount != Rows.Count) RowCount = Rows.Count;

        // 更新速率 /s：累计本次应用数，每 ≥1s 折算一次
        _updatesAccum += rawCount;
        var elapsed = (DateTimeOffset.UtcNow - _lastRateAt).TotalSeconds;
        if (elapsed >= 1.0)
        {
            UpdatesPerSecond = _updatesAccum / elapsed;
            _updatesAccum = 0;
            _lastRateAt = DateTimeOffset.UtcNow;
        }

        // UI 线程算好不可变快照后原子发布，供 /metrics 后台线程无锁读取。
        _statsSnapshot = ComputeFlushStats();
    }

    internal void EnqueueForTest(string taskId, TagValue v) => _buffer.Enqueue((taskId, v));

    internal void FlushForTest() => FlushBuffer();

    // 后台线程（/metrics）安全：返回最近一次 flush 在 UI 线程算好的不可变快照（volatile 原子读）。
    public LiveFlushStats GetFlushStats() => _statsSnapshot;

    // 在 UI 线程（FlushBuffer）调用：从当前 ring/累计器/Rows 算快照。
    private LiveFlushStats ComputeFlushStats()
    {
        double p50, p95;
        var n = Math.Min(_flushMsCount, _flushMsRing.Length);
        if (n == 0) { p50 = 0; p95 = 0; }
        else
        {
            var copy = new double[n];
            Array.Copy(_flushMsRing, copy, n);
            Array.Sort(copy);
            p50 = copy[(int)(n * 0.50)];
            p95 = copy[Math.Min(n - 1, (int)(n * 0.95))];
        }
        var ratio = _totalCoalesceOut > 0 ? (double)_totalCoalesceIn / _totalCoalesceOut : 0;
        return new LiveFlushStats(p50, p95, ratio, Rows.Count, UpdatesPerSecond);
    }

    private void Apply(string taskId, TagValue v, int rawCount)
    {
        if (!AvailableTaskIds.Contains(taskId)) AvailableTaskIds.Add(taskId);

        var key = $"{taskId}::{v.Item}";
        if (!_rowIndex.TryGetValue(key, out var row))
        {
            row = new LiveDataRowViewModel { TaskId = taskId, Item = v.Item };
            _rowIndex[key] = row;
            Rows.Add(row);
        }
        row.Apply(v, rawCount);
    }

    partial void OnTaskFilterChanged(string? value) => DoRefresh(); // 下拉非高频，保持即时刷新

    partial void OnSearchTextChanged(string value)
    {
        // 高频输入：重置防抖窗口，静默 250ms 后由 Tick 触发一次刷新
        _searchDebounce?.Stop();
        _searchDebounce?.Start();
    }

    [RelayCommand]
    private void NavigateToWorkspace() => _navigate?.Invoke("workspace");

    [RelayCommand]
    private void Clear()
    {
        _searchDebounce?.Stop(); // 清空后避免挂起的防抖回调刷新已清集合
        _rowIndex.Clear();
        Rows.Clear();
        AvailableTaskIds.Clear();
        RowCount = 0;

        // 重置 flush 统计累计器，避免清空后陈旧统计（ring 数组无需清零，count=0 即 n=0）
        _totalCoalesceIn = 0;
        _totalCoalesceOut = 0;
        _flushMsCount = 0;
        _flushMsHead = 0;
    }

    [RelayCommand]
    private void ClearFilter() => TaskFilter = null;

    [RelayCommand]
    private void TogglePause() => Paused = !Paused;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _searchDebounce?.Stop();
    }
}
