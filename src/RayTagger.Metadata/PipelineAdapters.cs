using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Metadata;

/// <summary>
/// Implements Core's <see cref="ITagReaderAdapter"/> by delegating to the Metadata-layer
/// <see cref="ITagReader"/>. The indirection lets Core stay free of the TagLib# dependency.
/// </summary>
public sealed class TagReaderAdapter : ITagReaderAdapter
{
    private readonly ITagReader _reader;

    public TagReaderAdapter(ITagReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public TrackTags Read(string path) => _reader.Read(path);
}

/// <summary>Companion writer adapter; maps the global <see cref="WriteOptions"/> to <see cref="TagWriteOptions"/>.</summary>
public sealed class TagWriterAdapter : ITagWriterAdapter
{
    private readonly ITagWriter _writer;

    public TagWriterAdapter(ITagWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public PipelineWriteResult Write(string path, ResolvedTrackTags resolved, TaggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var writeOpts = new TagWriteOptions(
            DryRun: options.Write.DryRun,
            Backup: options.Write.Backup,
            TagFieldMap: options.Write.TagFieldMap);

        var result = _writer.Write(path, resolved, writeOpts);
        return new PipelineWriteResult(result.WrittenFields, result.BackupPath);
    }
}
