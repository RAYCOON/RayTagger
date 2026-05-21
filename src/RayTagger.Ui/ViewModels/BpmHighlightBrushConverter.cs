using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// Renders a BPM cell's text foreground per the analyzer's diagnostic flags. Priority:
/// <list type="bullet">
///   <item>Forced-fallback (genre-range fold failed) → dark blue — the configured range and the
///         detected tempo disagree, and the written value is <c>snap(raw)</c>.</item>
///   <item>Snapped (drift rounded to grid) → dark red — the value was corrected, not verbatim.</item>
///   <item>Otherwise → <see cref="AvaloniaProperty.UnsetValue"/> so the TextBlock inherits the
///         theme's default foreground. (Returning <c>null</c> would paint with a null brush =
///         invisible text.)</item>
/// </list>
/// Two booleans in, one brush out — use as an <see cref="IMultiValueConverter"/> from a XAML
/// <c>MultiBinding</c>. First binding: <c>BpmIsForcedFallback</c>; second: <c>BpmWasSnapped</c>.
/// </summary>
public sealed class BpmHighlightBrushConverter : IMultiValueConverter
{
    public static readonly BpmHighlightBrushConverter Instance = new();

    /// <summary>Crimson-ish red — readable on both light and dark Fluent backgrounds.</summary>
    private static readonly IBrush SnappedBrush = new SolidColorBrush(Color.FromRgb(170, 30, 30));

    /// <summary>Deep blue with enough contrast for both Fluent themes.</summary>
    private static readonly IBrush ForcedFallbackBrush = new SolidColorBrush(Color.FromRgb(20, 60, 180));

    public object? Convert(IList<object?> values, System.Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);

        // MultiBinding hands a fresh list each tick; defensive bounds-check protects us if XAML
        // is ever rebound with a different shape.
        var forced = values.Count > 0 && values[0] is true;
        var snapped = values.Count > 1 && values[1] is true;

        if (forced) return ForcedFallbackBrush;
        if (snapped) return SnappedBrush;
        return AvaloniaProperty.UnsetValue;
    }
}
