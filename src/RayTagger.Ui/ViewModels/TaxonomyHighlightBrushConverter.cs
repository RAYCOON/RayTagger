using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// Renders a genre / sub-genre cell's text foreground in dark blue when the value isn't part of
/// the configured <c>taxonomy.yaml</c>. Mirrors the BPM cell's "forced-fallback" colour
/// (<see cref="BpmHighlightBrushConverter"/>) so the user has one consistent "you wrote
/// something off-taxonomy" visual cue across the grid.
/// </summary>
/// <remarks>
/// Single-value converter: bound to a <c>HasNonTaxonomy*</c> bool property on the row VM.
/// Returns <see cref="AvaloniaProperty.UnsetValue"/> for the in-taxonomy case so the TextBlock
/// inherits the theme's default foreground — returning <c>null</c> would paint invisible text.
/// </remarks>
public sealed class TaxonomyHighlightBrushConverter : IValueConverter
{
    public static readonly TaxonomyHighlightBrushConverter Instance = new();

    /// <summary>Same shade as BpmHighlightBrushConverter's forced-fallback colour for consistency.</summary>
    private static readonly IBrush NonTaxonomyBrush = new SolidColorBrush(Color.FromRgb(20, 60, 180));

    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is true ? NonTaxonomyBrush : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
