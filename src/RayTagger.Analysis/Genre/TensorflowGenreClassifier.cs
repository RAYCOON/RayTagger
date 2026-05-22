using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RayTagger.Analysis.Internal;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Genre;

/// <summary>
/// Invokes the Python TF-classifier bridge (<c>tools/raytagger-genre-classifier/</c>) for one
/// track and converts its JSON output into <see cref="GenreCandidate"/>s. One instance per
/// enabled model (electronic / jamendo / discogs-effnet) — model-specific values (label-map,
/// per-model min-confidence, top-k, aggregation settings) are captured in the constructor.
/// See <c>docs/PLAN_GENRE_CLASSIFICATION.md §4.2</c>.
/// </summary>
/// <remarks>
/// <para>
/// Label routing for discogs-effnet: the Python script emits labels in Discogs's
/// <c>Parent---Substyle</c> convention (e.g. <c>Electronic---Tech House</c>). After the §5.1a
/// normaliser converts <c>---</c> to a space, the resolver's whole-word search finds both the
/// parent genre (<c>House</c>) AND the substyle (<c>Tech</c>) naturally — no separate
/// <see cref="LookupResult.SubGenreCandidates"/> routing is required at the classifier layer.
/// </para>
/// <para>
/// Aggregation (§4.0c): when <see cref="AggregationConfig"/> is supplied, the classifier
/// additionally emits parent-genre-summed candidates IN ADDITION TO the raw top-K. These
/// aggregated candidates carry a <c>:aggregated</c> source suffix and resolve to a single
/// parent genre with confidence = sum-of-contributing-probabilities. Subgenre detection on
/// the aggregated value is suppressed (Value is the bare parent name) — disable aggregation
/// for that model if you need raw subgenre detection from discogs-effnet's 400-class output.
/// </para>
/// </remarks>
public sealed class TensorflowGenreClassifier : IGenreClassifier
{
    private readonly string _modelKey;
    private readonly string _pythonExecutable;
    private readonly string _scriptPath;
    private readonly string _modelsDirectory;
    private readonly double _minConfidence;
    private readonly int _topK;
    private readonly TimeSpan _timeout;
    private readonly AggregationConfig? _aggregation;
    private readonly INativeProcessRunner _runner;
    private readonly ILogger<TensorflowGenreClassifier> _logger;
    private readonly string _rawSource;
    private readonly string _aggregatedSource;
    private readonly string _aggregatedFallbackSource;

    /// <summary>
    /// Model key → display name. Used in <see cref="GenreCandidate.Source"/> and status logs.
    /// </summary>
    public static string NameFor(string modelKey) => $"essentia-tf-{modelKey}";

    public TensorflowGenreClassifier(
        string modelKey,
        string pythonExecutable,
        string scriptPath,
        string modelsDirectory,
        double minConfidence,
        int topK,
        TimeSpan timeout,
        INativeProcessRunner runner,
        ILogger<TensorflowGenreClassifier> logger,
        AggregationConfig? aggregation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);
        if (topK < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topK), topK, "top-k must be >= 1.");
        }

        _modelKey = modelKey;
        _pythonExecutable = pythonExecutable;
        _scriptPath = scriptPath;
        _modelsDirectory = modelsDirectory;
        _minConfidence = minConfidence;
        _topK = topK;
        _timeout = timeout;
        _aggregation = aggregation;
        _runner = runner;
        _logger = logger;
        _rawSource = $"classifier:{Name}";
        _aggregatedSource = $"classifier:{Name}:aggregated";
        _aggregatedFallbackSource = $"classifier:{Name}:aggregated-fallback";
    }

    public string Name => NameFor(_modelKey);

    public async Task<GenreClassificationResult> ClassifyAsync(TrackFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        var args = new List<string>
        {
            _scriptPath,
            "--model", _modelKey,
            "--audio", file.Path,
            "--models-dir", _modelsDirectory,
            "--top-k", _topK.ToString(CultureInfo.InvariantCulture),
        };

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_pythonExecutable, args, _timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning("TF classifier {Name} timed out for {Path}: {Message}", Name, file.Path, ex.Message);
            return GenreClassificationResult.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TF classifier {Name} failed to start for {Path}: {Message}", Name, file.Path, ex.Message);
            return GenreClassificationResult.Empty;
        }

        switch (result.ExitCode)
        {
            case 0:
                return ParseSuccessOutput(result.StandardOutput, file);
            case 2:
                _logger.LogWarning(
                    "TF classifier {Name}: model files missing for {Path}. Stderr: {Stderr}",
                    Name, file.Path, Truncate(result.StandardError));
                return GenreClassificationResult.Empty;
            case 3:
                _logger.LogWarning(
                    "TF classifier {Name}: audio unreadable for {Path}. Stderr: {Stderr}",
                    Name, file.Path, Truncate(result.StandardError));
                return GenreClassificationResult.Empty;
            default:
                _logger.LogWarning(
                    "TF classifier {Name} exited {ExitCode} for {Path}. Stderr: {Stderr}",
                    Name, result.ExitCode, file.Path, Truncate(result.StandardError));
                return GenreClassificationResult.Empty;
        }
    }

    /// <summary>
    /// Parses the Python script's stdout — one line of JSON of shape
    /// <c>{"model": "...", "predictions": [{"label": "...", "probability": ...}, ...]}</c>.
    /// All predictions feed the aggregation pipeline (§4.0c); only those above
    /// <see cref="_minConfidence"/> emit as raw candidates. Aggregated candidates (when
    /// configured) are prepended to the raw list so they win first-match-resolution.
    /// </summary>
    private GenreClassificationResult ParseSuccessOutput(string stdout, TrackFile file)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogWarning("TF classifier {Name}: empty stdout for {Path}.", Name, file.Path);
            return GenreClassificationResult.Empty;
        }

        // Defensive: the Python contract says "one line of JSON" but if TF ever leaks output
        // to stdout we want to find the JSON line. Scan from the end.
        var jsonLine = ExtractJsonLine(stdout);
        if (jsonLine is null)
        {
            _logger.LogWarning("TF classifier {Name}: no JSON line in stdout for {Path}.", Name, file.Path);
            return GenreClassificationResult.Empty;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonLine);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "TF classifier {Name}: invalid JSON for {Path}: {Json}", Name, file.Path, Truncate(jsonLine));
            return GenreClassificationResult.Empty;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("predictions", out var preds) || preds.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("TF classifier {Name}: missing 'predictions' array in {Path}'s output.", Name, file.Path);
                return GenreClassificationResult.Empty;
            }

            // Pass 1 — collect ALL normalised (label, probability) pairs. Aggregation works on
            // the FULL list (filtered only by per-candidate-floor), so min_confidence cannot
            // gate inputs to the aggregation sum.
            var allPredictions = new List<(string Label, double Probability)>();
            foreach (var pred in preds.EnumerateArray())
            {
                if (!TryReadPrediction(pred, out var label, out var probability))
                {
                    continue;
                }
                var normalised = ClassifierLabelNormaliser.Normalise(label);
                if (normalised.Length == 0)
                {
                    continue;
                }
                allPredictions.Add((normalised, probability));
            }

            // Pass 2 — aggregation (optional, §4.0c).
            var aggregated = ComputeAggregatedCandidates(allPredictions);

            // Pass 3 — raw candidates filtered by min_confidence (existing behaviour).
            var raw = new List<GenreCandidate>(allPredictions.Count);
            foreach (var (label, probability) in allPredictions)
            {
                if (probability < _minConfidence)
                {
                    continue;
                }
                raw.Add(new GenreCandidate(label, probability, _rawSource));
            }

            if (aggregated.Count == 0)
            {
                return new GenreClassificationResult(raw);
            }

            // Aggregated candidates ALWAYS first — their confidence is typically higher than
            // any single raw candidate, but we order explicitly rather than relying on sort
            // so the resolver sees aggregated → raw deterministically.
            var combined = new List<GenreCandidate>(aggregated.Count + raw.Count);
            combined.AddRange(aggregated);
            combined.AddRange(raw);
            return new GenreClassificationResult(combined);
        }
    }

    /// <summary>
    /// Sums prediction probabilities by their resolved taxonomy parent genre and emits one
    /// <see cref="GenreCandidate"/> per parent that clears <see cref="AggregationConfig.MinTotal"/>
    /// — or, when nothing clears the threshold and fallback is enabled, the single highest-sum
    /// parent with a distinct <c>:aggregated-fallback</c> source so the trace shows the
    /// uncertainty. See <c>docs/PLAN_GENRE_CLASSIFICATION.md §4.0c</c>.
    /// </summary>
    private List<GenreCandidate> ComputeAggregatedCandidates(IReadOnlyList<(string Label, double Probability)> predictions)
    {
        if (_aggregation is null)
        {
            return [];
        }

        var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, probability) in predictions)
        {
            if (probability < _aggregation.PerCandidateFloor)
            {
                continue;
            }
            var parent = _aggregation.Matcher.MatchParent(label);
            if (parent is null)
            {
                continue;
            }
            sums[parent] = sums.GetValueOrDefault(parent) + probability;
        }

        if (sums.Count == 0)
        {
            return [];
        }

        // Above-threshold parents emit as full "aggregated" candidates, sorted by sum desc.
        var above = sums
            .Where(kv => kv.Value >= _aggregation.MinTotal)
            .OrderByDescending(kv => kv.Value)
            .ToList();

        if (above.Count > 0)
        {
            return above
                .Select(kv => new GenreCandidate(
                    ClassifierLabelNormaliser.Normalise(kv.Key),
                    kv.Value,
                    _aggregatedSource))
                .ToList();
        }

        // Diffuse output: nothing cleared min_total. With fallback on, emit the argmax-sum
        // parent anyway, marked as fallback so the UI/trace can render the uncertainty.
        if (!_aggregation.FallbackOnDiffuse)
        {
            return [];
        }
        var (winnerGenre, winnerSum) = sums.OrderByDescending(kv => kv.Value).First();
        return
        [
            new GenreCandidate(
                ClassifierLabelNormaliser.Normalise(winnerGenre),
                winnerSum,
                _aggregatedFallbackSource),
        ];
    }

    private static bool TryReadPrediction(JsonElement pred, out string label, out double probability)
    {
        label = string.Empty;
        probability = 0;
        if (pred.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (!pred.TryGetProperty("label", out var labelElem) || labelElem.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        if (!pred.TryGetProperty("probability", out var probElem) || probElem.ValueKind != JsonValueKind.Number)
        {
            return false;
        }
        label = labelElem.GetString() ?? string.Empty;
        probability = probElem.GetDouble();
        return label.Length > 0;
    }

    /// <summary>
    /// Returns the JSON payload from <paramref name="stdout"/>. Common case: the entire stdout
    /// IS the JSON (the Python script emits one line — but it might be pretty-printed in tests
    /// or contain a trailing newline). Fallback: scan from the end for the last line that
    /// starts with <c>{</c> and ends with <c>}</c> — guards against stray log lines leaking
    /// into stdout (TF's libraries write to stderr in practice, but be defensive).
    /// </summary>
    private static string? ExtractJsonLine(string stdout)
    {
        var trimmed = stdout.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }
        var lines = stdout.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var lineTrimmed = lines[i].Trim();
            if (lineTrimmed.StartsWith('{') && lineTrimmed.EndsWith('}'))
            {
                return lineTrimmed;
            }
        }
        return null;
    }

    private static string Truncate(string s, int max = 500)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max] + "...";
    }
}

/// <summary>
/// Bundle of inputs the <see cref="TensorflowGenreClassifier"/> aggregation step (§4.0c) needs.
/// Constructed by <c>PipelineFactory</c> when
/// <c>TensorflowModelOptions.AggregateTopK</c> is true; <c>null</c> through the classifier
/// ctor means "no aggregation, emit raw top-K only" — matches the pre-B6.5 behaviour.
/// </summary>
public sealed record AggregationConfig(
    TaxonomyGenreMatcher Matcher,
    double PerCandidateFloor,
    double MinTotal,
    bool FallbackOnDiffuse);
