using System.Globalization;
using System.Text;
using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Renders a sort path from a template like <c>{genre}/{subgenre?}/{artist} - {title}.{ext}</c>.
/// Placeholders use <c>{name}</c> for required fields and <c>{name?}</c> for optional ones —
/// a path component that consists entirely of empty optional placeholders is dropped, so
/// <c>{genre}/{subgenre?}/{title}</c> collapses to <c>{genre}/{title}</c> when subgenre is null.
/// Reserved filesystem characters in substituted values are replaced with underscore.
/// </summary>
/// <remarks>
/// Supported placeholder names:
/// <list type="bullet">
///   <item><c>{genre}</c>, <c>{subgenre}</c> — from <see cref="ResolvedTrackTags"/></item>
///   <item><c>{artist}</c>, <c>{title}</c>, <c>{album}</c>, <c>{albumartist}</c>, <c>{year}</c> — from <see cref="TrackTags"/></item>
///   <item><c>{bpm}</c>, <c>{key}</c>, <c>{camelot}</c>, <c>{energy}</c> — from <see cref="ResolvedTrackTags"/></item>
///   <item><c>{ext}</c> — the source file's extension (without the leading dot)</item>
/// </list>
/// </remarks>
public static class SortTemplateEngine
{
    // ASCII characters disallowed on at least one major filesystem. We standardise on the union
    // so the same library tree is portable across macOS / Linux / Windows.
    private static readonly char[] ReservedChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];

    /// <summary>
    /// Renders <paramref name="template"/> for one track and returns a path RELATIVE to the sort
    /// destination root (caller combines with <c>sort.destination</c>). Throws
    /// <see cref="SortTemplateException"/> when a required placeholder is missing or unknown.
    /// </summary>
    public static string Render(string template, TrackTags existing, ResolvedTrackTags resolved, string sourceExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(resolved);

        var ctx = new TemplateContext(existing, resolved, sourceExtension);

        // Split on `/` only — backslash on Windows is a legal path separator in templates too but
        // less typical, and we'd rather force users to write Unix-style portable templates.
        var components = template.Split('/');
        var renderedComponents = new List<string>(components.Length);
        foreach (var component in components)
        {
            var rendered = RenderComponent(component, ctx);
            if (rendered is null) continue;  // entire component collapsed (all-optional, all-empty)
            renderedComponents.Add(rendered);
        }

        if (renderedComponents.Count == 0)
        {
            throw new SortTemplateException("Template rendered to an empty path — every component was optional and empty.");
        }

        return string.Join(Path.DirectorySeparatorChar, renderedComponents);
    }

    private static string? RenderComponent(string component, TemplateContext ctx)
    {
        var sb = new StringBuilder(component.Length);
        var i = 0;
        var hadAnyRequiredOutput = false;
        var hadAnyOptionalEmpty = false;

        while (i < component.Length)
        {
            var c = component[i];
            if (c == '{')
            {
                var close = component.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new SortTemplateException($"Unterminated placeholder in template component '{component}'.");
                }
                var raw = component[(i + 1)..close];
                var optional = raw.EndsWith('?');
                var name = optional ? raw[..^1] : raw;
                var value = LookupPlaceholder(name, ctx);

                if (string.IsNullOrEmpty(value))
                {
                    if (!optional)
                    {
                        throw new SortTemplateException($"Required placeholder '{{{name}}}' has no value for this track.");
                    }
                    hadAnyOptionalEmpty = true;
                }
                else
                {
                    sb.Append(SanitiseSegment(value));
                    hadAnyRequiredOutput = true;
                }
                i = close + 1;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        // A pure-optional component (e.g. "{subgenre?}") whose placeholder was empty produces an
        // empty string AND only literal whitespace/separators outside the braces — drop it.
        var rendered = sb.ToString();
        if (!hadAnyRequiredOutput && hadAnyOptionalEmpty && rendered.Trim(' ', '-', '_', '.').Length == 0)
        {
            return null;
        }

        return SanitiseSegment(rendered);
    }

    private static string? LookupPlaceholder(string name, TemplateContext ctx)
    {
        // Case-insensitive placeholder lookup — `{Genre}` and `{genre}` are equivalent. CA1308's
        // "prefer uppercase" advice is about security/sorting; for a switch over a known set of
        // ASCII placeholder names, lowercase keeps the table readable.
#pragma warning disable CA1308
        return name.ToLowerInvariant() switch
#pragma warning restore CA1308
        {
            "genre"       => ctx.Resolved.Genre.Value,
            "subgenre"    => ctx.Resolved.SubGenre.Value,
            "artist"      => ctx.Existing.Artist,
            "albumartist" => ctx.Existing.AlbumArtist,
            "album"       => ctx.Existing.Album,
            "title"       => ctx.Existing.Title,
            "year"        => ctx.Existing.Year?.ToString(CultureInfo.InvariantCulture),
            "bpm"         => ctx.Resolved.Bpm.Value?.ToString("0", CultureInfo.InvariantCulture),
            "key"         => ctx.Resolved.Key.Value?.Standard,
            "camelot"     => ctx.Resolved.Key.Value?.Camelot,
            "energy"      => ctx.Resolved.Energy.Value?.ToString(CultureInfo.InvariantCulture),
            "ext"         => ctx.SourceExtension,
            _ => throw new SortTemplateException($"Unknown placeholder '{{{name}}}'."),
        };
    }

    private static string SanitiseSegment(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(Array.IndexOf(ReservedChars, c) >= 0 ? '_' : c);
        }
        // Trim trailing dots and spaces — both are technically allowed by macOS/Linux but rejected
        // by Windows (and Explorer hides trailing dots silently).
        return sb.ToString().TrimEnd(' ', '.');
    }

    private sealed record TemplateContext(TrackTags Existing, ResolvedTrackTags Resolved, string SourceExtension);
}

/// <summary>Thrown when a sort template is malformed or references missing data.</summary>
public sealed class SortTemplateException : Exception
{
    public SortTemplateException(string message) : base(message) { }
    public SortTemplateException(string message, Exception inner) : base(message, inner) { }
    public SortTemplateException() { }
}
