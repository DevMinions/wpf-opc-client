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

    public TaskMasterRow(string taskId, string name, string protocol)
    {
        TaskId = taskId;
        Name = name;
        Protocol = protocol;
    }
}
