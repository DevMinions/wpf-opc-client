using Microsoft.Extensions.DependencyInjection;

namespace Dc.App.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _sp;
    private readonly Dictionary<string, NavigationRoute> _byKey;

    public NavigationService(
        IServiceProvider serviceProvider,
        IReadOnlyList<NavigationRoute> routes,
        NavigationRoute? footerAbout)
    {
        _sp = serviceProvider;
        Routes = routes;
        FooterAbout = footerAbout;
        _byKey = routes.ToDictionary(r => r.Key, StringComparer.Ordinal);
        if (footerAbout is not null) _byKey.Add(footerAbout.Key, footerAbout);
    }

    public IReadOnlyList<NavigationRoute> Routes { get; }
    public NavigationRoute? FooterAbout { get; }

    public object Resolve(string routeKey)
    {
        if (!_byKey.TryGetValue(routeKey, out var route))
            throw new KeyNotFoundException($"未注册的导航 key: {routeKey}");
        return _sp.GetRequiredService(route.ViewModelType);
    }
}
