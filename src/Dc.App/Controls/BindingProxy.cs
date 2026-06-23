using System.Windows;

namespace Dc.App.Controls;

/// <summary>
/// 让不在可视树里的元素(典型:DataGridColumn)也能绑定到 DataContext 上的属性。
/// 用法:把它放进某元素 Resources,Data 绑 {Binding};需要处 Source={StaticResource 该key} 再取 Data.xxx。
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new PropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
