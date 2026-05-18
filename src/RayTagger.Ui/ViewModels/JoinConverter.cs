using System.Globalization;
using Avalonia.Data.Converters;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// Joins an <see cref="IEnumerable{T}"/> of strings with ", " for grid-cell display. Avalonia's
/// MultiBinding StringFormat doesn't iterate collections, and a dedicated converter is cheaper
/// than wrapping every row VM in a derived "RulesAsString" property.
/// </summary>
public sealed class JoinConverter : IValueConverter
{
    public static JoinConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable<string> seq)
        {
            return string.Join(", ", seq);
        }
        return value?.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
