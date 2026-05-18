using RayTagger.Core.Models;
using TagLib;
using XiphComment = TagLib.Ogg.XiphComment;

namespace RayTagger.Metadata.Internal;

/// <summary>
/// Writes a logical tag field to the per-format frame(s) defined in the architecture matrix.
/// One static helper per logical dimension keeps the write-stage code declarative. All writes are
/// idempotent: passing <c>null</c> clears the frame.
/// </summary>
internal static class FrameMapper
{
    public static void WriteGenre(TagLib.File file, string? value)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.Tag.Genres = string.IsNullOrEmpty(value) ? [] : [value];
    }

    public static void WriteSubGenre(TagLib.File file, string? value) =>
        WriteUserDefined(file, TaggerTxxxFrames.SubGenre, value);

    public static void WriteBpm(TagLib.File file, double? value)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.Tag.BeatsPerMinute = value is > 0
            ? (uint)Math.Round(value.Value, MidpointRounding.AwayFromZero)
            : 0u;
    }

    public static void WriteStandardKey(TagLib.File file, string? value)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.Tag.InitialKey = string.IsNullOrEmpty(value) ? null : value;
    }

    public static void WriteCamelotKey(TagLib.File file, string? value) =>
        WriteUserDefined(file, TaggerTxxxFrames.CamelotKey, value);

    public static void WriteEnergy(TagLib.File file, int? value)
    {
        var text = value is >= 1 and <= 10
            ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        WriteUserDefined(file, TaggerTxxxFrames.EnergyLevel, text);
    }

    /// <summary>
    /// Writes a custom user-defined frame (anything from mapping rule <c>set: { tag.NAME: value }</c>
    /// or read from existing TXXX/Vorbis fields outside the logical field set). Description is the
    /// frame name as written; null/empty value removes the frame.
    /// </summary>
    public static void WriteCustomField(TagLib.File file, string fieldName, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        WriteUserDefined(file, fieldName, value);
    }

    /// <summary>
    /// Writes a value to ID3v2 <c>TXXX</c> (MP3/AIFF) or Vorbis comment (FLAC), depending on which
    /// tag container the file uses. Both branches are idempotent on <c>null</c>/empty input.
    /// </summary>
    private static void WriteUserDefined(TagLib.File file, string fieldName, string? value)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.GetTag(TagTypes.Id3v2, create: !string.IsNullOrEmpty(value)) is TagLib.Id3v2.Tag id3)
        {
            // SetUserTextAsString creates or updates the TXXX:<description> frame, or removes it
            // when the value is null. Don't use SetTextFrame here — that expects a 4-char frame
            // ID, not a TXXX-with-description.
            id3.SetUserTextAsString(fieldName, value);
        }

        if (file.GetTag(TagTypes.Xiph, create: !string.IsNullOrEmpty(value)) is XiphComment xiph)
        {
            if (string.IsNullOrEmpty(value))
            {
                xiph.RemoveField(fieldName);
            }
            else
            {
                xiph.SetField(fieldName, [value]);
            }
        }
    }
}
