using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// Renders an <see cref="IEnumerable"/> as a comma-separated string for inline TextBlock display.
/// Used by AppliedRulesDialog's "API-Antworten" section to show provider-returned genre and
/// style lists without an extra ItemsControl per row (which would visually drown the dialog).
/// </summary>
public sealed class CommaListConverter : IValueConverter
{
    public static readonly CommaListConverter Instance = new();

    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable items) return AvaloniaProperty.UnsetValue;
        return string.Join(", ", items.Cast<object?>().Where(o => o is not null).Select(o => o!.ToString()));
    }

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
