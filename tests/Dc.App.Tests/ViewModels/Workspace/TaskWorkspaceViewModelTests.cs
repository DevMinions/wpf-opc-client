using Dc.App.ViewModels.Workspace;
using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;
using Dc.App.ViewModels.Dashboard;
using Dc.Opc.Abstractions;

namespace Dc.App.Tests.ViewModels.Workspace;

public class TaskWorkspaceViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTaskSource : IWorkspaceTaskSource
    {
        public List<CollectorTask> Tasks { get; set; } = new();
        public List<CollectorTask> Saved { get; } = new();

        public Task<IReadOnlyList<CollectorTask>> LoadTasksAsync()
            => Task.FromResult<IReadOnlyList<CollectorTask>>(Tasks);

        public Task<(CollectorTask? Task, IReadOnlyList<TagDescriptor> Tags)> GetTaskWithTagsAsync(string taskId)
        {
            var t = Tasks.FirstOrDefault(x => x.Id == taskId);
            return Task.FromResult<(CollectorTask?, IReadOnlyList<TagDescriptor>)>(
                (t, Array.Empty<TagDescriptor>()));
        }

        public Task SaveNewTaskAsync(CollectorTask task)
        {
            Saved.Add(task);
            Tasks.Add(task);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrchView : IDashboardOrchestratorView
    {
        public IReadOnlyList<TaskDiagnostics> Diags { get; set; } = Array.Empty<TaskDiagnostics>();
        public IReadOnlyCollection<string> Running { get; set; } = Array.Empty<string>();
        public IReadOnlyList<TaskDiagnostics> GetDiagnostics() => Diags;
        public IReadOnlyCollection<string> RunningTaskIds => Running;
    }

    private sealed class FakeTagPanel : IEmbeddableTagPanel
    {
        public bool IsEmbedded { get; set; }
        public string? TaskScope { get; set; }
        public Dc.Domain.Entities.Group? GroupFilter { get; set; }
        public int LoadCount;
        public Task LoadAsync() { LoadCount++; return Task.CompletedTask; }
        public int ImportCount;
        public Task ImportAsync() { ImportCount++; return Task.CompletedTask; }
    }

    private sealed class FakeGroupPanel
        : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IEmbeddableGroupPanel
    {
        public bool IsEmbedded { get; set; }
        private CollectorTask? _taskFilter;
        public CollectorTask? TaskFilter { get => _taskFilter; set => SetProperty(ref _taskFilter, value); }
        private Group? _selectedGroup;
        public Group? SelectedGroup { get => _selectedGroup; private set => SetProperty(ref _selectedGroup, value); }
        public int LoadCount;
        public Task LoadAsync() { LoadCount++; return Task.CompletedTask; }
        public void SimulateSelect(Group g) => SelectedGroup = g;
    }

    private sealed class FakeLivePanel : IEmbeddableLivePanel
    {
        public string? TaskFilter { get; set; }
    }

    private sealed class FakeDiagPanel : IEmbeddableDiagPanel
    {
        public string? TaskScope { get; set; }
    }

    private sealed class FakeEditor : Dc.App.Services.ITaskEditorDialog
    {
        public CollectorTask? Edit(CollectorTask? existing) => null;
    }

    private static CollectorTask Task1(string id, string server = "炉温", byte type = 2)
        => new() { Id = id, Server = server, Node = "opc.tcp://x", Type = type,
                   TcpAddress = "10.0.0.1:9000", Interval = 1000, Deviation = 1, CreatedAt = Now.UtcDateTime };

    // ── Deps holder + full builder ────────────────────────────────────────

    private sealed class Deps
    {
        public FakeTaskSource Src = new();
        public FakeOrchView Orch = new();
        public FakeTagPanel Tag = new();
        public FakeGroupPanel Group = new();
        public FakeLivePanel Live = new();
        public FakeDiagPanel Diag = new();
        public WorkspaceOverviewViewModel Overview = null!;
        public WorkspaceConfigViewModel Config = null!;
    }

    private static (Deps deps, TaskWorkspaceViewModel vm) BuildFull()
    {
        var d = new Deps();
        d.Overview = new WorkspaceOverviewViewModel(d.Orch, () => Now);
        d.Config = new WorkspaceConfigViewModel(new FakeEditor());
        var vm = new TaskWorkspaceViewModel(
            d.Src, d.Orch, () => Now, TimeSpan.FromSeconds(120),
            d.Overview, d.Tag,
            orchestrator: null,
            editor: null,
            groupsPanel: d.Group,
            livePanel: d.Live,
            diagPanel: d.Diag,
            config: d.Config);
        return (d, vm);
    }

    private static (FakeTaskSource, FakeOrchView, FakeTagPanel, TaskWorkspaceViewModel) BuildWithTags()
    {
        var (d, vm) = BuildFull();
        return (d.Src, d.Orch, d.Tag, vm);
    }

    private static (FakeTaskSource src, FakeOrchView orch, TaskWorkspaceViewModel vm) Build()
    {
        var (d, vm) = BuildFull();
        return (d.Src, d.Orch, vm);
    }

    // ── Existing tests ────────────────────────────────────────────────────

    [Fact]
    public async Task Load_PopulatesRows()
    {
        var (src, _, vm) = Build();
        src.Tasks = new() { Task1("t1"), Task1("t2", "压力") };
        await vm.LoadAsync();
        Assert.Equal(2, vm.AllTasks.Count);
        Assert.Equal("炉温", vm.AllTasks[0].Name);
    }

    [Fact]
    public async Task Load_MarksRunningRows()
    {
        var (src, orch, vm) = Build();
        src.Tasks = new() { Task1("t1"), Task1("t2") };
        orch.Running = new[] { "t1" };
        await vm.LoadAsync();
        Assert.True(vm.AllTasks.Single(r => r.TaskId == "t1").IsRunning);
        Assert.False(vm.AllTasks.Single(r => r.TaskId == "t2").IsRunning);
    }

    [Fact]
    public async Task Summary_CountsRunningStoppedAlert()
    {
        var (src, orch, vm) = Build();
        src.Tasks = new() { Task1("t1"), Task1("t2"), Task1("t3") };
        orch.Running = new[] { "t1", "t2" };
        orch.Diags = new[]
        {
            new TaskDiagnostics("t1", Now.AddMinutes(-5), Now, Now, 10, 0, 0, 5),
            new TaskDiagnostics("t2", Now.AddMinutes(-5), Now, Now, 10, 0, 0, 5),
            new TaskDiagnostics("t3", Now.AddMinutes(-5), Now, Now, 10, 0, 0, 5)
        };
        await vm.LoadAsync();
        Assert.Equal(2, vm.RunningCount);
        Assert.Equal(1, vm.StoppedCount);
        Assert.Equal(1, vm.AlertCount);
    }

    [Fact]
    public async Task SearchText_FiltersByNameOrServer()
    {
        var (src, _, vm) = Build();
        src.Tasks = new() { Task1("t1", "炉温监测"), Task1("t2", "压力站") };
        await vm.LoadAsync();
        vm.SearchText = "压力";
        var visible = vm.FilteredTasks.Cast<TaskMasterRow>().ToList();
        Assert.Single(visible);
        Assert.Equal("t2", visible[0].TaskId);
    }

    [Fact]
    public async Task StatusFilter_Running_ShowsOnlyRunning()
    {
        var (src, orch, vm) = Build();
        src.Tasks = new() { Task1("t1"), Task1("t2") };
        orch.Running = new[] { "t1" };
        await vm.LoadAsync();
        vm.StatusFilter = WorkspaceStatusFilter.Running;
        var visible = vm.FilteredTasks.Cast<TaskMasterRow>().ToList();
        Assert.Single(visible);
        Assert.Equal("t1", visible[0].TaskId);
    }

    [Fact]
    public async Task SelectingTask_SetsSelectedAndExposesTaskId()
    {
        var (src, _, vm) = Build();
        src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];
        Assert.NotNull(vm.SelectedTask);
        Assert.Equal("t1", vm.SelectedTask!.TaskId);
    }

    [Fact]
    public async Task SelectingTask_SetsTagScopeAndDefaultsToOverviewTab()
    {
        var (src, _, tagPanel, vm) = BuildWithTags();
        src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];
        Assert.Equal("t1", tagPanel.TaskScope);
        Assert.True(tagPanel.LoadCount >= 1);
        Assert.Equal("overview", vm.SelectedTab);
        Assert.Same(vm.Overview, vm.CurrentTabContent);
    }

    [Fact]
    public async Task SwitchingToTagsTab_SetsCurrentContentToTagsPanel()
    {
        var (src, _, tagPanel, vm) = BuildWithTags();
        src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];
        vm.SelectedTab = "tags";
        Assert.Same(tagPanel, vm.CurrentTabContent);
    }

    // ── New tests (S3b.3) ─────────────────────────────────────────────────

    [Fact]
    public async Task SelectingTask_ConfiguresAllPanelScopes()
    {
        var (d, vm) = BuildFull();
        d.Src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];
        Assert.Equal("t1", d.Live.TaskFilter);
        Assert.Equal("t1", d.Diag.TaskScope);
        Assert.Equal("t1", d.Group.TaskFilter?.Id);
        Assert.True(d.Config.HasTask);
    }

    [Fact]
    public async Task SelectingGroupInGroupPanel_JumpsToTagsTabWithFilter()
    {
        var (d, vm) = BuildFull();
        d.Src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];
        var grp = new Group { Id = "g1", Name = "炉膛", TaskId = "t1" };
        d.Group.SimulateSelect(grp);
        Assert.Equal("tags", vm.SelectedTab);
        Assert.Same(grp, d.Tag.GroupFilter);
        Assert.Same(d.Tag, vm.CurrentTabContent);
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("livedata")]
    [InlineData("diagnostics")]
    [InlineData("config")]
    public async Task SwitchingTab_SetsCurrentContent(string tab)
    {
        var (d, vm) = BuildFull();
        d.Src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];
        vm.SelectedTab = tab;
        object expected = tab switch
        {
            "groups"      => (object)d.Group,
            "livedata"    => d.Live,
            "diagnostics" => d.Diag,
            "config"      => d.Config,
            _             => d.Overview
        };
        Assert.Same(expected, vm.CurrentTabContent);
    }

    [Fact]
    public async Task Import_DelegatesToTagPanel_AndSwitchesToTagsTab()
    {
        var (d, vm) = BuildFull();
        d.Src.Tasks = new() { Task1("t1") };
        await vm.LoadAsync();
        vm.SelectedTask = vm.AllTasks[0];

        await vm.ImportAsync();

        Assert.True(d.Tag.ImportCount >= 1);
        Assert.Equal("tags", vm.SelectedTab);
    }

    // ── Task 3: NewTaskCommand ────────────────────────────────────────────────

    private sealed class SavingEditor : Dc.App.Services.ITaskEditorDialog
    {
        public CollectorTask? Edit(CollectorTask? existing) => Task1("new-1");
    }

    [Fact]
    public async Task NewTaskCommand_Exists_And_Saves_Via_Editor()
    {
        var src = new FakeTaskSource();
        var orch = new FakeOrchView();
        var overview = new WorkspaceOverviewViewModel(orch, () => Now);
        var config = new WorkspaceConfigViewModel(new FakeEditor());
        var vm = new TaskWorkspaceViewModel(
            src, orch, () => Now, TimeSpan.FromSeconds(120),
            overview, new FakeTagPanel(),
            orchestrator: null,
            editor: new SavingEditor(),
            groupsPanel: new FakeGroupPanel(),
            livePanel: new FakeLivePanel(),
            diagPanel: new FakeDiagPanel(),
            config: config);

        Assert.NotNull(vm.NewTaskCommand);
        await vm.NewTaskCommand.ExecuteAsync(null);

        Assert.Single(src.Saved);
    }
}
