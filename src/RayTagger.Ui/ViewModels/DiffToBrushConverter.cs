using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// Lights up a cell background when its "this field differs from the on-disk value" flag is true.
/// Used by the results grid to give the user a visual cue while typing rule changes — every cell
/// that flips between two evaluations highlights yellow so they can spot the impact at a glance.
/// </summary>
public sealed class DiffToBrushConverter : IValueConverter
{
    public static readonly DiffToBrushConverter Instance = new();

    /// <summary>Soft yellow that reads against both Fluent light and dark theme backgrounds.</summary>
    private static readonly IBrush DiffBrush = new SolidColorBrush(Color.FromArgb(64, 255, 204, 0));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? DiffBrush : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
