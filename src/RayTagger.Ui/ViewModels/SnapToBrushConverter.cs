using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// Renders a BPM cell's text in dark red when the value was snapped (rounded by the analyzer's
/// snap-to-integer post-step). Returns <see cref="AvaloniaProperty.UnsetValue"/> for the false
/// case so the TextBlock falls back to the theme's default foreground — returning a plain
/// <c>null</c> there causes Avalonia to actually paint the text with a null brush (invisible).
/// </summary>
public sealed class SnapToBrushConverter : IValueConverter
{
    public static readonly SnapToBrushConverter Instance = new();

    /// <summary>Crimson-ish red — readable on both light and dark Fluent backgrounds.</summary>
    private static readonly IBrush SnappedBrush = new SolidColorBrush(Color.FromRgb(170, 30, 30));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? SnappedBrush : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
