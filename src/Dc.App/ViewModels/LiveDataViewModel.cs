using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly DispatcherTimer _batchTimer;
    private bool _disposed;

    /// <summary>最大保留行数，超限删除最旧行。</summary>
    private const int MaxRows = 5000;

    /// <summary>批量应用间隔（毫秒）。</summary>
    private const int BatchIntervalMs = 100;

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
        var count = 0;
        while (_buffer.TryDequeue(out var item))
        {
            Apply(item.TaskId, item.Value);
            count++;
        }

        // 超限时移除最旧行
        while (_rowIndex.Count > MaxRows)
        {
            // 找到最旧的 key（按插入顺序，Dictionary 在 .NET 8 保持插入序）
            var oldestKey = _rowIndex.Keys.First();
            var oldestRow = _rowIndex[oldestKey];
            _rowIndex.Remove(oldestKey);
            Rows.Remove(oldestRow);
        }

        if (count > 0 || RowCount != Rows.Count) RowCount = Rows.Count;

        // 更新速率 /s：累计本次应用数，每 ≥1s 折算一次
        _updatesAccum += count;
        var elapsed = (DateTimeOffset.UtcNow - _lastRateAt).TotalSeconds;
        if (elapsed >= 1.0)
        {
            UpdatesPerSecond = _updatesAccum / elapsed;
            _updatesAccum = 0;
            _lastRateAt = DateTimeOffset.UtcNow;
        }
    }

    private void Apply(string taskId, TagValue v)
    {
        if (!AvailableTaskIds.Contains(taskId)) AvailableTaskIds.Add(taskId);

        var key = $"{taskId}::{v.Item}";
        if (!_rowIndex.TryGetValue(key, out var row))
        {
            row = new LiveDataRowViewModel { TaskId = taskId, Item = v.Item };
            _rowIndex[key] = row;
            Rows.Add(row);
        }
        row.Apply(v);
    }

    partial void OnTaskFilterChanged(string? value) => RowsView.Refresh();

    partial void OnSearchTextChanged(string value) => RowsView.Refresh();

    [RelayCommand]
    private void NavigateToWorkspace() => _navigate?.Invoke("workspace");

    [RelayCommand]
    private void Clear()
    {
        _rowIndex.Clear();
        Rows.Clear();
        AvailableTaskIds.Clear();
        RowCount = 0;
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
    }
}
