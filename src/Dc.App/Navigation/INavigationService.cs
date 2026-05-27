namespace Dc.App.Navigation;

public interface INavigationService
{
    IReadOnlyList<NavigationRoute> Routes { get; }
    NavigationRoute? FooterAbout { get; }

    /// 按 key 拉 VM 实例。未注册的 key 抛 KeyNotFoundException。
    object Resolve(string routeKey);
}
