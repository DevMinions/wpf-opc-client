# 任务上下文内建分组/Tag —— 隐藏冗余归属选择器 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 从任务工作台新建/编辑分组、Tag 时，弹窗不再显示冗余的"所属任务/所属分组"下拉，归属锁定为当前上下文，上下文写进标题。

**架构：** 两个编辑器 VM（GroupEditorViewModel / TagEditorViewModel）各加一个**构造时固定**的只读 `ShowTaskSelector`/`ShowGroupSelector` 标志 + 把上下文拼进 `Title`；对应 Window XAML 把归属那一行的标签+ComboBox 的 `Visibility` 绑该标志，并把行高 36 改 Auto 以收缩。纯展示层，采集/持久化零改动。

**技术栈：** WPF（net8.0-windows）、CommunityToolkit.Mvvm、xUnit。构建/测试/截图走 dc-remote（home 工作区）。

**规格依据：** `docs/superpowers/specs/2026-06-11-task-scoped-group-tag-editor-design.md`

**dc-remote 命令：**
- 同步+构建：`~/dc-remote.sh home sync && ~/dc-remote.sh home build`
- 测试：`~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter <名>'`（末尾 `total=N passed=N failed=N`）
- 截图：`~/dc-remote.sh home run` → `~/dc-remote.sh home ui click ...` → `~/dc-remote.sh home shot` → Read `/tmp/dc-home-screen.png`

---

## 文件结构

**修改：**
- `src/Dc.App/ViewModels/GroupEditorViewModel.cs` — 加 `ShowTaskSelector`（ctor 固定）+ 标题带任务 + 去掉 create 的"兜底选第一个任务"。
- `src/Dc.App/Views/GroupEditorWindow.xaml` — 所属任务行 Visibility 绑 `ShowTaskSelector`，行高 36→Auto。
- `src/Dc.App/ViewModels/TagEditorViewModel.cs` — 加 `ShowGroupSelector`（ctor 固定）+ 标题带分组 + 去掉 create 的"兜底选第一个分组"。
- `src/Dc.App/Views/TagEditorWindow.xaml` — 所属分组行 Visibility 绑 `ShowGroupSelector`，行高 36→Auto。

**新增测试：**
- `tests/Dc.App.Tests/ViewModels/GroupEditorViewModelTests.cs`
- `tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs`

**已核实事实（实现者参考）：**
- `GroupEditorViewModel` 构造：`(IEnumerable<CollectorTask> tasks, Group? existing, CollectorTask? defaultTask = null)`；`ToEntity()` 用 `Task!.Id`；`Validate()` 要求 Task/Name 非空。
- `TagEditorViewModel` 构造：`(IEnumerable<Group> groups, Tag? existing, Group? defaultGroup = null, IBrowseDialog? browseDialog = null, Func<string,CollectorTask?>? taskLookup = null)`；`ToEntity()` 用 `Group!.Id`/`Group!.TaskId`；`Validate()` 已含 `if (Group is null) errors.Add("必须选择所属分组")`。
- 实体：`CollectorTask.Server`/`.Id`、`Group.Name`/`.Id`/`.TaskId`、`Tag.Item`/`.DataType`/`.GroupId`/`.TaskId`。
- App.xaml 已注册 `BooleanToVisibilityConverter x:Key="BoolToVis"`。
- GroupEditorWindow 归属行 = `Grid.Row=1`，该 Grid 第 2 个 `RowDefinition Height="36"`。TagEditorWindow 归属行 = `Grid.Row=2`，第 3 个 `RowDefinition Height="36"`。
- 两个 Window 标题已绑 `Title="{Binding Title}"`。

---

## 任务 1：GroupEditorViewModel

**文件：**
- 修改：`src/Dc.App/ViewModels/GroupEditorViewModel.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/GroupEditorViewModelTests.cs`

- [ ] **步骤 1：编写失败的测试**

创建 `tests/Dc.App.Tests/ViewModels/GroupEditorViewModelTests.cs`：

```csharp
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
        Assert.Null(vm.Task);   // 不兜底选第一个任务
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
}
```

- [ ] **步骤 2：运行测试验证失败**

运行：`~/dc-remote.sh home sync && ~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter GroupEditorViewModelTests'`
预期：编译失败（`ShowTaskSelector` 不存在）。

- [ ] **步骤 3：改 GroupEditorViewModel**

把 `src/Dc.App/ViewModels/GroupEditorViewModel.cs` 的构造函数体改为（加只读属性 `ShowTaskSelector`、去 create 兜底、标题带任务）：

```csharp
    public bool ShowTaskSelector { get; }

    public GroupEditorViewModel(IEnumerable<CollectorTask> tasks, Group? existing, CollectorTask? defaultTask = null)
    {
        foreach (var t in tasks) AvailableTasks.Add(t);

        if (existing is null)
        {
            _task = defaultTask;   // 不兜底选第一个：无上下文则留 null → 显示选择器
            _title = _task is null ? "新建分组" : $"新建分组 · 任务：{_task.Server}";
        }
        else
        {
            OriginalId = existing.Id;
            _name = existing.Name;
            _task = AvailableTasks.FirstOrDefault(t => t.Id == existing.TaskId);
            _title = _task is null ? "编辑分组" : $"编辑分组 · 任务：{_task.Server}";
        }

        ShowTaskSelector = _task is null;   // 有任务上下文 → 隐藏；无（防御）→ 显示
    }
```

> 注意：原 `if/else` 里给 `_title` 赋的是 `"新建分组"`/`"编辑分组"`，现替换为上面带任务名的形式；`_task = defaultTask ?? AvailableTasks.FirstOrDefault()` 改为 `_task = defaultTask`。其余成员（`AvailableTasks`/`Task`/`Name`/`Validate`/`ToEntity`）不动。

- [ ] **步骤 4：运行测试验证通过**

运行：`~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter GroupEditorViewModelTests'`
预期：`total=3 passed=3 failed=0`

- [ ] **步骤 5：Commit**

```bash
cd /home/adamyu/workspace/wpf-opc-client
git add src/Dc.App/ViewModels/GroupEditorViewModel.cs tests/Dc.App.Tests/ViewModels/GroupEditorViewModelTests.cs
git commit -m "✨ feat(ui): 分组编辑器 ShowTaskSelector + 标题带任务（锁定当前任务）"
```
（末尾加 `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`）

---

## 任务 2：GroupEditorWindow.xaml 隐藏任务行

**文件：** 修改 `src/Dc.App/Views/GroupEditorWindow.xaml`

> XAML 无单测，用「构建 0 错误 + 任务 5 截图」验证。

- [ ] **步骤 1：改行高为 Auto**

把归属所在 Grid 的 `RowDefinitions`：

```xml
                    <Grid.RowDefinitions>
                        <RowDefinition Height="36" /><RowDefinition Height="36" />
                    </Grid.RowDefinitions>
```
改为（第 2 行 36→Auto，隐藏时收缩为 0）：
```xml
                    <Grid.RowDefinitions>
                        <RowDefinition Height="36" /><RowDefinition Height="Auto" />
                    </Grid.RowDefinitions>
```

- [ ] **步骤 2：给所属任务行两元素绑 Visibility**

把这两行（`Grid.Row="1"` 的 TextBlock 与 ComboBox）：

```xml
                    <TextBlock Grid.Row="1" Grid.Column="0" Text="所属任务:"
                               Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                    <ComboBox Grid.Row="1" Grid.Column="1" ItemsSource="{Binding AvailableTasks}" SelectedItem="{Binding Task}">
```
分别加 `Visibility="{Binding ShowTaskSelector, Converter={StaticResource BoolToVis}}"`：
```xml
                    <TextBlock Grid.Row="1" Grid.Column="0" Text="所属任务:"
                               Visibility="{Binding ShowTaskSelector, Converter={StaticResource BoolToVis}}"
                               Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                    <ComboBox Grid.Row="1" Grid.Column="1"
                              Visibility="{Binding ShowTaskSelector, Converter={StaticResource BoolToVis}}"
                              ItemsSource="{Binding AvailableTasks}" SelectedItem="{Binding Task}">
```
（ComboBox 的 `<ComboBox.ItemTemplate>...</ComboBox>` 内容不动。）

- [ ] **步骤 3：构建验证**

运行：`~/dc-remote.sh home sync && ~/dc-remote.sh home build`
预期：`0 个错误`。

- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/Views/GroupEditorWindow.xaml
git commit -m "✨ feat(ui): 分组编辑器隐藏所属任务行（有上下文时）"
```

---

## 任务 3：TagEditorViewModel

**文件：**
- 修改：`src/Dc.App/ViewModels/TagEditorViewModel.cs`
- 测试：`tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs`

- [ ] **步骤 1：编写失败的测试**

创建 `tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs`：

```csharp
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
}
```

- [ ] **步骤 2：运行测试验证失败**

运行：`~/dc-remote.sh home sync && ~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter TagEditorViewModelTests'`
预期：编译失败（`ShowGroupSelector` 不存在）。

- [ ] **步骤 3：改 TagEditorViewModel 构造**

把 `src/Dc.App/ViewModels/TagEditorViewModel.cs` 构造函数的 `if (existing is null)/else` 块改为（去兜底、标题带分组），并加只读属性：

```csharp
    public bool ShowGroupSelector { get; }
```
构造体内（`foreach (var g in groups) AvailableGroups.Add(g);` 之后）：
```csharp
        if (existing is null)
        {
            _group = defaultGroup;   // 不兜底选第一个：无上下文留 null → 显示选择器
            _title = _group is null ? "新建 Tag" : $"新建 Tag · 分组：{_group.Name}";
        }
        else
        {
            OriginalId = existing.Id;
            _item = existing.Item;
            _dataType = OpcDataTypeOption.FromCode(existing.DataType);
            _group = AvailableGroups.FirstOrDefault(g => g.Id == existing.GroupId);
            _title = _group is null ? "编辑 Tag" : $"编辑 Tag · 分组：{_group.Name}";
        }

        ShowGroupSelector = _group is null;
```

> 注意：原 create 分支是 `_title = "新建 Tag"; _group = defaultGroup ?? AvailableGroups.FirstOrDefault();`，edit 分支 `_title = "编辑 Tag";`。现替换为上面形式。`_browseDialog`/`_taskLookup` 的赋值（在 foreach 之前）保持不动；`Validate`/`ToEntity`/`Browse` 等不动。

- [ ] **步骤 4：运行测试验证通过**

运行：`~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj --filter TagEditorViewModelTests'`
预期：`total=3 passed=3 failed=0`

- [ ] **步骤 5：Commit**

```bash
git add src/Dc.App/ViewModels/TagEditorViewModel.cs tests/Dc.App.Tests/ViewModels/TagEditorViewModelTests.cs
git commit -m "✨ feat(ui): Tag 编辑器 ShowGroupSelector + 标题带分组（锁定当前分组，无上下文回退显示）"
```

---

## 任务 4：TagEditorWindow.xaml 隐藏分组行

**文件：** 修改 `src/Dc.App/Views/TagEditorWindow.xaml`

- [ ] **步骤 1：改行高为 Auto**

把归属所在 Grid 的 `RowDefinitions`：
```xml
                    <Grid.RowDefinitions>
                        <RowDefinition Height="36" />
                        <RowDefinition Height="36" />
                        <RowDefinition Height="36" />
                    </Grid.RowDefinitions>
```
改为（第 3 行 36→Auto）：
```xml
                    <Grid.RowDefinitions>
                        <RowDefinition Height="36" />
                        <RowDefinition Height="36" />
                        <RowDefinition Height="Auto" />
                    </Grid.RowDefinitions>
```

- [ ] **步骤 2：给所属分组行两元素绑 Visibility**

把 `Grid.Row="2"` 的 TextBlock（`Text="所属分组:"`）与 ComboBox（`ItemsSource="{Binding AvailableGroups}"`）各加：
```xml
Visibility="{Binding ShowGroupSelector, Converter={StaticResource BoolToVis}}"
```
即：
```xml
                    <TextBlock Grid.Row="2" Grid.Column="0" Text="所属分组:"
                               Visibility="{Binding ShowGroupSelector, Converter={StaticResource BoolToVis}}"
                               Foreground="{DynamicResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                    <ComboBox Grid.Row="2" Grid.Column="1"
                              Visibility="{Binding ShowGroupSelector, Converter={StaticResource BoolToVis}}"
                              ItemsSource="{Binding AvailableGroups}" SelectedItem="{Binding Group}">
```
（ComboBox 的 `<ComboBox.ItemTemplate>` 内容不动。）

- [ ] **步骤 3：构建验证**

运行：`~/dc-remote.sh home sync && ~/dc-remote.sh home build`
预期：`0 个错误`。

- [ ] **步骤 4：Commit**

```bash
git add src/Dc.App/Views/TagEditorWindow.xaml
git commit -m "✨ feat(ui): Tag 编辑器隐藏所属分组行（有上下文时）"
```

---

## 任务 5：真机截图复核 + 全量回归

> 截图需先有数据：seed 一个任务 + 一个分组，再走「新建分组」「选分组→新建 Tag」截图。

- [ ] **步骤 1：全量 App 回归**

运行：`~/dc-remote.sh home sync && ~/dc-remote.sh home test 'tests/Dc.App.Tests/Dc.App.Tests.csproj'`
预期：全绿（现 88 + 新 6 = 94 上下；以实际为准，关键是 0 失败）。

- [ ] **步骤 2：seed 一个任务**

```bash
~/dc-remote.sh home build >/dev/null && ~/dc-remote.sh home run
~/dc-remote.sh home ui click 采集任务
~/dc-remote.sh home ui click 新建           # 打开新建任务弹窗
~/dc-remote.sh home ui set TaskServer 'opc.tcp://localhost:4840'
~/dc-remote.sh home ui click TaskSave        # 保存任务（默认 Ua、其余默认值合法）
```
预期：采集任务列表出现一个任务。

- [ ] **步骤 3：截图「新建分组」弹窗——应无任务下拉、标题带任务名**

```bash
# 选中该任务 → 切到分组 tab → 新建分组
~/dc-remote.sh home ui click 分组            # 切到分组 tab（先确保任务被选中；必要时先点列表里的任务行）
~/dc-remote.sh home ui click 新建
~/dc-remote.sh home shot                     # Read /tmp/dc-home-screen.png
```
预期截图：弹窗标题"新建分组 · 任务：opc.tcp://localhost:4840"（即 Server），**无"所属任务"下拉**，只有"名称"输入。把所见写进汇报。

> 若 `ui click 分组`/`新建` 因控件名歧义点不准，用 `ui tree` 查实际可点文本，或先 `ui click` 任务列表行选中任务。截图能体现"无任务下拉 + 标题带任务"即可。

- [ ] **步骤 4：截图「新建 Tag」弹窗——应无分组下拉、标题带分组名**

```bash
# 先填名称保存一个分组，再选中它（自动进 Tag tab），新建 Tag
~/dc-remote.sh home ui set <分组名称框> '温度组'   # 用 ui tree 找名称框；或 desktop 通道
~/dc-remote.sh home ui click 保存
# 选中分组行 → 自动切到 Tag tab → 新建 Tag
~/dc-remote.sh home ui click 新建
~/dc-remote.sh home shot                     # Read
```
预期截图：弹窗标题"新建 Tag · 分组：温度组"，**无"所属分组"下拉**，只有 Item/数据类型。把所见写进汇报。

> 此步交互较多，若 UIA 点击受限，可改用 `~/dc-remote.sh home desktop '<powershell>'` 直接驱动，或最低限度验证到「新建分组弹窗无任务下拉」即可，Tag 侧以构建+单测为准、截图尽力而为。

- [ ] **步骤 5：清场 + 最终 Commit（如截图过程有微调）**

```bash
~/dc-remote.sh home stop
git add -A
git commit -m "✅ test(ui): 分组/Tag 编辑器隐藏归属选择器截图复核" --allow-empty
```

---

## 自检结论

- **规格覆盖**：分组编辑器（任务1/2）、Tag 编辑器（任务3/4）、隐藏选择器+标题+行高（各任务步骤）、Tag 无上下文回退（任务3 `_group=defaultGroup` + ShowGroupSelector）、测试（任务1/3 单测）、截图复核（任务5）——规格各节均有对应任务。
- **关键细化**：`ShowTaskSelector`/`ShowGroupSelector` 用**ctor 固定的只读属性**（非 `=> Task is null` 计算式），避免无上下文时用户在选择器里选了归属后选择器反而消失。
- **类型一致**：`ShowTaskSelector`/`ShowGroupSelector`/`Title`/`ToEntity().TaskId`/`.GroupId`、`CollectorTask.Server`、`Group.Name`、`BoolToVis` 全程一致。
- **占位符**：无 TODO/待定；截图步骤标注了 UIA 点击受限时的退路（构建+单测为主、截图尽力）。
