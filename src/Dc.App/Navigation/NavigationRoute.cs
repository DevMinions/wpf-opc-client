namespace Dc.App.Navigation;

public sealed record NavigationRoute(
    string Key,           // "dashboard" / "workspace" / ...，与 NavigationViewItem.Tag 匹配
    string Title,         // 侧栏显示文本
    string Icon,          // SymbolRegular 名称（如 "Home24"）
    Type ViewModelType,   // 解析时去 IServiceProvider 拉
    string? GroupHeader = null   // 显示在该项前的分组标题；null = 紧贴上一项
);
