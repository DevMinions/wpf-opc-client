using Dc.App.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace Dc.App.Tests.Navigation;

public class NavigationServiceTests
{
    private sealed class FakeVmA { }
    private sealed class FakeVmB { }

    private static (IServiceProvider sp, NavigationService nav) Build(params NavigationRoute[] routes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<FakeVmA>();
        services.AddSingleton<FakeVmB>();
        var sp = services.BuildServiceProvider();
        return (sp, new NavigationService(sp, routes, footerAbout: null));
    }

    [Fact]
    public void Routes_AreExposedInOrder()
    {
        var (_, nav) = Build(
            new NavigationRoute("a", "A", "Home24", typeof(FakeVmA)),
            new NavigationRoute("b", "B", "Home24", typeof(FakeVmB)));

        Assert.Collection(nav.Routes,
            r => Assert.Equal("a", r.Key),
            r => Assert.Equal("b", r.Key));
    }

    [Fact]
    public void Resolve_ReturnsRegisteredInstance()
    {
        var (sp, nav) = Build(
            new NavigationRoute("a", "A", "Home24", typeof(FakeVmA)));

        var result = nav.Resolve("a");

        Assert.Same(sp.GetRequiredService<FakeVmA>(), result);
    }

    [Fact]
    public void Resolve_UnknownKey_Throws()
    {
        var (_, nav) = Build(
            new NavigationRoute("a", "A", "Home24", typeof(FakeVmA)));

        Assert.Throws<KeyNotFoundException>(() => nav.Resolve("missing"));
    }
}
