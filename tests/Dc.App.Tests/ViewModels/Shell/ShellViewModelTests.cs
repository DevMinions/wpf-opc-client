using Dc.App.Navigation;
using Dc.App.Services.Theme;
using Dc.App.ViewModels.Shell;

namespace Dc.App.Tests.ViewModels.Shell;

public class ShellViewModelTests
{
    private sealed class FakeDashboardVm { }
    private sealed class FakeTasksVm { }

    private static (Mock<INavigationService> nav, Mock<IThemeService> theme, ShellViewModel vm) Build()
    {
        var dashRoute = new NavigationRoute("dashboard", "仪表盘", "Home24", typeof(FakeDashboardVm));
        var tasksRoute = new NavigationRoute("workspace", "采集任务", "TaskListSquareLtr24", typeof(FakeTasksVm), GroupHeader: "采集");

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.Routes).Returns(new[] { dashRoute, tasksRoute });
        nav.SetupGet(n => n.FooterAbout).Returns((NavigationRoute?)null);
        nav.Setup(n => n.Resolve("dashboard")).Returns(new FakeDashboardVm());
        nav.Setup(n => n.Resolve("workspace")).Returns(new FakeTasksVm());

        var theme = new Mock<IThemeService>();
        theme.SetupGet(t => t.Current).Returns(AppTheme.System);

        var vm = new ShellViewModel(nav.Object, theme.Object);
        return (nav, theme, vm);
    }

    [Fact]
    public void Initial_SelectsFirstRouteAndResolvesContent()
    {
        var (nav, _, vm) = Build();

        Assert.Equal("dashboard", vm.SelectedRouteKey);
        Assert.IsType<ShellViewModelTests.FakeDashboardVm>(vm.CurrentContent);
        nav.Verify(n => n.Resolve("dashboard"), Times.Once);
    }

    [Fact]
    public void Navigate_UpdatesSelectedRouteAndContent()
    {
        var (nav, _, vm) = Build();

        vm.NavigateCommand.Execute("workspace");

        Assert.Equal("workspace", vm.SelectedRouteKey);
        Assert.IsType<ShellViewModelTests.FakeTasksVm>(vm.CurrentContent);
        nav.Verify(n => n.Resolve("workspace"), Times.Once);
    }

    [Fact]
    public void Navigate_SameRoute_DoesNotReResolve()
    {
        var (nav, _, vm) = Build();
        nav.Invocations.Clear();

        vm.NavigateCommand.Execute("dashboard");

        nav.Verify(n => n.Resolve(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Navigate_UnknownKey_LeavesStateUnchanged()
    {
        var (nav, _, vm) = Build();
        nav.Setup(n => n.Resolve("ghost")).Throws<KeyNotFoundException>();

        vm.NavigateCommand.Execute("ghost");

        Assert.Equal("dashboard", vm.SelectedRouteKey);
        Assert.IsType<ShellViewModelTests.FakeDashboardVm>(vm.CurrentContent);
    }

    [Fact]
    public void ToggleTheme_CyclesLight_Dark_System()
    {
        var (_, theme, vm) = Build();
        theme.SetupGet(t => t.Current).Returns(AppTheme.Light);

        vm.ToggleThemeCommand.Execute(null);
        theme.Verify(t => t.Apply(AppTheme.Dark), Times.Once);

        theme.SetupGet(t => t.Current).Returns(AppTheme.Dark);
        vm.ToggleThemeCommand.Execute(null);
        theme.Verify(t => t.Apply(AppTheme.System), Times.Once);

        theme.SetupGet(t => t.Current).Returns(AppTheme.System);
        vm.ToggleThemeCommand.Execute(null);
        theme.Verify(t => t.Apply(AppTheme.Light), Times.Once);
    }
}
