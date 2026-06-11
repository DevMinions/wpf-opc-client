using Dc.App.ViewModels;
using Dc.Domain.Entities;

namespace Dc.App.Tests.ViewModels;

public class TagEditorViewModelTests
{
    private static Group Grp(string id, string name, string taskId = "t1")
        => new() { Id = id, Name = name, TaskId = taskId };

    [Fact]
    public void Create_WithDefaultGroup_HidesSelector_TitleHasGroup_LocksGroup()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g);

        Assert.False(vm.ShowGroupSelector);
        Assert.Contains("新建 Tag", vm.Title);
        Assert.Contains("温度组", vm.Title);
        vm.Item = "tag.a";
        Assert.Equal("g1", vm.ToEntity().GroupId);
    }

    [Fact]
    public void Create_WithoutDefaultGroup_ShowsSelector_GroupNull()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: null);

        Assert.True(vm.ShowGroupSelector);
        Assert.Null(vm.Group);   // 不兜底选第一个分组
        Assert.Equal("新建 Tag", vm.Title);
    }

    [Fact]
    public void Edit_HidesSelector_TitleHasGroup()
    {
        var g = Grp("g1", "温度组");
        var existing = new Tag { Id = "tag1", Item = "x", DataType = 4, GroupId = "g1", TaskId = "t1" };
        var vm = new TagEditorViewModel(new[] { g }, existing, defaultGroup: null);

        Assert.False(vm.ShowGroupSelector);
        Assert.Contains("编辑 Tag", vm.Title);
        Assert.Contains("温度组", vm.Title);
    }

    [Fact]
    public void Edit_OrphanedGroup_ShowsSelector_TitlePlain()
    {
        // existing.GroupId 不在 AvailableGroups 中（分组已删）→ 无法锁定 → 显示选择器、标题退化
        var g = Grp("g1", "温度组");
        var existing = new Tag { Id = "tag1", Item = "x", DataType = 4, GroupId = "GONE", TaskId = "t1" };
        var vm = new TagEditorViewModel(new[] { g }, existing, defaultGroup: null);

        Assert.True(vm.ShowGroupSelector);
        Assert.Null(vm.Group);
        Assert.Equal("编辑 Tag", vm.Title);
    }
}
