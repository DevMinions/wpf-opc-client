using CommunityToolkit.Mvvm.ComponentModel;

namespace Dc.App.ViewModels.Workspace;

public sealed partial class TaskMasterRow : ObservableObject
{
    public string TaskId { get; }
    public string Name { get; }
    public string Protocol { get; }

    [ObservableProperty] private int _tagCount;
    [ObservableProperty] private double _ratePerSecond;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasAlert;

    // 状态文案 key 后缀(配合 Workspace_Status 前缀)。优先级须与 XAML DataTrigger 一致:
    // 告警 > 已停止 > 运行中。IsRunning/HasAlert 变化时通知重算(下方 partial 钩子)。
    public string StatusKey => HasAlert ? "Alert" : !IsRunning ? "Stopped" : "Running";

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StatusKey));
    partial void OnHasAlertChanged(bool value) => OnPropertyChanged(nameof(StatusKey));

    public TaskMasterRow(string taskId, string name, string protocol)
    {
        TaskId = taskId;
        Name = name;
        Protocol = protocol;
    }
}
