namespace RayTagger.Metadata;

/// <summary>
/// Wraps any failure during tag I/O (corrupt file, unknown format, locked file, etc.) with the
/// file path that caused it. The pipeline catches this per-file so a single bad file doesn't
/// abort an entire scan.
/// </summary>
public sealed class MetadataException : Exception
{
    public string? FilePath { get; }

    public MetadataException(string message, string? filePath = null)
        : base(message)
    {
        FilePath = filePath;
    }

    public MetadataException(string message, string? filePath, Exception inner)
        : base(message, inner)
    {
        FilePath = filePath;
    }

    public MetadataException()
    {
    }

    public MetadataException(string message)
        : base(message)
    {
    }

    public MetadataException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
