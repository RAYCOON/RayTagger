using TagLib;
using XiphComment = TagLib.Ogg.XiphComment;

namespace RayTagger.Metadata;

/// <summary>
/// Reads the standard "Comment" tag-frame (ID3v2 <c>COMM</c> on MP3/AIFF, Vorbis
/// <c>COMMENT</c> on FLAC). Lives next to the full <see cref="TagLibTagReader"/> as a
/// separate utility because the comment isn't part of <c>TrackTags</c>'s logical fields —
/// it's only needed by the backtest harness to extract Mixed-In-Key ground-truth.
/// </summary>
public static class CommentTagReader
{
    /// <summary>
    /// Reads the first non-empty comment string from <paramref name="filePath"/>. Returns null
    /// when the file has no comment frame, the file is unreadable, or all comment frames are
    /// empty. Read-only — does not mutate the file.
    /// </summary>
    public static string? Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            // TagLib# unifies the comment across formats via Tag.Comment. For ID3v2 it picks the
            // first COMM frame; for Vorbis the first COMMENT field. That's what we want here —
            // Mixed-In-Key writes a single comment per track.
            var unified = tagFile.Tag.Comment;
            if (!string.IsNullOrWhiteSpace(unified))
            {
                return unified;
            }

            // Some FLAC writers leak the comment into a frame that doesn't surface via
            // Tag.Comment — fall back to a direct Vorbis read for that case.
            if (tagFile.GetTag(TagTypes.Xiph, create: false) is XiphComment xiph)
            {
                var vorbis = Internal.VorbisCommentAccessor.GetField(xiph, "COMMENT");
                if (!string.IsNullOrWhiteSpace(vorbis))
                {
                    return vorbis;
                }
            }
            return null;
        }
        catch (CorruptFileException)
        {
            return null;
        }
        catch (UnsupportedFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads raw BPM and key-string from the file's primary tag frames (TBPM / TKEY on ID3v2,
    /// BPM / INITIALKEY on Vorbis). Used by the backtest harness for secondary-truth roots
    /// (e.g. Virtual DJ output) where the per-track metadata sits in tag frames rather than
    /// a Mixed-In-Key comment string. Returns (null, null) for any read failure.
    /// </summary>
    public static (double? Bpm, string? Key) ReadBpmKey(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;
            double? bpm = tag.BeatsPerMinute > 0 ? tag.BeatsPerMinute : null;
            var key = string.IsNullOrWhiteSpace(tag.InitialKey) ? null : tag.InitialKey;
            return (bpm, key);
        }
        catch (CorruptFileException) { return (null, null); }
        catch (UnsupportedFormatException) { return (null, null); }
        catch (IOException) { return (null, null); }
    }
}
