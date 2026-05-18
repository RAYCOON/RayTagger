namespace RayTagger.Analysis;

/// <summary>
/// Wraps any failure during audio analysis (native binary missing, timed out, exit code != 0,
/// unparseable output, etc.) with enough context for the pipeline log. The pipeline catches this
/// per-file so a single bad analysis run doesn't abort the whole scan.
/// </summary>
public sealed class AnalysisException : Exception
{
    public string? Analyzer { get; }
    public string? FilePath { get; }

    public AnalysisException(string message, string? analyzer = null, string? filePath = null)
        : base(message)
    {
        Analyzer = analyzer;
        FilePath = filePath;
    }

    public AnalysisException(string message, Exception inner, string? analyzer = null, string? filePath = null)
        : base(message, inner)
    {
        Analyzer = analyzer;
        FilePath = filePath;
    }

    public AnalysisException()
    {
    }

    public AnalysisException(string message)
        : base(message)
    {
    }

    public AnalysisException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
