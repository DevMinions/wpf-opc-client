using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dc.App.ViewModels;

/// <summary>日志级别</summary>
public enum LogLevel
{
    Info,
    Warning,
    Error,
    Ok
}

/// <summary>单行日志条目</summary>
public class LogEntry
{
    public string Timestamp { get; init; } = "";
    public LogLevel Level { get; init; }
    public string LevelTag { get; init; } = "INF";
    public string Message { get; init; } = "";
    public string RawLine { get; init; } = "";
}

public partial class LogsViewModel : ObservableObject, IDisposable
{
    private const int MaxLines = 500;
    private const long TailBytes = 256 * 1024; // 只读文件尾部，避免日志增长后每次读整文件
    private readonly System.Threading.Timer _autoRefreshTimer;
    private long _lastFileLength = -1;          // 上次读取的文件长度；未变则跳过重建
    private string _lastFilePath = string.Empty;

    [ObservableProperty] private string _title = "运行日志";
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private string _logFilePath = string.Empty;
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private int _minLogLevelIndex;

    /// <summary>结构化日志行（用于着色渲染）</summary>
    public ICollectionView LogEntriesView { get; }

    private readonly List<LogEntry> _allEntries = [];

    private bool _running;

    public LogsViewModel()
    {
        ResolveLogFilePath();
        // 定时器创建为不触发（Infinite）；仅页面可见时 Start 才开始每 2s 读文件，
        // 否则单例 VM 会全程每 2s 读日志文件做无谓 I/O。
        _autoRefreshTimer = new System.Threading.Timer(_ =>
        {
            if (AutoRefresh) _ = ReloadAsync();
        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        LogEntriesView = CollectionViewSource.GetDefaultView(_allEntries);
    }

    /// <summary>页面可见时（view Loaded）：立即加载 + 启动 2s 轮询。幂等。</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _ = ReloadAsync();
        _autoRefreshTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>页面隐藏时（view Unloaded）：停轮询。幂等。</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _autoRefreshTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    partial void OnMinLogLevelIndexChanged(int value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        LogEntriesView.Filter = MinLogLevelIndex switch
        {
            1 => o => o is LogEntry e && e.Level >= LogLevel.Info,
            2 => o => o is LogEntry e && e.Level >= LogLevel.Warning,
            _ => null
        };
        LogEntriesView.Refresh();
    }

    private void ResolveLogFilePath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        var preferred = Path.Combine(dir, $"dc-{DateTime.Now:yyyyMMdd}.log");
        if (File.Exists(preferred)) { LogFilePath = preferred; return; }

        if (Directory.Exists(dir))
        {
            var latest = new DirectoryInfo(dir).GetFiles("dc-*.log")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            LogFilePath = latest?.FullName ?? string.Empty;
        }
        else
        {
            LogFilePath = string.Empty;
        }
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        ResolveLogFilePath();
        if (string.IsNullOrEmpty(LogFilePath) || !File.Exists(LogFilePath))
        {
            Content = "(日志文件尚未生成)";
            _allEntries.Clear();
            LogEntriesView.Refresh();
            return;
        }
        try
        {
            await using var fs = new FileStream(LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // 文件长度未变且仍是同一文件 → 内容没新增，跳过重建（避免每 2s 无谓刷新造成卡顿）
            if (fs.Length == _lastFileLength && LogFilePath == _lastFilePath)
                return;

            // 只读尾部 TailBytes：日志按天滚动可能很大，整文件读会随增长越来越慢
            long start = Math.Max(0, fs.Length - TailBytes);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var text = await sr.ReadToEndAsync();
            if (start > 0)
            {
                // 丢弃因截断产生的不完整首行
                var nl = text.IndexOf('\n');
                if (nl >= 0) text = text[(nl + 1)..];
            }

            var lines = text.Split('\n');
            var tail = lines.Length <= MaxLines ? lines : lines[^MaxLines..];

            _allEntries.Clear();
            foreach (var raw in tail)
            {
                var trimmed = raw.TrimEnd('\r');
                if (string.IsNullOrEmpty(trimmed)) continue;
                _allEntries.Add(ParseLine(trimmed));
            }
            Content = string.Join('\n', tail);
            LogEntriesView.Refresh();

            _lastFileLength = fs.Length;
            _lastFilePath = LogFilePath;
        }
        catch (Exception ex)
        {
            Content = $"(读取失败: {ex.Message})";
        }
    }

    /// <summary>解析 Serilog 格式行 → LogEntry。格式示例：[14:28:40 INF] Starting Dc.App</summary>
    private static LogEntry ParseLine(string line)
    {
        // Serilog 默认格式: [HH:mm:ss LEVEL] message  或  HH:mm:ss.fff LEVEL message
        var (timestamp, level, tag, message) = ExtractParts(line);
        return new LogEntry
        {
            Timestamp = timestamp,
            Level = level,
            LevelTag = tag,
            Message = message,
            RawLine = line
        };
    }

    private static (string ts, LogLevel level, string tag, string msg) ExtractParts(string line)
    {
        // Try bracket format: [HH:mm:ss INF] ...
        if (line.StartsWith('['))
        {
            var close = line.IndexOf(']');
            if (close > 0)
            {
                var inner = line[1..close];
                var space = inner.IndexOf(' ');
                if (space > 0)
                {
                    var ts = inner[..space];
                    var tag = inner[(space + 1)..];
                    var msg = line.Length > close + 2 ? line[(close + 2)..] : "";
                    return (ts, ToLevel(tag), tag, msg);
                }
            }
        }

        // Try space-delimited: HH:mm:ss.fff INF ...
        var firstSpace = line.IndexOf(' ');
        if (firstSpace > 0)
        {
            var secondSpace = line.IndexOf(' ', firstSpace + 1);
            if (secondSpace > 0)
            {
                var ts = line[..firstSpace];
                var tag = line.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
                var msg = line[(secondSpace + 1)..];
                return (ts, ToLevel(tag), tag, msg);
            }
        }

        return ("", LogLevel.Info, "INF", line);
    }

    private static LogLevel ToLevel(string tag) => tag.ToUpperInvariant() switch
    {
        "FTL" or "ERR" => LogLevel.Error,
        "WRN" or "WRN" => LogLevel.Warning,
        "VRB" or "DBG" => LogLevel.Info,
        _ when tag.StartsWith("ERR", StringComparison.OrdinalIgnoreCase) => LogLevel.Error,
        _ when tag.StartsWith("WRN", StringComparison.OrdinalIgnoreCase) => LogLevel.Warning,
        _ => LogLevel.Info
    };

    [RelayCommand]
    private void OpenFolder()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(dir))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
    }

    [RelayCommand]
    private void CopyContent()
    {
        if (!string.IsNullOrEmpty(Content))
            System.Windows.Clipboard.SetText(Content);
    }

    public void Dispose() => _autoRefreshTimer.Dispose();
}
