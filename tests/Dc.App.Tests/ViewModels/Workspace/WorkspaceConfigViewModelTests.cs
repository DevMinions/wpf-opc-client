using Dc.App.Services;
using Dc.App.ViewModels.Workspace;
using Dc.Domain.Entities;

namespace Dc.App.Tests.ViewModels.Workspace;

public class WorkspaceConfigViewModelTests
{
    private sealed class FakeEditor : ITaskEditorDialog
    {
        public CollectorTask? ReturnValue;
        public CollectorTask? LastArg;
        public int Calls;
        public CollectorTask? Edit(CollectorTask? existing)
        {
            Calls++; LastArg = existing; return ReturnValue;
        }
    }

    private static CollectorTask Task1(string id = "t1")
        => new() { Id = id, Server = "炉温", Node = "opc.tcp://x", Type = 2,
                   TcpAddress = "10.0.0.1:9000", Interval = 1000, Deviation = 1 };

    [Fact]
    public void SetTask_PopulatesReadonlyFields()
    {
        var vm = new WorkspaceConfigViewModel(new FakeEditor());
        vm.SetTask(Task1());
        Assert.Equal("炉温", vm.Server);
        Assert.Equal("opc.tcp://x", vm.Node);
        Assert.Equal("UA", vm.ProtocolLabel);
        Assert.Equal("10.0.0.1:9000", vm.TcpAddress);
        Assert.Equal(1000, vm.Interval);
        Assert.Equal(1, vm.Deviation);
        Assert.True(vm.HasTask);
    }

    [Fact]
    public void SetTask_Null_ClearsHasTask()
    {
        var vm = new WorkspaceConfigViewModel(new FakeEditor());
        vm.SetTask(Task1());
        vm.SetTask(null);
        Assert.False(vm.HasTask);
    }

    [Fact]
    public void EditCommand_CallsDialogWithCurrentTask()
    {
        var editor = new FakeEditor { ReturnValue = null };
        var vm = new WorkspaceConfigViewModel(editor);
        var task = Task1();
        vm.SetTask(task);
        vm.EditCommand.Execute(null);
        Assert.Equal(1, editor.Calls);
        Assert.Same(task, editor.LastArg);
    }

    [Fact]
    public void EditCommand_OnSuccess_RaisesEdited()
    {
        var result = Task1("t1");
        var editor = new FakeEditor { ReturnValue = result };
        var vm = new WorkspaceConfigViewModel(editor);
        vm.SetTask(Task1("t1"));
        CollectorTask? edited = null;
        vm.Edited += t => edited = t;
        vm.EditCommand.Execute(null);
        Assert.Same(result, edited);
    }

    [Fact]
    public void EditCommand_NoTask_DoesNothing()
    {
        var editor = new FakeEditor();
        var vm = new WorkspaceConfigViewModel(editor);
        vm.EditCommand.Execute(null);
        Assert.Equal(0, editor.Calls);
    }
}
