using System.Collections.Generic;
using Dc.App.ViewModels;
using Dc.Domain.Entities;
using Dc.Infrastructure.Orchestration;

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
        Assert.Equal("g1", vm.ToResult().Tag.GroupId);
    }

    [Fact]
    public void Edit_ExistingScaledRealTag_RestoresScaleFields()
    {
        var g = Grp("g1", "温度组");
        var existing = new Tag
        {
            Id = "tag1",
            Item = "x",
            DataType = 4,
            GroupId = "g1",
            TaskId = "t1",
            ScaleFactor = 0.1,
            Offset = -5
        };
        var vm = new TagEditorViewModel(new[] { g }, existing, defaultGroup: null);

        Assert.Equal("0.1", vm.ScaleFactor);
        Assert.Equal("-5", vm.Offset);
        var result = vm.ToResult();
        Assert.Equal(0.1, result.Tag.ScaleFactor);
        Assert.Equal(-5, result.Tag.Offset);
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

    [Fact]
    public void ToResult_RealTag_NoFormula()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g);
        vm.Item = "ns=3;i=1002";
        var result = vm.ToResult();

        Assert.NotNull(result);
        Assert.Equal("ns=3;i=1002", result.Tag.Item);
        Assert.Equal("g1", result.Tag.GroupId);
        Assert.Null(result.Formula);
        Assert.Empty(result.Inputs);
    }

    [Theory]
    [InlineData("T * 1.8 + 32", new[] { "T" })]
    [InlineData("T * 1.8 + P / (T + 273.15)", new[] { "T", "P" })]          // 去重保序
    [InlineData("SQRT(T) + SIN(P) + PI + E", new[] { "T", "P" })]           // 排除函数+常量
    [InlineData("AVG(A, B, C) + SUM(X, Y)", new[] { "A", "B", "C", "X", "Y" })]
    [InlineData("123 + 4.5", new string[0])]                                 // 纯数字无变量
    public void ExtractAliases_ReturnsDedupedOrdered_ExcludingBuiltins(string expr, string[] expected)
    {
        Assert.Equal(expected, TagEditorViewModel.ExtractAliases(expr));
    }

    private static Tag RealTag(string id, string item, string taskId = "t1", int dataType = 5)
        => new() { Id = id, Item = item, DataType = dataType, TaskId = taskId, IsVirtual = false };

    [Fact]
    public void Virtual_ExpressionExtractsInputs_AndToResultBuildsFormula()
    {
        var g = Grp("g1", "温度组");
        var realT = RealTag("rt1", "Random");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { realT });

        vm.IsVirtual = true;
        vm.FormulaName = "Sum";
        vm.Expression = "T * 2";
        // 提取出 T 行
        Assert.Single(vm.InputBindings);
        Assert.Equal("T", vm.InputBindings[0].Alias);
        Assert.Null(vm.InputBindings[0].SelectedTag);

        // 选 T
        vm.InputBindings[0].SelectedTag = realT;
        var result = vm.ToResult();

        Assert.True(result.Tag.IsVirtual);
        Assert.Equal("Sum", result.Tag.Item);
        Assert.NotNull(result.Formula);
        Assert.Equal("T * 2", result.Formula!.Expression);
        Assert.Equal("Sum", result.Formula.Name);
        Assert.Equal(result.Tag.Id, result.Formula.OutputTagId);
        Assert.Equal("t1", result.Formula.TaskId);
        Assert.Single(result.Inputs);
        Assert.Equal("T", result.Inputs[0].Alias);
        Assert.Equal("rt1", result.Inputs[0].SourceTagId);
    }

    [Fact]
    public void Virtual_ExpressionChange_PreservesSelectedKeepsNewNull()
    {
        var g = Grp("g1", "温度组");
        var realT = RealTag("rt1", "Random");
        var realP = RealTag("rt2", "Counter");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { realT, realP });

        vm.IsVirtual = true;
        vm.Expression = "T";
        vm.InputBindings[0].SelectedTag = realT;

        vm.Expression = "T + P";
        Assert.Equal(2, vm.InputBindings.Count);
        Assert.Equal("T", vm.InputBindings[0].Alias);
        Assert.Same(realT, vm.InputBindings[0].SelectedTag);   // 保留已选
        Assert.Equal("P", vm.InputBindings[1].Alias);
        Assert.Null(vm.InputBindings[1].SelectedTag);           // 新增空
    }

    [Fact]
    public void Edit_ExistingVirtualTag_PreselectsInputsFromFormula()
    {
        var g = Grp("g1", "温度组");
        var realT = RealTag("rt1", "Random");
        var existing = new Tag { Id = "vt1", Item = "Sum", IsVirtual = true, GroupId = "g1", TaskId = "t1" };
        var formula = new Formula
        {
            Id = "f1", Name = "Sum", Expression = "T * 2", OutputTagId = "vt1", TaskId = "t1",
            Inputs = new List<FormulaInput> { new() { Id = "fi1", FormulaId = "f1", Alias = "T", SourceTagId = "rt1" } }
        };
        var vm = new TagEditorViewModel(new[] { g }, existing, defaultGroup: g,
            taskTags: new[] { realT, existing },
            existingFormulas: new[] { formula });

        Assert.True(vm.IsVirtual);
        Assert.Equal("Sum", vm.FormulaName);
        Assert.Equal("T * 2", vm.Expression);
        Assert.Single(vm.InputBindings);
        Assert.Equal("T", vm.InputBindings[0].Alias);
        Assert.NotNull(vm.InputBindings[0].SelectedTag);
        Assert.Equal("rt1", vm.InputBindings[0].SelectedTag!.Id);
    }

    [Fact]
    public void ToggleIsVirtual_AfterExpression_RebuildsInputBindings()
    {
        var g = Grp("g1", "温度组");
        var realT = RealTag("rt1", "Random");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { realT });
        // Set expression BEFORE toggling virtual on
        vm.Expression = "T * 2";
        Assert.Empty(vm.InputBindings);   // not virtual yet → no rows
        vm.IsVirtual = true;
        Assert.Single(vm.InputBindings);
        Assert.Equal("T", vm.InputBindings[0].Alias);
    }

    private static IFormulaValidator Validator() => new FormulaValidator();

    [Fact]
    public void Validate_RealTag_BadScaleNumber_HasError()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            formulaValidator: Validator());
        vm.Item = "x";
        vm.ScaleFactor = "abc";
        Assert.Contains(vm.Validate(), e => e.Contains("缩放"));
    }

    [Fact]
    public void Validate_RealTag_EmptyScale_NullScale()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g);
        vm.Item = "x";
        Assert.Null(vm.ToResult().Tag.ScaleFactor);
    }

    [Fact]
    public void Validate_Virtual_MissingName_HasError()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { RealTag("rt1", "Random") },
            formulaValidator: Validator());
        vm.IsVirtual = true;
        vm.Expression = "T";
        Assert.Contains(vm.Validate(), e => e.Contains("公式名"));
    }

    [Fact]
    public void Validate_Virtual_DuplicateName_HasError()
    {
        var g = Grp("g1", "温度组");
        var existingVirtual = new Tag { Id = "rv1", Item = "Sum", IsVirtual = true, TaskId = "t1" };
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { existingVirtual, RealTag("rt1", "Random") },
            formulaValidator: Validator());
        vm.IsVirtual = true;
        vm.FormulaName = "Sum";   // 与已有虚拟同名
        vm.Expression = "T";
        vm.InputBindings[0].SelectedTag = RealTag("rt1", "Random");
        Assert.Contains(vm.Validate(), e => e.Contains("已存在"));
    }

    [Fact]
    public void Validate_Virtual_UnselectedInput_HasError()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { RealTag("rt1", "Random") },
            formulaValidator: Validator());
        vm.IsVirtual = true;
        vm.FormulaName = "Doubled";
        vm.Expression = "T";   // T 行未选
        Assert.Contains(vm.Validate(), e => e.Contains("未选择输入"));
    }

    [Fact]
    public void Validate_Virtual_StringInputTag_HasError()
    {
        var g = Grp("g1", "温度组");
        var strTag = RealTag("rs1", "Name", dataType: 8); // String
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { strTag },
            formulaValidator: Validator());
        vm.IsVirtual = true;
        vm.FormulaName = "Doubled";
        vm.Expression = "T";
        vm.InputBindings[0].SelectedTag = strTag;
        Assert.Contains(vm.Validate(), e => e.Contains("数值化"));
    }

    [Fact]
    public void Validate_Virtual_Valid_NoErrors()
    {
        var g = Grp("g1", "温度组");
        var vm = new TagEditorViewModel(new[] { g }, existing: null, defaultGroup: g,
            taskTags: new[] { RealTag("rt1", "Random") },
            formulaValidator: Validator());
        vm.IsVirtual = true;
        vm.FormulaName = "Doubled";
        vm.Expression = "T * 2";
        vm.InputBindings[0].SelectedTag = RealTag("rt1", "Random");
        Assert.Empty(vm.Validate());
    }
}
