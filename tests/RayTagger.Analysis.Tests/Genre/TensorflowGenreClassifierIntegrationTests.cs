using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Analysis;
using RayTagger.Analysis.Genre;
using RayTagger.Analysis.Internal;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using Xunit.Abstractions;

namespace RayTagger.Analysis.Tests.Genre;

/// <summary>
/// End-to-end tests for <see cref="TensorflowGenreClassifier"/> against a real Python interpreter,
/// real Essentia TF models downloaded on first run, and real audio from <c>./music/</c>.
/// Silently skipped when any prerequisite is missing:
/// <list type="bullet">
///   <item>python3 not on PATH</item>
///   <item><c>essentia-tensorflow</c> not installed via pip</item>
///   <item><c>./music/</c> directory absent or empty (gitignored per CLAUDE.md)</item>
///   <item>model download failed (network outage)</item>
/// </list>
/// Skip reasons are emitted to xUnit's test output so a developer running locally with a
/// half-configured environment knows what's missing.
/// </summary>
/// <remarks>
/// Validates B6 explicitly: the §4.0a remap → §5.1a normaliser → <see cref="TaxonomyGenreResolver"/>
/// chain produces taxonomy-shaped output. Also captures wall-clock timings for the §4.0b
/// subprocess-cost estimate (~3 sec/track/model) so future regressions in cold-start cost surface.
/// </remarks>
[Trait("Category", "Integration")]
public class TensorflowGenreClassifierIntegrationTests
{
    private readonly ITestOutputHelper _out;

    public TensorflowGenreClassifierIntegrationTests(ITestOutputHelper output)
    {
        _out = output;
    }

    [Fact]
    public async Task Genre_electronic_classifies_a_real_track_end_to_end()
    {
        var env = await TfIntegrationEnvironment.PrepareAsync("electronic", _out);
        if (!env.IsReady)
        {
            _out.WriteLine($"SKIP: {env.SkipReason}");
            return;
        }

        var classifier = env.BuildClassifier("electronic", minConfidence: 0.0, topK: 5);
        var audio = env.PickAudio();

        var sw = Stopwatch.StartNew();
        var result = await classifier.ClassifyAsync(audio, CancellationToken.None);
        sw.Stop();

        _out.WriteLine($"electronic on {Path.GetFileName(audio.Path)}: {sw.ElapsedMilliseconds} ms");
        foreach (var c in result.Candidates)
        {
            _out.WriteLine($"  {c.Value,-30} p={c.Confidence:0.000}  source={c.Source}");
        }

        // 5-class model + min_confidence=0 ⇒ all 5 emit. Source matches per-model convention.
        result.Candidates.Should().HaveCount(5);
        result.Candidates.Select(c => c.Source).Should().AllSatisfy(s =>
            s.Should().Be("classifier:essentia-tf-electronic"));
        result.Candidates.Should().AllSatisfy(c => c.Confidence.Should().BeInRange(0, 1));
        result.Candidates.Should().BeInDescendingOrder(c => c.Confidence);

        // §4.0b sanity: a single inference shouldn't exceed 10s wall-clock on any reasonable
        // machine. If it does we want to know — the §4.0b estimate would be wildly off.
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000,
            because: "§4.0b estimates ~3 sec/track/model; >10s would invalidate that planning assumption");
    }

    [Fact]
    public async Task Discogs_effnet_candidates_resolve_through_taxonomy_end_to_end()
    {
        var env = await TfIntegrationEnvironment.PrepareAsync("discogs-effnet", _out);
        if (!env.IsReady)
        {
            _out.WriteLine($"SKIP: {env.SkipReason}");
            return;
        }

        // Build classifier WITH aggregation enabled — matches production defaults for
        // discogs-effnet (AggregateTopK=true). The taxonomy used for aggregation matching
        // is the same one fed to the resolver below, so we can validate the full chain:
        // model → normaliser → §4.0c aggregation → resolver → taxonomy match.
        var taxonomy = BuildIntegrationTaxonomy();
        var aggregation = new AggregationConfig(
            Matcher: new TaxonomyGenreMatcher(taxonomy),
            PerCandidateFloor: 0.02,
            MinTotal: 0.25,
            FallbackOnDiffuse: true);
        var classifier = env.BuildClassifier("discogs-effnet", minConfidence: 0.0, topK: 10, aggregation: aggregation);
        var audio = env.PickAudio();

        var sw = Stopwatch.StartNew();
        var result = await classifier.ClassifyAsync(audio, CancellationToken.None);
        sw.Stop();

        _out.WriteLine($"discogs-effnet on {Path.GetFileName(audio.Path)}: {sw.ElapsedMilliseconds} ms");
        foreach (var c in result.Candidates)
        {
            _out.WriteLine($"  {c.Value,-50} p={c.Confidence:0.000}  [{c.Source}]");
        }

        result.Candidates.Should().NotBeEmpty(because: "min_confidence=0 forces all top-10 to emit");

        // §4.0c verification: aggregation should produce at least one synthesised candidate.
        // For real DJ tracks (One Self - Bluebird etc.) the discogs-effnet 400-class output
        // typically spreads across multiple subgenres of the same parent — exactly the case
        // where aggregation adds value over top-1 picking.
        var aggregatedCandidates = result.Candidates
            .Where(c => c.Source.Contains(":aggregated", StringComparison.Ordinal))
            .ToList();
        _out.WriteLine($"Aggregated candidates: {aggregatedCandidates.Count}");
        foreach (var c in aggregatedCandidates)
        {
            _out.WriteLine($"  → {c.Value} p={c.Confidence:0.000} [{c.Source}]");
        }

        // The B6 chain verification: remap → normaliser → (aggregation) → resolver finds
        // a taxonomy match.
        var resolver = new TaxonomyGenreResolver();
        var resolution = resolver.Resolve(
            result.Candidates,
            subGenreCandidates: [],
            taxonomy,
            existingGenre: null,
            existingSubgenre: null);

        _out.WriteLine(
            $"Resolved: genre={resolution.ProposedGenre ?? "(none)"} "
            + $"subgenre={resolution.ProposedSubgenre ?? "(none)"} "
            + $"matched={resolution.MatchedCandidate?.Value ?? "(none)"} "
            + $"matched-source={resolution.MatchedCandidate?.Source ?? "(none)"}");

        resolution.ProposedGenre.Should().NotBeNull(
            because: "discogs-effnet top-10 should produce at least one candidate that matches "
            + "the taxonomy via the remap+normaliser+(aggregation)+resolver chain");
    }

    /// <summary>
    /// Mirrors the canonical electronic + adjacent slice of <c>./music/taxonomy.yaml</c>. Lives
    /// in code rather than loading the YAML because the test project doesn't have a standalone
    /// taxonomy loader (the production loader goes through <c>TaggerOptionsLoader</c>, which
    /// requires a full options tree). 15 genres + the 5 subgenre groups that matter here is
    /// enough to verify the resolution chain.
    /// </summary>
    private static Taxonomy BuildIntegrationTaxonomy()
    {
        var subs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["House"] = ["Deep", "Tech", "Soul", "Soulful", "Funk", "Jazz", "Progressive", "Afro", "Latin", "Disco", "Bass", "Minimal", "Vocal"],
            ["Techno"] = ["Detroit", "Berlin", "Acid", "Melodic", "Industrial", "Minimal", "Hard", "Dub"],
            ["Trance"] = ["Progressive", "Uplifting", "Psy", "Vocal", "Tech", "Hard", "Goa"],
            ["Drum and Bass"] = ["Liquid", "Neurofunk", "Jump-up", "Halftime", "Minimal", "Jungle", "Drumfunk", "Rollers"],
            ["Hip Hop"] = ["Boom Bap", "Trap", "Lo-fi", "Conscious", "Old School", "Drill"],
        };
        return new Taxonomy
        {
            Genres = ["House", "Indie Dance", "Techno", "Trance", "Drum and Bass", "Dubstep",
                      "Hip Hop", "R&B", "Rap", "Pop", "Rock", "Ambient", "Trip Hop", "Downtempo",
                      "Jazz", "Funk", "Soul"],
            Subgenres = subs,
        };
    }
}

/// <summary>
/// Per-test environment for the TF integration suite. Probes prerequisites + lazily downloads
/// the model into a stable temp directory so successive runs hit a cache. When anything's
/// missing, sets <see cref="IsReady"/>=false and records <see cref="SkipReason"/>; tests bail
/// silently with the reason emitted to the xUnit output.
/// </summary>
internal sealed class TfIntegrationEnvironment
{
    public bool IsReady { get; }
    public string SkipReason { get; } = string.Empty;
    public string PythonExecutable { get; } = "python3";
    public string ScriptPath { get; } = string.Empty;
    public string ModelsDirectory { get; } = string.Empty;
    public string MusicDirectory { get; } = string.Empty;

    private TfIntegrationEnvironment(bool isReady, string reason)
    {
        IsReady = isReady;
        SkipReason = reason;
    }

    private TfIntegrationEnvironment(string scriptPath, string modelsDir, string musicDir)
    {
        IsReady = true;
        ScriptPath = scriptPath;
        ModelsDirectory = modelsDir;
        MusicDirectory = musicDir;
    }

    public static async Task<TfIntegrationEnvironment> PrepareAsync(string modelKey, ITestOutputHelper output)
    {
        var repoRoot = TryLocateRepoRoot();
        if (repoRoot is null)
        {
            return new(false, "Could not locate repo root from AppContext.BaseDirectory");
        }

        var scriptPath = Path.Combine(repoRoot, "tools", "raytagger-genre-classifier", "raytagger_genre_classifier.py");
        if (!File.Exists(scriptPath))
        {
            return new(false, $"Python bridge script not found at {scriptPath}");
        }

        var musicDir = Path.Combine(repoRoot, "music");
        if (!Directory.Exists(musicDir) || !HasAudioFile(musicDir))
        {
            return new(false, $"No audio fixtures in {musicDir} (gitignored per CLAUDE.md)");
        }

        var probe = new AnalysisToolProbe(new NativeProcessRunner(NullLogger<NativeProcessRunner>.Instance));
        var pythonProbe = await probe.ProbeAsync("python3", ["--version"]);
        if (!pythonProbe.IsAvailable)
        {
            return new(false, "python3 not on PATH");
        }
        var essentiaProbe = await probe.ProbeAsync("python3", ["-c", "import essentia.standard"]);
        if (!essentiaProbe.IsAvailable)
        {
            return new(false, "essentia-tensorflow not installed (pip install essentia-tensorflow)");
        }

        var modelsDir = Path.Combine(Path.GetTempPath(), "raytagger-b6-integration-models");
        try
        {
            await DownloadModelIfMissingAsync(modelKey, modelsDir, output);
        }
        catch (Exception ex)
        {
            return new(false, $"Model download for '{modelKey}' failed: {ex.Message}");
        }

        return new(scriptPath, modelsDir, musicDir);
    }

    public TensorflowGenreClassifier BuildClassifier(
        string modelKey, double minConfidence, int topK, AggregationConfig? aggregation = null)
    {
        var runner = new NativeProcessRunner(NullLogger<NativeProcessRunner>.Instance);
        return new TensorflowGenreClassifier(
            modelKey,
            PythonExecutable,
            ScriptPath,
            ModelsDirectory,
            minConfidence,
            topK,
            TimeSpan.FromMinutes(2),
            runner,
            NullLogger<TensorflowGenreClassifier>.Instance,
            aggregation);
    }

    public TrackFile PickAudio()
    {
        var path = Directory.EnumerateFiles(MusicDirectory, "*.mp3").FirstOrDefault()
                   ?? Directory.EnumerateFiles(MusicDirectory, "*.flac").FirstOrDefault()
                   ?? throw new InvalidOperationException("No audio files in " + MusicDirectory);
        var info = new FileInfo(path);
        var ext = Path.GetExtension(path);
        var format = string.Equals(ext, ".flac", StringComparison.OrdinalIgnoreCase) ? AudioFormat.Flac
            : string.Equals(ext, ".aiff", StringComparison.OrdinalIgnoreCase) ? AudioFormat.Aiff
            : AudioFormat.Mp3;
        return new TrackFile(path, format, info.Length, info.LastWriteTimeUtc);
    }

    private static bool HasAudioFile(string dir) =>
        Directory.EnumerateFiles(dir, "*.mp3").Any()
        || Directory.EnumerateFiles(dir, "*.flac").Any()
        || Directory.EnumerateFiles(dir, "*.aiff").Any();

    private static string? TryLocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RayTagger.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Downloads the .pb + labels.json files for <paramref name="modelKey"/> into
    /// <c>&lt;modelsDir&gt;/&lt;modelKey&gt;/</c>. Files that already exist are skipped — successive
    /// test runs hit a cache. Uses HttpClient directly rather than NativeToolBootstrapper because
    /// (a) the bootstrapper requires a manifest YAML which we'd have to load with the loader,
    /// (b) the integration test is about the classifier path, not the bootstrap path (covered by
    /// NativeToolBootstrapperTests).
    /// </summary>
    private static async Task DownloadModelIfMissingAsync(string modelKey, string baseDir, ITestOutputHelper output)
    {
        var modelDir = Path.Combine(baseDir, modelKey);
        Directory.CreateDirectory(modelDir);

        var files = GetModelFileSpecs(modelKey);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        foreach (var (url, fileName) in files)
        {
            var target = Path.Combine(modelDir, fileName);
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                continue;
            }

            // Cross-model file dedup keyed by URL (not filename) — multiple models share
            // discogs-effnet-bs64-1.pb (18 MB embedding) and that URL is identical across
            // them. Two models can however emit different .json files (different URLs) that
            // both get renamed to labels.json — those must NOT be cross-copied even though
            // the target filename matches. URL-based check is the right invariant: same URL
            // ⇒ same content.
            var sibling = FindSiblingByUrl(baseDir, modelKey, url);
            if (sibling is not null)
            {
                output.WriteLine($"Reusing {sibling} → {target}");
                File.Copy(sibling, target);
                continue;
            }

            output.WriteLine($"Downloading {url} → {target}");
            await using var stream = await http.GetStreamAsync(new Uri(url));
            await using var file = File.Create(target);
            await stream.CopyToAsync(file);
        }
    }

    private static string? FindSiblingByUrl(string baseDir, string currentModelKey, string url)
    {
        // Scan the file specs of every other model. If any other model declares a file with
        // the same URL AND we've already materialised that file in its model dir, return that
        // path. The URL → target-filename mapping is per-model (different `rename_to` values
        // are possible) so we have to read the other model's spec to know where its copy lives.
        foreach (var otherModel in new[] { "electronic", "jamendo", "discogs-effnet" })
        {
            if (string.Equals(otherModel, currentModelKey, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (var (otherUrl, otherFileName) in GetModelFileSpecs(otherModel))
            {
                if (!string.Equals(otherUrl, url, StringComparison.Ordinal))
                {
                    continue;
                }
                var candidatePath = Path.Combine(baseDir, otherModel, otherFileName);
                if (File.Exists(candidatePath) && new FileInfo(candidatePath).Length > 0)
                {
                    return candidatePath;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Per-model file list — URLs + the cache-side filename the Python script expects. Mirrors
    /// the entries in <c>samples/native-tools.example.yaml</c> but lives here so the integration
    /// test doesn't depend on the manifest format.
    /// </summary>
    private static (string Url, string FileName)[] GetModelFileSpecs(string modelKey)
    {
        return modelKey switch
        {
            "electronic" =>
            [
                ("https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.pb",
                 "discogs-effnet-bs64-1.pb"),
                ("https://essentia.upf.edu/models/classification-heads/genre_electronic/genre_electronic-discogs-effnet-1.pb",
                 "genre_electronic-discogs-effnet-1.pb"),
                ("https://essentia.upf.edu/models/classification-heads/genre_electronic/genre_electronic-discogs-effnet-1.json",
                 "labels.json"),
            ],
            "jamendo" =>
            [
                ("https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.pb",
                 "discogs-effnet-bs64-1.pb"),
                ("https://essentia.upf.edu/models/classification-heads/mtg_jamendo_genre/mtg_jamendo_genre-discogs-effnet-1.pb",
                 "mtg_jamendo_genre-discogs-effnet-1.pb"),
                ("https://essentia.upf.edu/models/classification-heads/mtg_jamendo_genre/mtg_jamendo_genre-discogs-effnet-1.json",
                 "labels.json"),
            ],
            "discogs-effnet" =>
            [
                ("https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.pb",
                 "discogs-effnet-bs64-1.pb"),
                ("https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.json",
                 "labels.json"),
            ],
            _ => throw new ArgumentException($"Unknown model key '{modelKey}'", nameof(modelKey)),
        };
    }
}
