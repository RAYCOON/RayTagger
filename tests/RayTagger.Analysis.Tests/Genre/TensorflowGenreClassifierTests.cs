using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Analysis.Genre;
using RayTagger.Analysis.Internal;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests.Genre;

public class TensorflowGenreClassifierTests
{
    private static readonly TrackFile FakeFile = new("/fake/song.mp3", AudioFormat.Mp3, 1024, DateTime.UtcNow);

    private static TensorflowGenreClassifier MakeClassifier(
        INativeProcessRunner runner,
        string modelKey = "electronic",
        double minConfidence = 0.5,
        int topK = 5,
        AggregationConfig? aggregation = null) =>
        new(
            modelKey,
            pythonExecutable: "/usr/bin/python3",
            scriptPath: "/opt/raytagger/raytagger_genre_classifier.py",
            modelsDirectory: "/var/raytagger/models",
            minConfidence: minConfidence,
            topK: topK,
            timeout: TimeSpan.FromSeconds(30),
            runner,
            NullLogger<TensorflowGenreClassifier>.Instance,
            aggregation);

    private static Taxonomy BuildTestTaxonomy()
    {
        // Subset of music/taxonomy.yaml relevant for the aggregation tests below.
        var subs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["House"] = ["Deep", "Tech"],
            ["Hip Hop"] = ["Boom Bap", "Trap", "Conscious"],
        };
        return new Taxonomy
        {
            Genres = ["House", "Techno", "Trance", "Drum and Bass", "Hip Hop", "R&B",
                      "Pop", "Rock", "Ambient", "Trip Hop", "Downtempo", "Jazz", "Funk", "Soul"],
            Subgenres = subs,
        };
    }

    private static AggregationConfig MakeAggregation(
        double minTotal = 0.25,
        double perCandidateFloor = 0.02,
        bool fallback = true) =>
        new(
            Matcher: new TaxonomyGenreMatcher(BuildTestTaxonomy()),
            PerCandidateFloor: perCandidateFloor,
            MinTotal: minTotal,
            FallbackOnDiffuse: fallback);

    private static ProcessResult Ok(string stdout) =>
        new(ExitCode: 0, StandardOutput: stdout, StandardError: string.Empty, Duration: TimeSpan.FromMilliseconds(50));

    private static ProcessResult Failed(int exitCode, string stderr = "") =>
        new(ExitCode: exitCode, StandardOutput: string.Empty, StandardError: stderr, Duration: TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task Parses_valid_json_into_candidates()
    {
        const string stdout = """
            {"model": "electronic", "predictions": [
                {"label": "house", "probability": 0.78},
                {"label": "techno", "probability": 0.12},
                {"label": "trance", "probability": 0.05}
            ]}
            """;
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var classifier = MakeClassifier(runner, minConfidence: 0.0);
        var result = await classifier.ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().HaveCount(3);
        result.Candidates[0].Value.Should().Be("house");
        result.Candidates[0].Confidence.Should().BeApproximately(0.78, 1e-6);
        result.Candidates[0].Source.Should().Be("classifier:essentia-tf-electronic");
        result.Candidates[1].Value.Should().Be("techno");
        result.Candidates[2].Value.Should().Be("trance");
    }

    [Fact]
    public async Task Applies_min_confidence_floor()
    {
        const string stdout = """
            {"predictions": [
                {"label": "house", "probability": 0.78},
                {"label": "techno", "probability": 0.12},
                {"label": "trance", "probability": 0.05}
            ]}
            """;
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var classifier = MakeClassifier(runner, minConfidence: 0.15);
        var result = await classifier.ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().ContainSingle()
            .Which.Value.Should().Be("house");
    }

    [Fact]
    public async Task Applies_label_normaliser_to_model_output()
    {
        // Mimics discogs-effnet output: 'Electronic---Tech House' goes through the §5.1a
        // normaliser and emerges with `---` and capitalisation flattened to a form the
        // .NET resolver can whole-word match.
        const string stdout = """
            {"predictions": [
                {"label": "Electronic---Tech House", "probability": 0.6},
                {"label": "Drum n Bass", "probability": 0.3}
            ]}
            """;
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var classifier = MakeClassifier(runner, minConfidence: 0.0);
        var result = await classifier.ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().HaveCount(2);
        result.Candidates[0].Value.Should().Be("electronic tech house");   // ---, casing normalised
        result.Candidates[1].Value.Should().Be("drum n bass");             // ss-guard preserves trailing double-s
    }

    [Fact]
    public async Task Drum_n_bass_keeps_its_double_s()
    {
        // Sanity: the §5.1a normaliser's depluralisation has an "ss" guard so 'drum n bass'
        // doesn't lose its trailing letter. This is what makes the discogs-effnet remap
        // mapping 'electronic---drum n bass' → 'Drum and Bass' useful — the remap injects
        // the missing "and" without the normaliser breaking the word.
        const string stdout = """{"predictions": [{"label": "drum n bass", "probability": 0.9}]}""";
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var result = await MakeClassifier(runner, minConfidence: 0.0).ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().ContainSingle().Which.Value.Should().Be("drum n bass");
    }

    [Fact]
    public async Task Exit_code_2_model_missing_returns_empty()
    {
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Failed(2, "Model file missing: /var/raytagger/models/electronic/discogs-effnet-bs64-1.pb"));

        var result = await MakeClassifier(runner).ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Exit_code_3_audio_unreadable_returns_empty()
    {
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Failed(3, "Audio file does not exist: /fake/song.mp3"));

        var result = await MakeClassifier(runner).ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Exit_code_1_generic_failure_returns_empty()
    {
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Failed(1, "essentia-tensorflow is not installed"));

        var result = await MakeClassifier(runner).ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_json_returns_empty()
    {
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok("not json at all"));

        var result = await MakeClassifier(runner).ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_predictions_field_returns_empty()
    {
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok("""{"model": "electronic"}"""));

        var result = await MakeClassifier(runner).ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Tolerates_stderr_noise_when_json_is_on_stdout()
    {
        // Mimics TF/Essentia logging that may leak to stdout in edge cases — the parser scans
        // from the bottom for the JSON line. (In practice TF logs to stderr, but be defensive.)
        const string stdout = """
            [   INFO   ] TensorflowPredict: Successfully loaded graph file: foo
            WARNING: stuff
            {"predictions": [{"label": "house", "probability": 0.8}]}
            """;
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var result = await MakeClassifier(runner, minConfidence: 0.0).ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().ContainSingle().Which.Value.Should().Be("house");
    }

    [Fact]
    public async Task Timeout_returns_empty_not_throws()
    {
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProcessResult>>(_ => throw new TimeoutException("script wedged"));

        var result = await MakeClassifier(runner).ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_bubbles_up()
    {
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProcessResult>>(_ => throw new OperationCanceledException());

        var act = () => MakeClassifier(runner).ClassifyAsync(FakeFile, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Process_spawn_failure_returns_empty()
    {
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProcessResult>>(_ => throw new System.ComponentModel.Win32Exception("file not found"));

        var result = await MakeClassifier(runner).ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Passes_correct_args_to_runner()
    {
        var runner = Substitute.For<INativeProcessRunner>();
        IReadOnlyList<string>? capturedArgs = null;
        string? capturedExe = null;
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedExe = (string)callInfo[0];
                capturedArgs = (IReadOnlyList<string>)callInfo[1];
                return Task.FromResult(Ok("""{"predictions": []}"""));
            });

        var classifier = MakeClassifier(runner, modelKey: "discogs-effnet", topK: 10);
        await classifier.ClassifyAsync(FakeFile, CancellationToken.None);

        capturedExe.Should().Be("/usr/bin/python3");
        capturedArgs.Should().NotBeNull();
        capturedArgs!.Should().Contain("--model").And.Contain("discogs-effnet");
        capturedArgs.Should().Contain("--audio").And.Contain(FakeFile.Path);
        capturedArgs.Should().Contain("--models-dir").And.Contain("/var/raytagger/models");
        capturedArgs.Should().Contain("--top-k").And.Contain("10");
    }

    [Fact]
    public void Name_for_each_model_key_follows_convention()
    {
        TensorflowGenreClassifier.NameFor("electronic").Should().Be("essentia-tf-electronic");
        TensorflowGenreClassifier.NameFor("jamendo").Should().Be("essentia-tf-jamendo");
        TensorflowGenreClassifier.NameFor("discogs-effnet").Should().Be("essentia-tf-discogs-effnet");
    }

    // --- §4.0c Aggregation tests --------------------------------------------------------------

    /// <summary>
    /// The user's documented motivating example from One Self - Bluebird.mp3 — top-10 with
    /// 6 Hip-Hop variants spread across subgenres, but the top-1 raw pick is Downtempo. With
    /// aggregation enabled, Hip Hop should win the parent-genre vote (sum ≈ 0.495) and emit
    /// as the first candidate with the :aggregated source suffix.
    /// </summary>
    [Fact]
    public async Task Aggregates_top_k_by_taxonomy_parent_when_enabled()
    {
        const string stdout = """
            {"predictions": [
                {"label": "Electronic---Downtempo",  "probability": 0.156},
                {"label": "Hip Hop---Conscious",     "probability": 0.113},
                {"label": "Electronic---Trip Hop",   "probability": 0.108},
                {"label": "Hip Hop---Trip Hop",      "probability": 0.094},
                {"label": "Funk / Soul---Neo Soul",  "probability": 0.082},
                {"label": "Hip Hop---Jazzy Hip-Hop", "probability": 0.073},
                {"label": "Hip Hop---RnB/Swing",     "probability": 0.073},
                {"label": "Hip Hop---Ragga HipHop",  "probability": 0.072},
                {"label": "Funk / Soul---Soul",      "probability": 0.071},
                {"label": "Electronic---Hip Hop",    "probability": 0.070}
            ]}
            """;
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var result = await MakeClassifier(runner, minConfidence: 0.0, aggregation: MakeAggregation())
            .ClassifyAsync(FakeFile, CancellationToken.None);

        // First candidate must be aggregated Hip Hop. Confidence is the SUM of every raw
        // candidate that resolves to "Hip Hop" via the longest-match rule. Important detail:
        // `Hip Hop---Trip Hop` normalises to "hip hop trip hop" which matches BOTH "Hip Hop"
        // (7 chars) and "Trip Hop" (8 chars) — longest wins, so Trip Hop takes that p=0.094.
        // Hip Hop ends up with 5 contributors (not 6), summing to 0.401.
        var first = result.Candidates[0];
        first.Value.Should().Be("hip hop");
        first.Source.Should().Be("classifier:essentia-tf-electronic:aggregated");
        first.Confidence.Should().BeApproximately(0.401, 0.001,
            because: "Hip Hop contributors: Conscious 0.113 + Jazzy 0.073 + RnB/Swing 0.073 "
            + "+ Ragga 0.072 + Electronic---Hip Hop 0.070 = 0.401. 'Hip Hop---Trip Hop' "
            + "goes to Trip Hop via longest-match (8 > 7 chars).");

        // Raw candidates still present, sorted by raw probability after the aggregated entry.
        result.Candidates.Should().Contain(c => c.Value == "electronic downtempo" && c.Source == "classifier:essentia-tf-electronic");
    }

    [Fact]
    public async Task Does_not_aggregate_when_aggregation_config_is_null()
    {
        // Same input as the documented example; without an AggregationConfig the classifier
        // emits only the raw top-K — backward compatibility with pre-B6.5 behaviour.
        const string stdout = """
            {"predictions": [
                {"label": "Electronic---Downtempo",  "probability": 0.156},
                {"label": "Hip Hop---Conscious",     "probability": 0.113}
            ]}
            """;
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var result = await MakeClassifier(runner, minConfidence: 0.0, aggregation: null)
            .ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().HaveCount(2);
        result.Candidates.Select(c => c.Source).Should().AllSatisfy(s =>
            s.Should().Be("classifier:essentia-tf-electronic"),
            because: "no aggregated source suffix without an AggregationConfig");
    }

    [Fact]
    public async Task Per_candidate_floor_excludes_long_tail_from_aggregation_sum()
    {
        // 0.50 single Hip Hop pick + 10 tiny Hip Hop tail (p=0.01 each). With floor=0.02 the
        // tail is excluded → aggregated Hip Hop = 0.50. With floor=0.0 the tail would inflate
        // the sum to 0.60. We verify the former.
        const string strongPick = "{\"label\": \"Hip Hop---Boom Bap\", \"probability\": 0.50}";
        const string tailPick = "{\"label\": \"Hip Hop---Conscious\", \"probability\": 0.01}";
        var allPicks = string.Join(",", new[] { strongPick }.Concat(Enumerable.Repeat(tailPick, 10)));
        var stdout = "{\"predictions\": [" + allPicks + "]}";

        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var result = await MakeClassifier(runner, minConfidence: 0.0,
            aggregation: MakeAggregation(perCandidateFloor: 0.02))
            .ClassifyAsync(FakeFile, CancellationToken.None);

        var aggregated = result.Candidates.First(c => c.Source.EndsWith(":aggregated", StringComparison.Ordinal));
        aggregated.Confidence.Should().BeApproximately(0.50, 0.001,
            because: "10 tail candidates of p=0.01 fall below floor=0.02 and don't contribute");
    }

    [Fact]
    public async Task Diffuse_output_with_fallback_enabled_emits_argmax_with_fallback_source()
    {
        // 4-way Crossover at 0.20 / 0.18 / 0.16 / 0.14 — none above min_total=0.25 →
        // fallback kicks in, emits the 0.20 winner with :aggregated-fallback source.
        const string stdout = """
            {"predictions": [
                {"label": "Hip Hop---Trap",        "probability": 0.20},
                {"label": "Funk / Soul---Soul",    "probability": 0.18},
                {"label": "Electronic---Downtempo","probability": 0.16},
                {"label": "Jazz",                  "probability": 0.14}
            ]}
            """;
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var result = await MakeClassifier(runner, minConfidence: 0.0,
            aggregation: MakeAggregation(minTotal: 0.25, fallback: true))
            .ClassifyAsync(FakeFile, CancellationToken.None);

        var fallback = result.Candidates.FirstOrDefault(c =>
            c.Source.EndsWith(":aggregated-fallback", StringComparison.Ordinal));
        fallback.Should().NotBeNull();
        fallback!.Value.Should().Be("hip hop");
        fallback.Confidence.Should().BeApproximately(0.20, 0.001);
    }

    [Fact]
    public async Task Diffuse_output_without_fallback_emits_only_raw_candidates()
    {
        // Same 4-way crossover but fallback disabled → no aggregated candidate. Raw top-K
        // is all that emits.
        const string stdout = """
            {"predictions": [
                {"label": "Hip Hop---Trap",        "probability": 0.20},
                {"label": "Funk / Soul---Soul",    "probability": 0.18},
                {"label": "Electronic---Downtempo","probability": 0.16},
                {"label": "Jazz",                  "probability": 0.14}
            ]}
            """;
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var result = await MakeClassifier(runner, minConfidence: 0.0,
            aggregation: MakeAggregation(minTotal: 0.25, fallback: false))
            .ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().NotContain(c =>
            c.Source.Contains(":aggregated", StringComparison.Ordinal));
        result.Candidates.Should().HaveCount(4,
            because: "raw top-K still emits — only the aggregated synthesis is suppressed");
    }

    [Fact]
    public async Task Aggregation_works_on_full_top_K_even_when_min_confidence_filters_raw_emit()
    {
        // The critical design property: aggregation feeds on ALL predictions before
        // min_confidence filtering. Otherwise a strict min_confidence=0.5 would gut the
        // aggregation input and defeat the purpose.
        const string stdout = """
            {"predictions": [
                {"label": "Hip Hop---Boom Bap",   "probability": 0.20},
                {"label": "Hip Hop---Trap",       "probability": 0.18},
                {"label": "Hip Hop---Conscious",  "probability": 0.15}
            ]}
            """;
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        // min_confidence=0.50 → no raw candidate emits (all below). But aggregation should
        // still produce Hip Hop with sum=0.53, above min_total=0.25.
        var result = await MakeClassifier(runner, minConfidence: 0.50,
            aggregation: MakeAggregation(minTotal: 0.25))
            .ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().ContainSingle(
            because: "raw filtered out by min_confidence=0.5 but aggregated Hip Hop = 0.53 survives");
        var aggregated = result.Candidates[0];
        aggregated.Source.Should().Be("classifier:essentia-tf-electronic:aggregated");
        aggregated.Value.Should().Be("hip hop");
        aggregated.Confidence.Should().BeApproximately(0.53, 0.001);
    }

    [Fact]
    public async Task Aggregation_emits_zero_candidates_when_nothing_matches_taxonomy()
    {
        // All-Latin output — no entry matches the (test) taxonomy → aggregation produces
        // nothing. Raw candidates pass through unchanged.
        const string stdout = """
            {"predictions": [
                {"label": "Latin---Salsa",  "probability": 0.40},
                {"label": "Latin---Mambo",  "probability": 0.30}
            ]}
            """;
        var runner = Substitute.For<INativeProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Ok(stdout));

        var result = await MakeClassifier(runner, minConfidence: 0.0,
            aggregation: MakeAggregation())
            .ClassifyAsync(FakeFile, CancellationToken.None);

        result.Candidates.Should().NotContain(c =>
            c.Source.Contains(":aggregated", StringComparison.Ordinal));
        result.Candidates.Should().HaveCount(2,
            because: "raw Latin candidates still emit — only aggregation drops them");
    }
}
