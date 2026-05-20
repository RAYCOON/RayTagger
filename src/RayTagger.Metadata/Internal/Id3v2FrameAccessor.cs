using TagLib.Id3v2;

namespace RayTagger.Metadata.Internal;

/// <summary>
/// Thin helpers over TagLib#'s ID3v2 API for the user-defined frames Tagger reads/writes
/// (<c>TXXX:CAMELOTKEY</c>, <c>TXXX:ENERGYLEVEL</c>, <c>TXXX:SUBGENRE</c>, …). MP3 and AIFF both
/// store tags in an ID3v2 chunk so they share this path.
/// </summary>
internal static class Id3v2FrameAccessor
{
    /// <summary>Reads the value of a <c>TXXX</c> frame by description, or null if absent.</summary>
    public static string? GetUserText(Tag id3, string description)
    {
        ArgumentNullException.ThrowIfNull(id3);
        ArgumentNullException.ThrowIfNull(description);

        foreach (var frame in id3.GetFrames<UserTextInformationFrame>())
        {
            if (string.Equals(frame.Description, description, StringComparison.OrdinalIgnoreCase) &&
                frame.Text is { Length: > 0 })
            {
                return frame.Text[0];
            }
        }
        return null;
    }

    /// <summary>
    /// Reads a standard ID3v2 text frame (TBPM, TKEY, TCON, …) as its raw string value. Used by
    /// the reader to bypass TagLib#'s built-in numeric parsers — its <c>Tag.BeatsPerMinute</c>
    /// getter, for example, mis-parses a TBPM frame like <c>"140.00"</c> as <c>14000</c>.
    /// </summary>
    public static string? GetText(Tag id3, string frameId)
    {
        ArgumentNullException.ThrowIfNull(id3);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);

        var frame = TextInformationFrame.Get(id3, frameId, create: false);
        var text = frame?.Text;
        if (text is { Length: > 0 } && !string.IsNullOrWhiteSpace(text[0]))
        {
            return text[0];
        }
        return null;
    }

    /// <summary>Enumerates every <c>TXXX</c> description/value pair on the tag.</summary>
    public static IEnumerable<KeyValuePair<string, string>> EnumerateUserText(Tag id3)
    {
        ArgumentNullException.ThrowIfNull(id3);

        foreach (var frame in id3.GetFrames<UserTextInformationFrame>())
        {
            if (frame.Description is { Length: > 0 } desc &&
                frame.Text is { Length: > 0 } text)
            {
                yield return new KeyValuePair<string, string>(desc, text[0]);
            }
        }
    }
}
