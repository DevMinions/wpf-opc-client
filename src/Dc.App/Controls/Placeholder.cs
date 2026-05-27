using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Dc.App.Controls;

/// <summary>
/// Attached property that adds placeholder (watermark) text to a TextBox.
/// Theme-aware: resolves <c>TextFillColorTertiaryBrush</c> from the element's
/// resource chain so placeholder adapts to light/dark mode automatically.
/// </summary>
public static class Placeholder
{
    /// <summary>
    /// Fallback brush used when <c>TextFillColorTertiaryBrush</c> is not found
    /// in the resource chain (should not happen with wpfui loaded).
    /// </summary>
    private static readonly Brush FallbackBrush =
        new SolidColorBrush(Color.FromArgb(0xFF, 0xA0, 0xA0, 0xA0));

    static Placeholder()
    {
        FallbackBrush.Freeze();
    }

    // ─── Text ───────────────────────────────────────────────────

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(Placeholder),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    // ─── Brush (optional override) ──────────────────────────────

    /// <summary>
    /// Allows XAML to override the placeholder brush explicitly.
    /// If unset (null), the adorner resolves <c>TextFillColorTertiaryBrush</c>
    /// from the element's resources at render time.
    /// </summary>
    public static readonly DependencyProperty BrushProperty =
        DependencyProperty.RegisterAttached(
            "Brush",
            typeof(Brush),
            typeof(Placeholder),
            new PropertyMetadata(null));

    public static Brush GetBrush(DependencyObject d) => (Brush)d.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject d, Brush value) => d.SetValue(BrushProperty, value);

    // ─── Lifecycle ──────────────────────────────────────────────

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;

        tb.TextChanged -= OnBoxTextChanged;
        tb.TextChanged += OnBoxTextChanged;

        tb.GotFocus -= OnFocusChanged;
        tb.LostFocus -= OnFocusChanged;
        tb.GotFocus += OnFocusChanged;
        tb.LostFocus += OnFocusChanged;

        tb.Unloaded -= OnUnloaded;
        tb.Unloaded += OnUnloaded;

        RefreshAdorner(tb);
    }

    private static void OnBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb) RefreshAdorner(tb);
    }

    /// <summary>
    /// GotFocus / LostFocus: re-resolve the adorner so the brush
    /// picks up any theme change that happened while the box was unfocused.
    /// </summary>
    private static void OnFocusChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) RefreshAdorner(tb);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.TextChanged -= OnBoxTextChanged;
            tb.GotFocus -= OnFocusChanged;
            tb.LostFocus -= OnFocusChanged;
            tb.Unloaded -= OnUnloaded;
            RemoveAdorner(tb);
        }
    }

    // ─── Adorner management ─────────────────────────────────────

    private static void RefreshAdorner(TextBox tb)
    {
        RemoveAdorner(tb);

        var placeholder = GetText(tb);
        if (string.IsNullOrEmpty(placeholder)) return;
        if (!string.IsNullOrEmpty(tb.Text)) return;

        var brush = GetBrush(tb) ?? ResolveThemeBrush(tb);
        var adorner = new PlaceholderAdorner(tb, placeholder, brush);
        AdornerLayer.GetAdornerLayer(tb)?.Add(adorner);
    }

    private static void RemoveAdorner(TextBox tb)
    {
        var layer = AdornerLayer.GetAdornerLayer(tb);
        if (layer is null) return;

        var adorners = layer.GetAdorners(tb);
        if (adorners is null) return;

        foreach (var a in adorners)
        {
            if (a is PlaceholderAdorner)
            {
                layer.Remove(a);
            }
        }
    }

    /// <summary>
    /// Resolve the theme-aware placeholder brush from the wpfui resource chain.
    /// Falls back to <see cref="FallbackBrush"/> if not found.
    /// </summary>
    private static Brush ResolveThemeBrush(FrameworkElement fe)
    {
        // wpfui exposes TextFillColorTertiaryBrush — perfect for placeholder text
        if (fe.TryFindResource("TextFillColorTertiaryBrush") is Brush themeBrush)
            return themeBrush;

        // Walk up: try Application resources
        if (Application.Current?.TryFindResource("TextFillColorTertiaryBrush") is Brush appBrush)
            return appBrush;

        return FallbackBrush;
    }
}
