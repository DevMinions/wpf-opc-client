using Dc.App.ViewModels;
using Dc.Domain.Entities;

namespace Dc.App.Tests.ViewModels;

public class GroupEditorViewModelTests
{
    private static CollectorTask Task(string id, string server) => new() { Id = id, Server = server };

    [Fact]
    public void Create_WithDefaultTask_HidesSelector_TitleHasTask_LocksTask()
    {
        var t = Task("t1", "炉温");
        var vm = new GroupEditorViewModel(new[] { t }, existing: null, defaultTask: t);

        Assert.False(vm.ShowTaskSelector);
        Assert.Contains("新建分组", vm.Title);
        Assert.Contains("炉温", vm.Title);
        vm.Name = "G1";
        Assert.Equal("t1", vm.ToEntity().TaskId);
    }

    [Fact]
    public void Create_WithoutDefaultTask_ShowsSelector_TaskNull()
    {
        var t = Task("t1", "炉温");
        var vm = new GroupEditorViewModel(new[] { t }, existing: null, defaultTask: null);

        Assert.True(vm.ShowTaskSelector);
        Assert.Null(vm.Task);
        Assert.Equal("新建分组", vm.Title);
    }

    [Fact]
    public void Edit_HidesSelector_TitleHasTask()
    {
        var t = Task("t1", "炉温");
        var existing = new Group { Id = "g1", Name = "G", TaskId = "t1" };
        var vm = new GroupEditorViewModel(new[] { t }, existing, defaultTask: null);

        Assert.False(vm.ShowTaskSelector);
        Assert.Contains("编辑分组", vm.Title);
        Assert.Contains("炉温", vm.Title);
    }

    [Fact]
    public void Edit_OrphanedTask_ShowsSelector_TitlePlain()
    {
        // existing.TaskId 不在 AvailableTasks 中（任务已删）→ 无法锁定 → 显示选择器、标题退化
        var t = Task("t1", "炉温");
        var existing = new Group { Id = "g1", Name = "G", TaskId = "GONE" };
        var vm = new GroupEditorViewModel(new[] { t }, existing, defaultTask: null);

        Assert.True(vm.ShowTaskSelector);
        Assert.Null(vm.Task);
        Assert.Equal("编辑分组", vm.Title);
    }
}
