using RayTagger.Core.Models;

namespace RayTagger.Metadata;

/// <summary>
/// Reads tag data from an audio file into a <see cref="TrackTags"/> snapshot. Implementations are
/// expected to be stateless and thread-safe per call — the pipeline creates one instance and
/// invokes <see cref="Read"/> concurrently from multiple files.
/// </summary>
public interface ITagReader
{
    /// <summary>
    /// Reads tags from <paramref name="path"/>. Returns a populated <see cref="TrackTags"/>; fields
    /// not present on the file are null. Throws <see cref="MetadataException"/> for unreadable
    /// files (corrupt header, unknown format, IO error).
    /// </summary>
    TrackTags Read(string path);
}
