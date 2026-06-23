using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Dc.App.Controls;

/// <summary>
/// 可复用空状态：图标 + 标题 + 说明 + 可选主操作按钮。零业务逻辑。
/// 默认模板见 Theme/Tokens.xaml。ActionText 为空时按钮不显示。
/// </summary>
public sealed class EmptyState : Control
{
    static EmptyState()
        => DefaultStyleKeyProperty.OverrideMetadata(
            typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(EmptyState),
        new PropertyMetadata(SymbolRegular.Info24));
    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyState), new PropertyMetadata(null));
    public string? ActionText
    {
        get => (string?)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null));
    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    // 可选次操作（幽灵按钮）。SecondaryActionText 为空时不显示。
    public static readonly DependencyProperty SecondaryActionTextProperty = DependencyProperty.Register(
        nameof(SecondaryActionText), typeof(string), typeof(EmptyState), new PropertyMetadata(null));
    public string? SecondaryActionText
    {
        get => (string?)GetValue(SecondaryActionTextProperty);
        set => SetValue(SecondaryActionTextProperty, value);
    }

    public static readonly DependencyProperty SecondaryActionCommandProperty = DependencyProperty.Register(
        nameof(SecondaryActionCommand), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null));
    public ICommand? SecondaryActionCommand
    {
        get => (ICommand?)GetValue(SecondaryActionCommandProperty);
        set => SetValue(SecondaryActionCommandProperty, value);
    }
}
