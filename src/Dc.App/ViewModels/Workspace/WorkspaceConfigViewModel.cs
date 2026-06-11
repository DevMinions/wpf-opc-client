using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dc.App.Services;
using Dc.Domain.Entities;

namespace Dc.App.ViewModels.Workspace;

public sealed partial class WorkspaceConfigViewModel : ObservableObject
{
    private readonly ITaskEditorDialog _editor;
    private CollectorTask? _task;

    [ObservableProperty] private bool _hasTask;
    [ObservableProperty] private string _server = string.Empty;
    [ObservableProperty] private string _node = string.Empty;
    [ObservableProperty] private string _protocolLabel = string.Empty;
    [ObservableProperty] private string _tcpAddress = string.Empty;
    [ObservableProperty] private int _interval;
    [ObservableProperty] private int _deviation;

    public IRelayCommand EditCommand { get; }

    public event Action<CollectorTask>? Edited;

    public WorkspaceConfigViewModel(ITaskEditorDialog editor)
    {
        _editor = editor;
        EditCommand = new RelayCommand(Edit);
    }

    public void SetTask(CollectorTask? task)
    {
        _task = task;
        HasTask = task is not null;
        Server = task?.Server ?? string.Empty;
        Node = task?.Node ?? string.Empty;
        ProtocolLabel = task is null ? string.Empty : Label(task.Type);
        TcpAddress = task?.TcpAddress ?? string.Empty;
        Interval = task?.Interval ?? 0;
        Deviation = task?.Deviation ?? 0;
    }

    private void Edit()
    {
        if (_task is null) return;
        var result = _editor.Edit(_task);
        if (result is not null) Edited?.Invoke(result);
    }

    private static string Label(byte type) => type switch
    {
        1 => "DA", 2 => "UA", 3 => "AE", _ => "?"
    };
}
