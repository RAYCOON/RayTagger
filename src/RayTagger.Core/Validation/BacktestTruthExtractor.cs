namespace RayTagger.Core.Validation;

/// <summary>
/// Builds a <see cref="BacktestTruth"/> from a file's path relative to the reference root plus its
/// comment-tag string. Pure function — IO (file enumeration, comment reading) lives in the CLI
/// handler so this stays unit-testable without touching disk.
/// </summary>
public static class BacktestTruthExtractor
{
    /// <summary>
    /// Extracts truth from a file path under the reference root and the file's comment string.
    /// The first path segment under <paramref name="referenceRoot"/> is the genre subfolder;
    /// an optional second segment is the subgenre. The comment is parsed via
    /// <see cref="MixedInKeyCommentParser"/> and BPM-corrected against the genre subfolder.
    /// </summary>
    /// <param name="referenceRoot">Absolute path to <c>./music/Tagged/</c>.</param>
    /// <param name="filePath">Absolute path to a track file under the reference root.</param>
    /// <param name="comment">Comment tag string read from the file (may be null).</param>
    public static BacktestTruth Extract(string referenceRoot, string filePath, string? comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var (genre, subgenre) = ExtractGenreFromPath(referenceRoot, filePath);

        MixedInKeyTruth? mik = null;
        if (!string.IsNullOrWhiteSpace(comment))
        {
            var parsed = MixedInKeyCommentParser.TryParseComment(comment);
            if (parsed is not null)
            {
                mik = MixedInKeyCommentParser.ApplyGenreCorrection(parsed, genre);
            }
        }

        return new BacktestTruth(
            FilePath: filePath,
            Genre: genre,
            SubGenre: subgenre,
            Bpm: mik?.Bpm,
            CamelotKey: mik?.CamelotKey,
            Energy: mik?.Energy,
            BpmWasCorrected: mik?.BpmWasCorrected ?? false);
    }

    /// <summary>
    /// Splits the relative path under the reference root into (genre, subgenre?). When the file
    /// sits directly under the reference root with no subfolder, returns ("", null) — the caller
    /// should skip those files since the backtest needs a genre anchor.
    /// </summary>
    private static (string Genre, string? SubGenre) ExtractGenreFromPath(string referenceRoot, string filePath)
    {
        var normalizedRoot = Path.GetFullPath(referenceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFile = Path.GetFullPath(filePath);

        // Defensive guard — caller is expected to enumerate from referenceRoot, so files should
        // always be under it. If not, return empty genre and let the metric engine skip.
        if (!normalizedFile.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return (string.Empty, null);
        }

        var relative = normalizedFile[(normalizedRoot.Length + 1)..];
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // segments: [Genre, SubGenre?, ..., file.ext]
        if (segments.Length < 2)
        {
            // File sits directly under reference root → no genre anchor.
            return (string.Empty, null);
        }

        var genre = segments[0];
        string? subgenre = segments.Length >= 3 ? segments[1] : null;
        return (genre, subgenre);
    }
}
