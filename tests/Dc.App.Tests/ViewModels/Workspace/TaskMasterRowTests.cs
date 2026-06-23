using System.ComponentModel;
using Dc.App.ViewModels.Workspace;

namespace Dc.App.Tests.ViewModels.Workspace;

public class TaskMasterRowTests
{
    private static TaskMasterRow Row() => new("t1", "Task 1", "UA");

    [Fact]
    public void StatusKey_DefaultsToStopped()
        // IsRunning 默认 false(行新建时编排器尚未回填运行态)→ 与 XAML DataTrigger 一致判为 Stopped。
        => Assert.Equal("Stopped", Row().StatusKey);

    [Fact]
    public void StatusKey_RunningWhenIsRunning()
    {
        var r = Row();
        r.IsRunning = true;
        Assert.Equal("Running", r.StatusKey);
    }

    [Fact]
    public void StatusKey_AlertTakesPriorityOverStopped()
    {
        var r = Row();
        r.IsRunning = false;
        r.HasAlert = true; // 告警优先于已停止(与 XAML DataTrigger 顺序一致)
        Assert.Equal("Alert", r.StatusKey);
    }

    [Fact]
    public void StatusKey_RaisesPropertyChanged_OnIsRunning()
    {
        var r = Row();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)r).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        r.IsRunning = true;
        Assert.Contains(nameof(TaskMasterRow.StatusKey), raised);
    }

    [Fact]
    public void StatusKey_RaisesPropertyChanged_OnHasAlert()
    {
        var r = Row();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)r).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        r.HasAlert = true;
        Assert.Contains(nameof(TaskMasterRow.StatusKey), raised);
    }
}
