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

        var musicalKey = KeyNotationConverter.FromEither(common.InitialKey, camelot);

        return new TrackTags(
            Title: NullIfEmpty(common.Title),
            Artist: NullIfEmpty(common.FirstPerformer),
            AlbumArtist: NullIfEmpty(common.FirstAlbumArtist),
            Album: NullIfEmpty(common.Album),
            Year: common.Year > 0 ? (int)common.Year : null,
            Genre: NullIfEmpty(common.FirstGenre),
            SubGenre: NullIfEmpty(subGenre),
            Bpm: common.BeatsPerMinute > 0 ? common.BeatsPerMinute : null,
            Key: musicalKey,
            Energy: ParseEnergy(energyText),
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
