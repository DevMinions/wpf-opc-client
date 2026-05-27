using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Dc.App.Controls;

/// <summary>
/// Renders placeholder (watermark) text inside a TextBox when it is empty.
/// The brush is resolved at <see cref="RefreshAdorner"/> time so it follows
/// the current light/dark theme automatically.
/// </summary>
internal sealed class PlaceholderAdorner : Adorner
{
    private readonly string _text;
    private readonly Brush _brush;

    public PlaceholderAdorner(UIElement adornedElement, string text, Brush brush)
        : base(adornedElement)
    {
        _text = text;
        _brush = brush;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (AdornedElement is not TextBox tb) return;
        if (!string.IsNullOrEmpty(tb.Text)) return;

        var padding = tb.Padding;
        var fontSize = tb.FontSize > 0 ? tb.FontSize : SystemFonts.MessageFontSize;
        var fontFamily = tb.FontFamily ?? SystemFonts.MessageFontFamily;
        var typeface = new Typeface(fontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);

        // Measure the text to calculate true vertical centering
        var dpi = VisualTreeHelper.GetDpi(this);
        var formatted = new FormattedText(
            _text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            _brush,
            dpi.PixelsPerDip);

        // Horizontal: respect padding + small offset to align with typed text
        var x = padding.Left + 2;

        // Vertical: center the text baseline relative to the TextBox content area
        // Content area = RenderSize minus vertical padding; center the text height within it
        var contentHeight = RenderSize.Height - padding.Top - padding.Bottom;
        var y = padding.Top + (contentHeight - formatted.Height) / 2;

        dc.DrawText(formatted, new Point(x, y));
    }
}
