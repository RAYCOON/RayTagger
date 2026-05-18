using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Indirection between Core's pipeline and the Metadata layer's tag I/O. Lets Core stay free of
/// the TagLib# dependency — the Metadata project provides the implementation, the host wires it up.
/// </summary>
public interface ITagReaderAdapter
{
    TrackTags Read(string path);
}

/// <summary>Companion to <see cref="ITagReaderAdapter"/> for writes.</summary>
public interface ITagWriterAdapter
{
    /// <summary>
    /// Writes <paramref name="resolved"/> to disk, honoring <see cref="WriteOptions.DryRun"/> and
    /// <see cref="WriteOptions.Backup"/>. Returns the number of fields actually written.
    /// </summary>
    PipelineWriteResult Write(string path, ResolvedTrackTags resolved, TaggerOptions options);
}

/// <summary>Lightweight result the pipeline cares about (full TagWriteResult lives in Metadata).</summary>
public sealed record PipelineWriteResult(IReadOnlyList<string> WrittenFields, string? BackupPath);
