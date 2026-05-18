using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using TagLib;
using XiphComment = TagLib.Ogg.XiphComment;

namespace RayTagger.Metadata.Internal;

/// <summary>
/// Writes a logical tag field to the per-format frame(s) defined in the architecture matrix.
/// One static helper per logical dimension keeps the write-stage code declarative. All writes are
/// idempotent: passing <c>null</c> clears the frame.
/// </summary>
/// <remarks>
/// User-overridable frame names (TXXX descriptions, Vorbis field names) come from the loaded
/// <see cref="TagFieldMap"/>. Standard ID3v2 frames (TBPM, TKEY, TCON) and their Vorbis
/// equivalents are spec-fixed and not parameterised.
/// </remarks>
internal static class FrameMapper
{
    public static void WriteGenre(TagLib.File file, string? value)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.Tag.Genres = string.IsNullOrEmpty(value) ? [] : [value];
    }

    public static void WriteSubGenre(TagLib.File file, string? value, TagFieldMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        WriteUserDefined(file, map.SubGenreId3Description, map.SubGenreVorbisField, value);
    }

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

    public static void WriteCamelotKey(TagLib.File file, string? value, TagFieldMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        WriteUserDefined(file, map.CamelotKeyId3Description, map.CamelotKeyVorbisField, value);
    }

    public static void WriteEnergy(TagLib.File file, int? value, TagFieldMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var text = value is >= 1 and <= 10
            ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        WriteUserDefined(file, map.EnergyLevelId3Description, map.EnergyLevelVorbisField, text);
    }

    public static void WriteMood(TagLib.File file, string? value, TagFieldMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        WriteUserDefined(file, map.MoodId3Description, map.MoodVorbisField, value);
    }

    public static void WriteSetPosition(TagLib.File file, string? value, TagFieldMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        WriteUserDefined(file, map.SetPositionId3Description, map.SetPositionVorbisField, value);
    }

    /// <summary>
    /// Writes a custom user-defined frame (anything from mapping rule <c>set: { tag.NAME: value }</c>
    /// or read from existing TXXX/Vorbis fields outside the logical field set). Description is the
    /// frame name as written; null/empty value removes the frame.
    /// </summary>
    public static void WriteCustomField(TagLib.File file, string fieldName, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        WriteUserDefined(file, fieldName, fieldName, value);
    }

    /// <summary>
    /// Writes a value to ID3v2 <c>TXXX</c> (MP3/AIFF) under <paramref name="id3Description"/> AND
    /// to Vorbis comment (FLAC) under <paramref name="vorbisField"/>. The two names usually
    /// match, but the user can split them via <c>tag_fields</c> when their DJ tool wants
    /// different conventions per container.
    /// </summary>
    private static void WriteUserDefined(TagLib.File file, string id3Description, string vorbisField, string? value)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.GetTag(TagTypes.Id3v2, create: !string.IsNullOrEmpty(value)) is TagLib.Id3v2.Tag id3)
        {
            // SetUserTextAsString creates or updates the TXXX:<description> frame, or removes it
            // when the value is null. Don't use SetTextFrame here — that expects a 4-char frame
            // ID, not a TXXX-with-description.
            id3.SetUserTextAsString(id3Description, value);
        }

        if (file.GetTag(TagTypes.Xiph, create: !string.IsNullOrEmpty(value)) is XiphComment xiph)
        {
            if (string.IsNullOrEmpty(value))
            {
                xiph.RemoveField(vorbisField);
            }
            else
            {
                xiph.SetField(vorbisField, [value]);
            }
        }
    }
}
