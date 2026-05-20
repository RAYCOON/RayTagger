using RayTagger.Core.Models;
using RayTagger.Metadata.Internal;
using TagLib;
using FsFile = System.IO.File;
using XiphComment = TagLib.Ogg.XiphComment;

namespace RayTagger.Metadata;

/// <summary>
/// Default <see cref="ITagReader"/> implementation backed by TagLib#. Stateless and thread-safe —
/// each call opens its own <c>TagLib.File</c>.
/// </summary>
public sealed class TagLibTagReader : ITagReader
{
    public TrackTags Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!FsFile.Exists(path))
        {
            throw new MetadataException($"File not found: {path}", path);
        }

        TagLib.File tagFile;
        try
        {
            tagFile = TagLib.File.Create(path);
        }
        catch (Exception ex) when (ex is CorruptFileException or UnsupportedFormatException)
        {
            throw new MetadataException($"Unreadable tag data in {path}: {ex.Message}", path, ex);
        }

        try
        {
            return Map(tagFile);
        }
        finally
        {
            tagFile.Dispose();
        }
    }

    /// <summary>
    /// Internal mapper exposed so tests can drive it with an in-memory <see cref="TagLib.File"/>
    /// constructed via an <see cref="TagLib.File.IFileAbstraction"/>.
    /// </summary>
    internal static TrackTags Map(TagLib.File tagFile)
    {
        ArgumentNullException.ThrowIfNull(tagFile);

        var common = tagFile.Tag;

        var id3 = tagFile.GetTag(TagTypes.Id3v2, create: false) as TagLib.Id3v2.Tag;
        var xiph = tagFile.GetTag(TagTypes.Xiph, create: false) as XiphComment;

        var camelot = id3 is not null
            ? Id3v2FrameAccessor.GetUserText(id3, TaggerTxxxFrames.CamelotKey)
            : xiph is not null
                ? VorbisCommentAccessor.GetField(xiph, TaggerTxxxFrames.CamelotKey)
                : null;

        var energyText = id3 is not null
            ? Id3v2FrameAccessor.GetUserText(id3, TaggerTxxxFrames.EnergyLevel)
            : xiph is not null
                ? VorbisCommentAccessor.GetField(xiph, TaggerTxxxFrames.EnergyLevel)
                : null;

        var subGenre = id3 is not null
            ? Id3v2FrameAccessor.GetUserText(id3, TaggerTxxxFrames.SubGenre)
            : xiph is not null
                ? VorbisCommentAccessor.GetField(xiph, TaggerTxxxFrames.SubGenre)
                : null;

        var mood = id3 is not null
            ? Id3v2FrameAccessor.GetUserText(id3, TaggerTxxxFrames.Mood)
            : xiph is not null
                ? VorbisCommentAccessor.GetField(xiph, TaggerTxxxFrames.Mood)
                : null;

        var setPosition = id3 is not null
            ? Id3v2FrameAccessor.GetUserText(id3, TaggerTxxxFrames.SetPosition)
            : xiph is not null
                ? VorbisCommentAccessor.GetField(xiph, TaggerTxxxFrames.SetPosition)
                : null;

        // Standard-notation key. TagLib# Tag.InitialKey only maps to Vorbis INITIALKEY (canonical),
        // but plenty of FLAC writers (Mixed In Key, Mp3Tag's default) emit just KEY. Read both
        // and prefer the standard name when present.
        var standardKeyText = id3 is not null
            ? Id3v2FrameAccessor.GetText(id3, "TKEY")
            : xiph is not null
                ? VorbisCommentAccessor.GetField(xiph, "INITIALKEY") ?? VorbisCommentAccessor.GetField(xiph, "KEY")
                : null;
        // Fallback to common.InitialKey for formats this code doesn't special-case (rare; here
        // mostly for forward compatibility).
        standardKeyText ??= NullIfEmpty(common.InitialKey);
        var musicalKey = KeyNotationConverter.FromEither(standardKeyText, camelot);

        // BPM. Same story as the key: TagLib#'s Tag.BeatsPerMinute is a uint that mis-parses
        // decimal BPM strings ("140.00" → 14000). Read the raw frame string and parse with
        // InvariantCulture so "140.00" / "94.92" / "173.4819" all come back correct.
        var bpmText = id3 is not null
            ? Id3v2FrameAccessor.GetText(id3, "TBPM")
            : xiph is not null
                ? VorbisCommentAccessor.GetField(xiph, "BPM")
                : null;
        var bpm = ParseBpm(bpmText)
            ?? (common.BeatsPerMinute > 0 ? (double?)common.BeatsPerMinute : null);

        // TagLib# exposes container-decoded duration on Properties (null if the file is corrupt
        // or the codec wasn't recognised). AcoustID's lookup endpoint refuses to answer without
        // it, so wiring duration through closes the AcoustID → MusicBrainz handshake.
        var duration = tagFile.Properties?.Duration ?? TimeSpan.Zero;
        var durationSeconds = duration > TimeSpan.Zero
            ? (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero)
            : (int?)null;

        return new TrackTags(
            Title: NullIfEmpty(common.Title),
            Artist: NullIfEmpty(common.FirstPerformer),
            AlbumArtist: NullIfEmpty(common.FirstAlbumArtist),
            Album: NullIfEmpty(common.Album),
            Year: common.Year > 0 ? (int)common.Year : null,
            Genre: NullIfEmpty(common.FirstGenre),
            SubGenre: NullIfEmpty(subGenre),
            Bpm: bpm,
            Key: musicalKey,
            Energy: ParseEnergy(energyText),
            Mood: NullIfEmpty(mood),
            SetPosition: NullIfEmpty(setPosition),
            DurationSeconds: durationSeconds,
            Custom: ExtractUnknownFrames(id3, xiph));
    }

    private static int? ParseEnergy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var n) &&
               n is >= 1 and <= 10
            ? n
            : null;
    }

    /// <summary>
    /// Parses a raw BPM string (TBPM frame value or Vorbis BPM field) with InvariantCulture so
    /// "140.00", "94.92" and "173.4819" all round-trip correctly. Returns null for blanks, junk,
    /// zero, or negative values — the caller falls back to TagLib#'s uint getter only when the
    /// raw frame is absent.
    /// </summary>
    /// <remarks>
    /// Exposed as internal (not private) so the test suite can pin the regression:
    /// TagLib#'s <c>Tag.BeatsPerMinute</c> getter mis-reads "140.00" as 14000, and we have to
    /// guarantee our replacement keeps doing the right thing across future TagLib# versions.
    /// </remarks>
    internal static double? ParseBpm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // NumberStyles.Float = Integer | DecimalPoint | Exponent + leading/trailing white + sign.
        // Deliberately NO thousands separator: "140.00" must stay 140.0, not become 14000 — which
        // is exactly the bug we're working around in TagLib# itself.
        return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var n)
               && n > 0
            ? n
            : null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Captures any TXXX/Vorbis fields that aren't part of our logical mapping, so a round-trip
    /// write preserves the user's existing custom tags (ReplayGain, MusicBrainz IDs, etc.).
    /// </summary>
    private static Dictionary<string, string> ExtractUnknownFrames(TagLib.Id3v2.Tag? id3, XiphComment? xiph)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TaggerTxxxFrames.CamelotKey,
            TaggerTxxxFrames.EnergyLevel,
            TaggerTxxxFrames.SubGenre,
            TaggerTxxxFrames.Mood,
            TaggerTxxxFrames.SetPosition,
        };

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (id3 is not null)
        {
            foreach (var kvp in Id3v2FrameAccessor.EnumerateUserText(id3))
            {
                if (!known.Contains(kvp.Key))
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
        }
        if (xiph is not null)
        {
            // Vorbis fields we already capture in typed properties (GENRE, ARTIST, etc.)
            // shouldn't leak into Custom. Block the standard set.
            var standardFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "TITLE", "ARTIST", "ALBUMARTIST", "ALBUM", "DATE", "GENRE",
                "BPM", "INITIALKEY",
                TaggerTxxxFrames.CamelotKey, TaggerTxxxFrames.EnergyLevel, TaggerTxxxFrames.SubGenre,
                TaggerTxxxFrames.Mood, TaggerTxxxFrames.SetPosition,
            };
            foreach (var kvp in VorbisCommentAccessor.EnumerateFields(xiph))
            {
                if (!standardFields.Contains(kvp.Key))
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
        }
        return result;
    }
}
