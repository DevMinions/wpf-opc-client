using System;
using System.Windows.Data;
using System.Windows.Markup;
using Dc.App.Services.I18n;

namespace Dc.App.Markup;

// 用法: Text="{loc:Loc Settings_Title}"
// 绑定到 LocalizationManager 单例索引器;culture 变化时索引器发 PropertyChanged("Item[]") → 实时刷。
// Source 显式指向单例,故与 DataContext 无关 → ContextMenu/Tray 等独立可视化树也能用。
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
