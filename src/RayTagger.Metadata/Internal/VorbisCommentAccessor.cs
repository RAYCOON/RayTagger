using TagLib.Ogg;

namespace RayTagger.Metadata.Internal;

/// <summary>Helpers over TagLib#'s Vorbis-comment API (FLAC's native tagging container).</summary>
internal static class VorbisCommentAccessor
{
    /// <summary>Returns the first value of a Vorbis comment field, or null if absent.</summary>
    public static string? GetField(XiphComment xiph, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(xiph);
        ArgumentNullException.ThrowIfNull(fieldName);

        var values = xiph.GetField(fieldName);
        return values is { Length: > 0 } ? values[0] : null;
    }

    /// <summary>Enumerates every field name on the Vorbis comment block (single-valued).</summary>
    public static IEnumerable<KeyValuePair<string, string>> EnumerateFields(XiphComment xiph)
    {
        ArgumentNullException.ThrowIfNull(xiph);

        foreach (var name in xiph)
        {
            var values = xiph.GetField(name);
            if (values is { Length: > 0 })
            {
                yield return new KeyValuePair<string, string>(name, values[0]);
            }
        }
    }
}
