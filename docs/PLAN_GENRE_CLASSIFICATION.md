# Plan — Audio-based Genre Classification

**Status:** approved, ready for implementation.
**Scope:** add two opt-in `IGenreClassifier` families (heuristic + TensorFlow) whose output enriches the existing `LookupResult.GenreCandidates` stream consumed by `TaxonomyGenreResolver`. Default-off everywhere — switching all flags off yields byte-identical behaviour to today.

---

## 1. Goals & non-goals

**Goals**

1. Provide audio-based genre detection for the 8 top-level electronic + adjacent genres the team library actually contains (House, Techno, Trance, Drum and Bass, Dubstep, Hip Hop, Ambient, Downtempo) — beyond what the four online providers can guess from metadata alone. Phase B adds subgenre detection (Tech House, Liquid DnB, …) and broader-genre coverage (Rock, Pop, R&B, Jazz, …).
2. Keep all new behaviour opt-in and per-classifier toggleable in `tagger.yaml`.
3. Reuse the existing taxonomy + resolver code path — no parallel "classifier merger" logic.
4. Match the project's existing patterns: subprocess-based analysis, PATH/bootstrap probe, graceful degradation, status reporting.

**Non-goals**

1. Replace the online providers. Classifiers complement, never supersede.
2. Cloud-based classification (Cyanite et al.) — explicitly excluded by the user.
3. Personalised / per-user classifiers (KNN on embeddings) — out of scope here.
4. Cover-art, lyrics-topic, DJ-set, or LLM-based classification — out of scope.
5. New dimensions other than genre/sub-genre (no mood, no sub-style detection beyond what the TF models emit natively).

---

## 2. Architecture overview

```
┌────────────────────────────────────────┐
│  TagPipeline.ProcessFileAsync          │
│                                        │
│  TrackFile  →  Read existing tags      │
│       │                                │
│       ↓                                │
│  ┌────────────────────┐                │
│  │ IAnalysisRunner    │ → AnalysisResult (BPM/Key/Energy/Fingerprint)
│  └────────────────────┘                │   + EssentiaResult cached per file+mtime
│       │                                │
│       ↓                                │
│  ┌────────────────────┐                │
│  │ ILookupRunner      │ → LookupRunResult { LookupResult, ProviderTrace }
│  │  (online providers)│                │
│  └────────────────────┘                │
│       │                                │
│       ↓                                │
│  ┌────────────────────┐                │  NEW STAGE
│  │ IGenreClassifier-  │ → GenreClassificationRunResult { Candidates, ClassifierTrace }
│  │ Runner             │                │     ↓
│  │  - Heuristic       │                │  TagPipeline appends Candidates into
│  │  - TF: electronic  │                │  LookupResult.GenreCandidates with
│  │  - TF: jamendo     │                │  Source = "classifier:<name>".
│  │  - TF: discogs-eff │                │
│  └────────────────────┘                │
│       │                                │
│       ↓                                │
│  ┌────────────────────┐                │
│  │ TagMerger +        │ → ResolvedTrackTags
│  │ TaxonomyGenre-     │                │
│  │ Resolver           │                │
│  └────────────────────┘                │
└────────────────────────────────────────┘
```

**Integration point:** a new pipeline stage `IGenreClassifierRunner` that mirrors `ILookupRunner`'s shape (one runner per pipeline, holds the list of enabled classifiers, returns a result + trace). The stage executes in `TagPipeline.ProcessFileAsync` between the lookup and the merger. Why this and not a decorator around `ILookupRunner`:

- `LookupQuery` is text-only (artist/title/album/fingerprint/MBIDs) — adding a `TrackFile` field would mix concerns with the text-only HTTP providers.
- `TagPipeline.ProcessFileAsync` already has `TrackFile` and `AnalysisResult` in scope — the natural home for the classifier call.
- A new runner mirrors the existing `ILookupRunner` pattern (filtering, ordering, trace, fail-open contract) so reviewers recognise the shape instantly.
- The resolver and merger code paths are *still* untouched — TagPipeline rewrites `lookup` to a new `LookupResult` with appended candidates before passing it on.

**Default behaviour (all flags off):** the DI container registers a `NoopGenreClassifierRunner` that returns empty trace and zero candidates. TagPipeline appends nothing, behaviour is byte-identical to today. Switching one flag on swaps the no-op for the real `GenreClassifierRunner` at `PipelineFactory` build time — same pattern as `LookupRunner` vs `NoopLookupRunner`.

Alternative considered: extend `AnalysisResult` with classifier output. Rejected because the resolver does not look at `AnalysisResult` for genre data today, and we'd duplicate "candidate + confidence + source" plumbing.

---

## 3. Phase A — Tier 3 Heuristic Classifier

### 3.1 Extend `EssentiaResult`

Add five DSP-only fields the heuristic needs (all already in Essentia's default JSON output, no profile change required):

```csharp
public sealed record EssentiaResult(
    double? Bpm,
    double? BpmConfidence,
    string? KeyKey,
    string? KeyScale,                  // existing — "major"/"minor"
    double? KeyStrength,
    double? SpectralEnergy,
    double? AverageLoudness,
    double? SpectralFlux,
    double? OnsetRate,
    double? Danceability,
    double? BeatsLoudness,
    // NEW (DSP-only — present in every Essentia run):
    double? SpectralCentroidMean,      // lowlevel.spectral_centroid.mean
    double? SpectralComplexityMean,    // lowlevel.spectral_complexity.mean
    double? DynamicComplexity,         // lowlevel.dynamic_complexity
    double? ChordsChangesRate,         // tonal.chords_changes_rate
    double? ChordsStrengthMean);       // tonal.chords_strength.mean
```

All fields are nullable and treated as "no signal" when absent — heuristic gracefully degrades if Essentia changes output shape.

### 3.2 Extend `EssentiaJsonParser`

Add five lookups using the existing helpers (`TryReadAggregateMean` / `TryReadDouble`):

```csharp
var spectralCentroid    = TryReadAggregateMean(root, "lowlevel", "spectral_centroid");
var spectralComplexity  = TryReadAggregateMean(root, "lowlevel", "spectral_complexity");
var dynamicComplexity   = TryReadDouble(root, "lowlevel", "dynamic_complexity");
var chordsChangesRate   = TryReadDouble(root, "tonal", "chords_changes_rate");
var chordsStrength      = TryReadAggregateMean(root, "tonal", "chords_strength");
```

Update `EssentiaJsonParserTests` with fixture JSON that contains these fields plus one fixture where they're absent — `null` round-trip.

### 3.3 New interface `IGenreClassifier`

In `src/RayTagger.Analysis/Genre/IGenreClassifier.cs`:

```csharp
namespace RayTagger.Analysis.Genre;

/// <summary>
/// Optional audio-based genre classifier. Output is appended to the candidate stream the
/// taxonomy resolver consumes — Source is "classifier:&lt;name&gt;" so the trace shows it
/// distinctly from provider-sourced candidates.
/// </summary>
public interface IGenreClassifier
{
    /// <summary>Display name used in <c>GenreCandidate.Source</c> and status logs.</summary>
    string Name { get; }

    /// <summary>Classifies one track. Returns empty when the classifier cannot run for this track
    /// (silent failure mode — pipeline continues). Never throws for per-track issues.</summary>
    Task<GenreClassificationResult> ClassifyAsync(TrackFile file, CancellationToken cancellationToken);
}

public sealed record GenreClassificationResult(IReadOnlyList<GenreCandidate> Candidates)
{
    public static GenreClassificationResult Empty { get; } = new([]);
}
```

`GenreCandidate` lives in `RayTagger.Core.Models` — reuse, don't duplicate. The `Source` string convention is `classifier:<name>` (e.g. `classifier:heuristic`, `classifier:essentia-tf-electronic`). Each classifier applies the §5.1a label normaliser before emitting candidates so the resolver sees taxonomy-compatible whole-word values.

### 3.4 `HeuristicGenreClassifier`

In `src/RayTagger.Analysis/Genre/HeuristicGenreClassifier.cs`:

```csharp
public sealed class HeuristicGenreClassifier : IGenreClassifier
{
    public string Name => "heuristic";
    private readonly IEssentiaAnalysisService _essentia;
    private readonly HeuristicClassifierOptions _options;
    private readonly ILogger<HeuristicGenreClassifier> _logger;

    public async Task<GenreClassificationResult> ClassifyAsync(
        TrackFile file, CancellationToken ct)
    {
        var result = await _essentia.RunAsync(file, ct).ConfigureAwait(false);
        if (result is null) return GenreClassificationResult.Empty;
        var scores = ScoreAll(result);
        var candidates = scores
            .Where(s => s.Confidence >= _options.MinConfidence)
            .OrderByDescending(s => s.Confidence)
            .Select(s => new GenreCandidate(s.Genre, s.Confidence, $"classifier:{Name}"))
            .ToList();
        return new GenreClassificationResult(candidates);
    }

    internal static IReadOnlyList<(string Genre, double Confidence)> ScoreAll(EssentiaResult r) { … }
}
```

### 3.5 Heuristic scoring (8-genre lineup)

Each genre gets a score in [0, 1] computed from independent feature contributions. Final candidate confidence = average over features that returned a usable signal (null features are skipped, not zeroed).

**Lineup** (mirrors the canonical electronic + adjacent entries in `taxonomy.yaml` that have DSP-distinguishable signatures):

`House`, `Techno`, `Trance`, `Drum and Bass`, `Dubstep`, `Hip Hop`, `Ambient`, `Downtempo`.

Same-genre subgenres (Tech House vs Deep House, Detroit vs Berlin Techno, Liquid vs Neurofunk DnB) are deliberately **out of scope** for the heuristic — discriminating those requires harmonic / timbral analysis the heuristic doesn't have. They land in Phase B (TF `discogs_effnet` model, 400 fine-grained labels) or stay in the user's mapping rules.

| Feature                       | House          | Techno         | Trance         | Drum and Bass  | Dubstep        | Hip Hop        | Ambient         | Downtempo      |
|-------------------------------|----------------|----------------|----------------|----------------|----------------|----------------|-----------------|----------------|
| `Bpm` (center ± half-width)   | 118–128 ±10    | 125–145 ±10    | 128–145 ±10    | 165–180 ±10    | 138–145 ±10    | 80–100 ±10     | 40–150 ±30 (capped peak) | 75–110 ±15 |
| `KeyScale`                    | minor 0.6 / major 0.4 | neutral 0.5  | major 0.7 / minor 0.4 | minor 0.7 / major 0.4 | minor 0.65 / major 0.4 | minor 0.65 / major 0.5 | neutral 0.55  | minor 0.65 / major 0.5 |
| `ChordsChangesRate`           | mid 0.7        | low 0.9        | high 0.8       | low 0.7        | low 0.9        | mid 0.85       | low 0.65        | high 0.85      |
| `SpectralCentroidMean`        | low 0.7        | high 0.8       | mid 0.7        | high 0.8       | high 0.75      | low 0.8        | low 0.8         | low 0.7        |
| `DynamicComplexity`           | mid 0.6        | low 0.8        | high 0.7       | high 0.75      | very high 0.9  | mid 0.8        | low 0.85        | mid 0.7        |
| `Danceability`                | high 0.7       | high 0.7       | mid 0.7        | high 0.8       | high 0.65      | mid 0.8        | low 0.85        | mid 0.7        |
| `BeatsLoudness`-profile       | High           | VeryHigh       | Mid            | VeryHigh       | VeryHigh       | Mid            | VeryLow         | Low            |

The `BeatsLoudness` profile selects a piecewise-linear scoring curve. Five bands: `VeryLow` peaks at `< 0.08` for ambient material; `Low` peaks at `< 0.10` for downtempo / trip-hop; `Mid` between 0.08–0.14 for hip-hop and trance; `High` from 0.14 for house; `VeryHigh` from 0.16 for techno / DnB / dubstep drops.

Numeric thresholds are tuned constants in `HeuristicClassifierOptions` with documented defaults; user-overrideable for power users who want to retune for their library. Default weight per feature is 1.0; user can also override weights (advanced, undocumented in the example YAML).

**Tiebreaks and floor:** if the top candidate's score is below `MinConfidence` (default `0.55`), the classifier returns no candidates at all — better silent than wrong. If two genres tie within 0.02, emit both with the higher score so the taxonomy resolver can disambiguate via existing-tag-protection.

**Why these features:** Chords-changes-rate alone separates Techno (loop-based, < 0.04) from Trance (chord-progression-based, > 0.10) with > 80% precision on a balanced test set per Essentia's published validation; the rest are supporting evidence to break BPM-overlap ambiguity. The 8-genre lineup leans hardest on **BPM** to split clusters that don't overlap (DnB 172 vs House 122 are unmistakable) and falls back to per-feature score profiles for clusters that do overlap (Hip Hop ↔ Downtempo at 90 BPM differ on beat punch and chord motion; Techno ↔ Dubstep at 140 BPM differ on dynamic complexity).

**Known overlap zones** that the heuristic deliberately doesn't try to resolve:

- **House ↔ Indie Dance** (both 110–125 BPM) — needs rock/electronic timbre split.
- **Trance ↔ Hard Trance ↔ Tech Trance** — same parent, subgenre work.
- **Hip Hop ↔ R&B ↔ Soul** at 80–100 BPM with vocal-heavy mixes — needs vocal-presence detection.
- **Trip Hop ↔ Downtempo** — almost identical DSP signature; the heuristic emits Downtempo for both and lets the taxonomy resolver / mapping rules disambiguate.

### 3.6 Configuration

New top-level branch in `analysis`:

```yaml
analysis:
  # ... existing bpm/key/energy/fingerprint blocks unchanged ...

  genre_classifier:
    heuristic:
      enabled: false                  # off by default
      min_confidence: 0.55            # below → no candidate emitted
    tensorflow:
      # Each model is a separate switch — turn on what you need, leave the rest off.
      genre_electronic:
        enabled: false
        min_confidence: 0.60
      mtg_jamendo:
        enabled: false
        min_confidence: 0.50
      discogs_effnet:
        enabled: false
        min_confidence: 0.50
      python_executable: ""           # empty = "python3" via PATH
      script_path: ""                 # empty = auto-discover next to tagger executable
      models_directory: ""            # empty = <data-dir>/RayTagger/models/
```

POCOs in `src/RayTagger.Core/Configuration/TaggerOptions.cs`:

```csharp
public sealed class AnalysisOptions
{
    // ... existing ...
    public GenreClassifierOptions GenreClassifier { get; set; } = new();
}

public sealed class GenreClassifierOptions
{
    public HeuristicClassifierOptions Heuristic { get; set; } = new();
    public TensorflowClassifierOptions Tensorflow { get; set; } = new();
}

public sealed class HeuristicClassifierOptions
{
    public bool Enabled { get; set; }
    public double MinConfidence { get; set; } = 0.55;
}

public sealed class TensorflowClassifierOptions
{
    // Asymmetric defaults — see B3 retrospective in §8. Each model's min_confidence is
    // independently overridable in tagger.yaml.
    public TensorflowModelOptions GenreElectronic { get; set; } = new() { MinConfidence = 0.65 };
    public TensorflowModelOptions MtgJamendo      { get; set; } = new();   // 0.50
    public TensorflowModelOptions DiscogsEffnet   { get; set; } = new();   // 0.50
    public string PythonExecutable { get; set; } = string.Empty;
    public string ScriptPath { get; set; } = string.Empty;
    public string ModelsDirectory { get; set; } = string.Empty;
}

public sealed class TensorflowModelOptions
{
    public bool Enabled { get; set; }
    public double MinConfidence { get; set; } = 0.5;
}
```

Validation: `TaggerOptionsValidator` rejects `min_confidence ∉ [0, 1]`. No relationship constraints between flags (any subset can be on).

### 3.7 Pipeline integration — `IGenreClassifierRunner` stage

#### 3.7.1 The runner interface

In `src/RayTagger.Core/Pipeline/IGenreClassifierRunner.cs`:

```csharp
namespace RayTagger.Core.Pipeline;

/// <summary>
/// Runs every enabled <see cref="IGenreClassifier"/> for one track and merges their outputs into
/// a single ranked candidate list. Mirrors <see cref="ILookupRunner"/>'s shape (fail-open contract,
/// separate trace for the UI, NoOp variant for the all-disabled case).
/// </summary>
public interface IGenreClassifierRunner
{
    Task<GenreClassifierRunResult> RunAsync(
        TrackFile file,
        AnalysisResult analysis,
        CancellationToken cancellationToken = default);
}

public sealed record GenreClassifierRunResult(
    IReadOnlyList<GenreCandidate> Candidates,
    IReadOnlyList<ClassifierTraceEntry> Trace)
{
    public static GenreClassifierRunResult Empty { get; } = new([], []);
}

public sealed record ClassifierTraceEntry(
    string ClassifierName,
    ClassifierTraceStatus Status,
    IReadOnlyList<GenreCandidate> Candidates,
    string? ErrorMessage,
    double DurationMs);

public enum ClassifierTraceStatus { Ok, NoHit, Disabled, Failed }

public sealed class NoopGenreClassifierRunner : IGenreClassifierRunner
{
    public static NoopGenreClassifierRunner Instance { get; } = new();
    public Task<GenreClassifierRunResult> RunAsync(
        TrackFile file, AnalysisResult analysis, CancellationToken ct) =>
        Task.FromResult(GenreClassifierRunResult.Empty);
}
```

The `IGenreClassifier` interface from §3.3 stays as-is — individual classifiers; `IGenreClassifierRunner` is the per-pipeline composite.

#### 3.7.2 Default implementation

In `src/RayTagger.Analysis/Genre/GenreClassifierRunner.cs`:

```csharp
public sealed class GenreClassifierRunner : IGenreClassifierRunner
{
    private readonly IReadOnlyList<IGenreClassifier> _classifiers;
    private readonly ILogger<GenreClassifierRunner> _logger;

    public async Task<GenreClassifierRunResult> RunAsync(
        TrackFile file, AnalysisResult analysis, CancellationToken ct)
    {
        if (_classifiers.Count == 0) return GenreClassifierRunResult.Empty;

        var tasks = _classifiers.Select(c => RunOneAsync(c, file, ct)).ToArray();
        var entries = await Task.WhenAll(tasks).ConfigureAwait(false);

        var allCandidates = entries
            .Where(e => e.Status == ClassifierTraceStatus.Ok)
            .SelectMany(e => e.Candidates)
            .OrderByDescending(c => c.Confidence)   // sort within classifier hits only;
                                                     // they get appended AFTER provider hits.
            .ToList();
        return new GenreClassifierRunResult(allCandidates, entries);
    }

    private async Task<ClassifierTraceEntry> RunOneAsync(
        IGenreClassifier classifier, TrackFile file, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await classifier.ClassifyAsync(file, ct).ConfigureAwait(false);
            sw.Stop();
            var status = result.Candidates.Count > 0
                ? ClassifierTraceStatus.Ok
                : ClassifierTraceStatus.NoHit;
            return new ClassifierTraceEntry(
                classifier.Name, status, result.Candidates, null, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Classifier {Name} threw: {Message}", classifier.Name, ex.Message);
            return new ClassifierTraceEntry(
                classifier.Name, ClassifierTraceStatus.Failed, [], ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }
}
```

#### 3.7.3 TagPipeline wiring

`TagPipeline` constructor gains one parameter: `IGenreClassifierRunner classifierRunner`. In `ProcessFileAsync`, after the lookup block and before `TagMerger.Merge`:

```csharp
// existing lookup block...
LookupResult? lookup = null;
IReadOnlyList<ProviderTraceEntry>? providerTrace = null;
try { /* existing code */ }
catch (...) { /* existing */ }

// NEW: classifier stage
IReadOnlyList<ClassifierTraceEntry>? classifierTrace = null;
try
{
    var classifierRun = await _classifierRunner.RunAsync(file, analysis, cancellationToken)
        .ConfigureAwait(false);
    classifierTrace = classifierRun.Trace;
    if (classifierRun.Candidates.Count > 0)
    {
        // Append at the END so provider candidates retain top spots. The resolver's
        // "stop at first taxonomy match" semantics mean classifier hits only win when no
        // provider candidate matched the taxonomy — exactly the right defensive default.
        var existingGenres = lookup?.GenreCandidates ?? [];
        var merged = existingGenres.Concat(classifierRun.Candidates).ToList();
        lookup = (lookup ?? LookupResult.Empty) with { GenreCandidates = merged };
    }
}
catch (Exception ex) when (ShouldIsolate(ex))
{
    _logger.LogWarning(ex, "Genre classifier stage failed for {Path}: {Message}", file.Path, ex.Message);
    errors.Add(new StageError("Classify", ex.Message));
    // Fail open — merge continues with the lookup result as-is.
}

// existing merge:
var resolved = TagMerger.Merge(existing, analysis, lookup, ...);
```

`ResolvedTrackTags` gets a new optional record field `ClassifierTrace : IReadOnlyList<ClassifierTraceEntry>?` (same pattern as the existing `ProviderTrace` and `GenreLookupTrace` fields). The merger doesn't read it — it's purely diagnostic for the UI / `explain` verb.

#### 3.7.4 Where `IEssentiaAnalysisService` for the heuristic comes from

`PipelineFactory.TryBuildEssentiaServiceAsync` already returns the singleton-per-scan service. If `analysis.genre_classifier.heuristic.enabled = true` but Essentia could not be made available (all of BPM/Key/Energy disabled, OR essentia binary missing), the heuristic classifier is silently disabled with a status report line — same pattern as Chromaprint's fingerprint dimension. `TaggerOptionsValidator` adds an info-level note when the heuristic is enabled and all three Essentia dimensions are disabled (degenerate config; classifier would never have data).

#### 3.7.5 DI registration

`IGenreClassifierRunner` is **not** registered in `ServiceCollectionComposer.AddRayTaggerHosting`. The container has no useful default because the runner's classifier list depends on per-scan `TaggerOptions` (which flags are enabled). Instead, `PipelineFactory.BuildAsync` constructs the runner per scan and hands it back via `PipelineBuildResult.ClassifierRunner` — the same shape `AnalysisRunner` and `LookupRunner` use today. Both `ScanHandler` (CLI) and `ScanCoordinator` (UI) read `built.ClassifierRunner` and pass it straight to `new TagPipeline(...)`.

When every flag is off, `PipelineFactory.BuildClassifierRunnerAsync` returns `NoopGenreClassifierRunner.Instance` — byte-identical pipeline behaviour to pre-classifier code.

### 3.8 Update `samples/tagger.example.yaml`

Add the `genre_classifier` block under `analysis:` with all flags set to `false` and inline comments pointing at this plan document. Update `CLAUDE.md`'s schema-sync rule — already covered in the existing convention.

### 3.9 Tests

| Test class                                     | What it covers                                                                  |
|------------------------------------------------|---------------------------------------------------------------------------------|
| `EssentiaJsonParserTests` (extend)             | New fields parsed; missing fields → null.                                       |
| `HeuristicGenreClassifierTests` (new)          | Score table: synthetic `EssentiaResult` per genre archetype hits the expected genre with confidence > 0.55. Edge cases: all features null, BPM at boundary, tie between House/Techno. |
| `GenreClassifierRunnerTests` (new)             | Runner aggregates classifier outputs; exception in one classifier doesn't kill the others; trace populated correctly per classifier (Ok/NoHit/Failed). |
| `TagPipelineTests` (extend)                    | Classifier candidates appended at end of `lookup.GenreCandidates`; empty classifier result leaves lookup unchanged; NoopGenreClassifierRunner is the default. |
| `TaggerOptionsLoaderTests` (extend)            | YAML round-trip with new section.                                               |
| `TaggerOptionsValidatorTests` (extend)         | min_confidence range validation; heuristic-enabled-without-essentia warning.    |
| Integration test in `RayTagger.Analysis.Tests` | Run heuristic against a real fixture from `./music/`, mark `[Trait("Category", "Integration")]`. |

---

## 4. Phase B — Tier 1 TensorFlow Classifiers

### 4.0 Role redefined after Phase A expansion

Phase A's heuristic already covers the 8 top-level electronic + adjacent genres (House, Techno, Trance, Drum and Bass, Dubstep, Hip Hop, Ambient, Downtempo). Phase B's TF classifiers are therefore re-scoped to complement, not duplicate:

- **`genre_electronic` (5–8 broad classes)** — overlaps heavily with the heuristic. Useful as a sanity-check / second opinion on tracks where the heuristic confidence is borderline (e.g. 0.55–0.65). Default `min_confidence` raised to `0.65` so it only fires when the model is genuinely confident — keeps the candidate stream clean. Still emits to `LookupResult.GenreCandidates`; redundancy with heuristic is harmless because the resolver stops at the first taxonomy match.
- **`mtg_jamendo` (87 classes)** — adds coverage the heuristic skips on purpose: Rock, Pop, R&B, Soul, Jazz, Funk, Reggae, Classical, etc. This is where Phase B genuinely earns its install cost.
- **`discogs_effnet` (400 fine-grained styles)** — the only path to **subgenre** detection. "Tech House", "Deep House", "Melodic Techno", "Liquid DnB", "Neurofunk", "Acid Techno" — Phase A explicitly stays out of subgenre territory; the discogs-effnet model is what fills that gap. Subgenre candidates feed `LookupResult.SubGenreCandidates` (not `GenreCandidates`) so the resolver's subgenre search picks them up.

### 4.0a Label canonicalisation pitfall (added during Phase A retrospective)

The §5.1a normaliser handles `_`/`-`/`/` → space and a rough depluralisation. It does NOT handle:

- **Symbol substitution**: TF models emit labels like `drum_n_bass` (lit. "drum n bass") and `r&b` ("r&b"). The taxonomy entries are `Drum and Bass` and `R&B`. Whole-word regex `\bDrum and Bass\b` doesn't match `\bdrum n bass\b` because the word `and` is missing.
- **Abbreviations**: `dnb`, `d&b`, `lofi` (lower-case "lofi" doesn't whole-word match `Lo-fi` in the taxonomy).
- **Multi-token splits**: the discogs-effnet model emits `Deep House`, `Tech House` — these are correctly normalised but the resolver's whole-word search for the parent genre `House` already matches both. That's by design.

**Mitigation** in Phase B: each TF wrapper applies a model-specific label-remap dictionary **before** the §5.1a normaliser. The map is bundled with the model's `labels.json` (one extra file: `labels_remap.json`) and lists model-emitted labels → canonical taxonomy phrases. Examples:

```json
{
  "drum_n_bass":   "Drum and Bass",
  "drum-n-bass":   "Drum and Bass",
  "dnb":           "Drum and Bass",
  "d&b":           "Drum and Bass",
  "r&b":           "R&B",
  "rnb":           "R&B",
  "lofi":          "Lo-fi",
  "lo_fi":         "Lo-fi",
  "trip_hop":      "Trip Hop"
}
```

Labels not in the remap fall through to the normaliser unchanged. Resolver scope is untouched.

**Phase B1 smoke-test refinements (2026-05-22).** The B1 manual test on both pipelines surfaced two corrections to the original §4.0a remap design:

1. **The remap key is the FULL model label**, not an isolated substring. Discogs-effnet emits `Electronic---Tech Trance` (Discogs's parent-substyle convention with `---` as separator). The .NET normaliser handles `---` → space cleanly, so the resolver finds `Trance` + `Tech` as a natural genre+subgenre composition. Most of the 400 Discogs labels resolve without remap entries; only labels with abbreviations (`Electronic---Drum n Bass` where "n" lacks the "and") or hyphenated taxonomy targets (`Lo-fi`) need explicit mapping.
2. **Genre_electronic emits lowercase labels** (`dnb`, `house`, `techno`, `trance`, `ambient`). The .NET resolver matches case-insensitively, so only `dnb → Drum and Bass` needs a remap entry — the other four are no-ops. The original speculative entries (`drum_n_bass`, `drum-n-bass`, `d&b`) were dead code per the actual model output and have been removed.

**Remap coverage analysis (post-B1, full vocabulary).** A Python dev tool simulates the full Python-remap → .NET-normaliser → .NET-resolver pipeline against every label of every model and reports `MATCH / NEAR-MISS / OOV`. Lives at `tools/raytagger-genre-classifier/dev/analyze_remap_coverage.py`. Coverage at end of B1:

| Model | Matched | Near-miss (skipped, deliberately ambiguous) | OOV (correctly dropped) |
| --- | ---: | ---: | ---: |
| `electronic` | 5 / 5 (100%) | 0 | 0 |
| `jamendo` | 33 / 87 (38%) | 11 | 43 |
| `discogs-effnet` | 214 / 400 (53.5%) | 57 | 129 |

The "missed" labels are not model deficiencies — they're either:
- Genuinely out-of-taxonomy (Blues, Classical, Latin, Reggae, Folk/World/Country, Brass & Military, Non-Music, Stage & Screen for Discogs; 60s/70s/folk/country/metal/grunge for Jamendo) — the resolver correctly drops them.
- Deliberately ambiguous standalone words (`dance`, `disco`, `industrial`, `minimal`, `progressive`) that could map to multiple taxonomy entries with different parents. The opinionated default would be wrong roughly half the time; better to drop and let the user write a mapping rule if their library has many such tracks.

Re-run the analyzer when adding a new TF model, when editing `taxonomy.yaml`, or when modifying `remap/*.json` to catch regressions.

### 4.0c Aggregation across fine-grained classifier classes

**Motivation.** A 400-class softmax (discogs-effnet) spreads probability mass over many subgenre classes that share a taxonomy parent. The user's reported example: `One Self - Bluebird.mp3` top-10 had one Downtempo pick at p=0.156 and **six Hip-Hop variants** with individual p ∈ [0.07, 0.11] summing to 0.401 — but the existing first-taxonomy-match resolver picked Downtempo because that was the single highest p. Aggregation captures the marginal-probability signal: summing per parent genre across the top-K identifies "the model is collectively most confident about Hip Hop, even though it can't commit to one subgenre".

**Formula.** Per taxonomy parent genre `G`:

$$\text{score}(G) = \sum_{i \in \text{Top-K}} p_i \cdot \mathbb{1}[\text{match}(c_i) = G] \cdot \mathbb{1}[p_i \geq \tau_{\text{floor}}]$$

Where `match(c_i)` is the longest-match parent the existing `TaxonomyGenreMatcher` finds in the (normalised) candidate label, and `τ_floor` (`aggregate_per_candidate_floor`, default 0.02) cuts off long-tail noise.

**Mathematically**: this is marginalisation of the softmax over hierarchical sub-classes. `P(track ∈ G) = Σ P(track ∈ c)` for every class `c` whose parent is `G`. The aggregation makes that implicit-from-the-model statistic explicit.

**Three outcome modes** (controlled by `aggregate_min_total` and `aggregate_fallback_on_diffuse`):

| Scenario | Behaviour | Emitted source suffix |
|---|---|---|
| One or more parents above `aggregate_min_total` (default 0.25) | All clearing parents emit as aggregated candidates, sorted desc by sum | `:aggregated` |
| No parent above threshold, fallback on (default true) | Highest-sum parent emits anyway as a single best-effort candidate | `:aggregated-fallback` |
| No parent above threshold, fallback off | No aggregated candidate; raw top-K still emits unchanged | — |

**Key design properties**:

1. **Additive, not replacing.** Aggregated candidates are PREPENDED to the raw top-K. The resolver's first-taxonomy-match logic naturally picks the aggregated entry (typically the highest p in the list), but if the aggregated value is somehow off-taxonomy, raw candidates still get their chance. Trace shows both for transparency.
2. **Aggregation works on the FULL prediction list**, not the post-`min_confidence` filtered subset. Otherwise a strict `min_confidence=0.5` would gut the aggregation input and defeat its purpose — covered by `Aggregation_works_on_full_top_K_even_when_min_confidence_filters_raw_emit` test.
3. **Subgenre detection is suppressed** for aggregated candidates. The aggregated `Value` is the bare parent genre name (e.g. `"hip hop"`), so the resolver's subgenre search finds nothing in the "rest" after the parent strip. Users who need fine-grained subgenre detection from discogs-effnet should set `aggregate_top_k: false` for that model. This is the v1 trade-off; future iterations could populate `LookupResult.SubGenreCandidates` from the contributing raw labels.
4. **`Hip Hop---Trip Hop` resolves to Trip Hop, not Hip Hop**, because `Trip Hop` (8 chars) beats `Hip Hop` (7 chars) in the resolver's longest-match tiebreak. This is consistent with the resolver's general behaviour but worth noting — users who think discogs's substyle prefix indicates parent intent will be surprised. Documented in the `Aggregates_top_k_by_taxonomy_parent_when_enabled` test.

**Defaults per model**:

| Model | `aggregate_top_k` default | Rationale |
|---|:-:|---|
| `discogs_effnet` | **true** | 400-class fine-grained vocabulary, principal beneficiary |
| `mtg_jamendo` | false | 87 classes, but most are top-level genres already (rock, pop, jazz, …) — aggregation barely changes anything |
| `genre_electronic` | false | 5 classes, every label IS a parent genre — aggregation is a no-op |

**User overrides**: every flag is per-model in `tagger.yaml` (see `samples/tagger.example.yaml`). All 4 knobs are independently configurable.

**Performance**: aggregation runs in C# after JSON parse, before result return. Cost = O(K × G) regex matches where K ≤ top-k (default ≤ 10) and G = number of taxonomy parents (~17). Measured: <1 ms per track on Apple Silicon — negligible against the ~1.5 s subprocess cost.

### 4.0b Subprocess startup cost — known limitation

Each invocation of the Python bridge spins up a fresh process: Python interpreter (~50 ms) + numpy (~100 ms) + tensorflow (~1500 ms) + essentia (~500 ms) + model graph load (~1000 ms per .pb file). The B1 smoke test estimated **~3 seconds per track per model**; the B6 integration test against `./music/` on Apple Silicon measured **~1.5 seconds per track per model** (`electronic` 1507 ms, `discogs-effnet` 1500 ms). The estimate is conservative — actual cost is better but in the same order of magnitude. The table below keeps the conservative ~3 s estimate for planning headroom.

At realistic library sizes the cost multiplies:

| Library size | Models enabled | Wall-clock penalty |
| ---: | ---: | --- |
| 100 tracks | 1 (electronic only) | ~5 minutes |
| 1 000 tracks | 1 | ~50 minutes |
| 1 000 tracks | 3 (all TF models) | **~2.5 hours of pure subprocess overhead**, before any inference work |
| 10 000 tracks | 3 | ~25 hours |

This is **acceptable for opt-in v1** because:

- All TF flags default to off; users opt-in deliberately.
- A second scan over the same library hits the .NET-side analysis cache (Essentia results) and skips the classifier entirely for unchanged files.
- The heuristic classifier covers the common case at zero subprocess cost.

**Mitigation deferred to a follow-up phase** (not blocking Phase B v1 ship). Two designs evaluated:

- **Daemon mode** (Python process pool with stdin/stdout protocol). Best throughput — model graphs stay resident across tracks, per-track cost drops to inference-only (~200–500 ms). Cost: complex C# side (process pool, lifecycle, graceful shutdown). Defer until measured pain.
- **Batch mode** (`--audio-list /path/to/manifest.txt` reads many paths, emits one JSON per line). Simpler than a daemon — still one process per scan-batch, no protocol. Cost: breaks the streaming-channel pipeline pattern (Phase A6); accumulating all paths before kicking the classifier means deferred outcomes and worse UX during long scans.

**Decision for Phase B**: ship the per-track subprocess design. Surface the overhead in the CLI startup banner ("TF classifier overhead: ~3 sec/track") so users with large libraries understand the tradeoff. Capture actual end-to-end timings during the Phase B6 integration test against `./music/`; if the measured overhead exceeds the estimates significantly, revisit with daemon mode.

### 4.1 Python bridge script

Location: `tools/raytagger-genre-classifier/`. Files:

- `raytagger_genre_classifier.py` — main script.
- `requirements.txt` — pinned versions of `essentia-tensorflow` and any transitive needs.
- `README.md` — install instructions (`pip install -r requirements.txt`), how Tagger invokes it, troubleshooting.

**CLI contract** (Tagger calls):

```
python raytagger_genre_classifier.py \
  --model <electronic|jamendo|discogs-effnet> \
  --audio <path-to-audio-file> \
  --models-dir <abs-path-to-models-directory> \
  --top-k 5
```

**stdout** (single line, JSON):
```json
{"model": "electronic", "predictions": [
  {"label": "house",  "probability": 0.78},
  {"label": "techno", "probability": 0.12},
  {"label": "trance", "probability": 0.05}
]}
```

**stderr**: diagnostic only. Empty on success.

**Exit codes**: 0 on success; 2 if model file missing (tells C# to attempt download); 3 if audio file unreadable; 1 on any other error. C# wrapper logs stderr at the appropriate level.

Script imports `essentia.standard.MonoLoader`, `TensorflowPredictMusiCNN` (for MusiCNN inputs) or `TensorflowPredictEffnetDiscogs` (for Effnet inputs), runs the appropriate preprocessing chain, and applies the model. Label vocabularies are bundled as JSON files next to the script (`labels_electronic.json`, `labels_jamendo.json`, `labels_discogs.json`) — they're tiny and never change for a frozen model version.

### 4.2 `TensorflowGenreClassifier` C# wrapper

In `src/RayTagger.Analysis/Genre/TensorflowGenreClassifier.cs`:

```csharp
public sealed class TensorflowGenreClassifier : IGenreClassifier
{
    public string Name { get; }              // "essentia-tf-electronic" etc.
    private readonly string _modelKey;       // "electronic" | "jamendo" | "discogs-effnet"
    private readonly string _pythonExe;
    private readonly string _scriptPath;
    private readonly string _modelsDir;
    private readonly NativeProcessRunner _runner;
    private readonly TimeSpan _timeout;
    private readonly double _minConfidence;
    private readonly ILogger<TensorflowGenreClassifier> _logger;

    public async Task<GenreClassificationResult> ClassifyAsync(TrackFile file, CancellationToken ct)
    {
        var args = new[] {
            _scriptPath,
            "--model", _modelKey,
            "--audio", file.Path,
            "--models-dir", _modelsDir,
            "--top-k", "5"
        };
        try
        {
            var process = await _runner.RunAsync(_pythonExe, args, _timeout, ct).ConfigureAwait(false);
            if (!process.Succeeded)
            {
                _logger.LogWarning("TF classifier {Name} exited {Code}: {Stderr}",
                    Name, process.ExitCode, process.StandardError);
                return GenreClassificationResult.Empty;
            }
            return ParseStdout(process.StandardOutput);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TF classifier {Name} failed for {Path}", Name, file.Path);
            return GenreClassificationResult.Empty;
        }
    }

    private GenreClassificationResult ParseStdout(string stdout) { … }
}
```

One C# instance per enabled model. The three model keys map to `Name` values:

| Config flag                       | `_modelKey`        | `Name`                          |
|-----------------------------------|--------------------|---------------------------------|
| `tensorflow.genre_electronic`     | `electronic`       | `essentia-tf-electronic`        |
| `tensorflow.mtg_jamendo`          | `jamendo`          | `essentia-tf-jamendo`           |
| `tensorflow.discogs_effnet`       | `discogs-effnet`   | `essentia-tf-discogs-effnet`    |

`Source` in the candidate becomes `classifier:essentia-tf-electronic` and so on — the resolver matches against taxonomy.yaml whole-words exactly as it does for provider candidates, so label normalisation (e.g. discogs "Tech House" → taxonomy "House") is taxonomy.yaml's job.

Each TF wrapper applies a **two-stage label pipeline** before emitting:

1. Look up the raw model label in `labels_remap.json` (per-model file shipped next to `labels.json`, see §4.0a). When the label is in the remap, the canonical taxonomy phrase replaces it. Labels not in the remap pass through unchanged.
2. Apply `ClassifierLabelNormaliser.Normalise` (§5.1a) — lowercase, `_/-//` → space, depluralisation, trim.

The remap is the only deliberate canonicalisation: `dnb` / `drum_n_bass` / `d&b` all become `Drum and Bass` here; the normaliser then lowercases to `drum and bass` and the resolver whole-word matches the taxonomy entry `Drum and Bass`. The discogs-effnet model also benefits — `Drum n Bass` and `Tech-House` get standardised to taxonomy phrases.

**Discogs-effnet → SubGenreCandidates.** The discogs-effnet wrapper emits "Tech House", "Deep House", "Melodic Techno", "Liquid", "Neurofunk" etc. — these are subgenres in `taxonomy.yaml`, not genres. The wrapper inspects each predicted label after the remap+normaliser pipeline and routes it to `GenreCandidates` (parent-genre matches) **or** `SubGenreCandidates` (subgenre-only matches) based on which list the candidate whole-word matches. Labels that match both (e.g. `House` appears in `Tech House`) emit one entry per slot so the resolver picks them up symmetrically. The `genre_electronic` and `mtg_jamendo` wrappers always emit to `GenreCandidates` because their vocabularies are top-level only.

### 4.3 Model files & download

**Where the model files live:** `<data-dir>/RayTagger/models/<model-key>/` containing:

- `model.pb` — the TensorFlow protobuf.
- `labels.json` — label vocabulary.
- `labels_remap.json` — per-model canonicalisation map (see §4.0a). Ships with the Python bridge in the repo (small file, hand-curated) and is not downloaded — versioned alongside `raytagger_genre_classifier.py`.
- `metadata.json` — sha256 hash, source URL, license note ("Essentia CC-BY-NC-SA — non-commercial only when bundled").

**Download mechanism:** extend `native-tools.yaml` schema with a `models:` section that mirrors `tools:`. The existing `NativeToolBootstrapper` is parameterised on URL + checksum + RID — adding a "models" downloader is a small extension, not a rewrite. **First-run UX**: on scan startup, if a TF flag is enabled and the model is missing, status reporter announces "Modell wird heruntergeladen…" and the scan blocks on the download. Failure → that model classifier is disabled for the scan with a clear message; other classifiers continue.

**Manifest entries** (added to `samples/native-tools.example.yaml`):

```yaml
models:
  essentia-tf-electronic:
    version: 1
    sources:
      - url: https://essentia.upf.edu/models/classification-heads/genre_electronic/genre_electronic-discogs-effnet-1.pb
        sha256: <pin-after-first-download>
      - url: https://essentia.upf.edu/models/classification-heads/genre_electronic/genre_electronic-discogs-effnet-1.json
        sha256: <pin-after-first-download>
        rename_to: labels.json
  # ... two more entries for jamendo and discogs-effnet ...
```

The exact URLs and hashes get pinned during implementation by downloading once and reading off the actual hashes. Schema validator rejects entries without sha256 (security gate).

### 4.4 Probing & graceful degradation

Three independent probe questions at scan startup, each with a clean status line:

1. **Python executable** — `python3 --version` (or whatever the user configured). Failure → all TF classifiers disabled.
2. **Helper script present** — file exists at the configured `script_path` or auto-discovered path. Failure → all TF classifiers disabled.
3. **Per-model files present** — for each enabled model, check `model.pb` + `labels.json` in `<models-dir>/<model-key>/`. Missing → try to download via the bootstrap; persistent failure → that specific model disabled.

Status reporter pattern follows the existing Essentia/Chromaprint reporting in `PipelineFactory`. The CLI's startup banner gains lines like:

```
Genre-Classifier
  heuristic                 aktiv (min_confidence=0.55)
  essentia-tf-electronic    deaktiviert (kein Python gefunden)
  essentia-tf-jamendo       aus (Konfig)
  essentia-tf-discogs-effnet aktiv (Modell unter ~/Library/.../models/discogs-effnet/)
```

### 4.5 Configuration

Covered in §3.6 above — same `genre_classifier` block, the TF flags fill in.

### 4.6 Pipeline integration

`LookupRunnerBuilder.HasAnyClassifierEnabled(genreOptions)` returns true if any of the four flags is set. `BuildClassifiers` instantiates only the enabled ones, in this order: heuristic first (cheapest), then the three TF models. Order doesn't affect correctness — the resolver picks the best candidate by taxonomy match, not by classifier order — but heuristic-first means we get a result line in the log sooner.

### 4.7 Tests

| Test class                                       | What it covers                                                              |
|--------------------------------------------------|-----------------------------------------------------------------------------|
| `TensorflowGenreClassifierTests` (new, unit)     | Stub `NativeProcessRunner` returning canned stdout JSON; verify parsing.   |
| `TensorflowGenreClassifierTests` (integration)   | Actual Python+models on disk; `[Trait("Category","Integration")]`; skip when probe fails. |
| `NativeToolBootstrapperTests` (extend)           | `models:` section parsing + download.                                       |
| `TaggerOptionsLoaderTests` (extend)              | `tensorflow.*` flags round-trip.                                            |
| `TaggerOptionsValidatorTests` (extend)           | per-model `min_confidence` range.                                           |
| Python script unit tests (new)                   | `pytest` in `tools/raytagger-genre-classifier/tests/` if the user has the Python env set up. Not part of `dotnet test`. |

---

## 5. Cross-cutting concerns

### 5.1 Order of merge into `LookupResult.GenreCandidates`

Append, don't prepend. Online providers retain top spots in the list because their candidates have stronger metadata grounding (track ID, MBID). Classifier hits sort by their own confidence among themselves. The resolver iterates the list and "stops at the first candidate that produces any taxonomy match", so the semantics are:

- **Provider produced a taxonomy-matching candidate** → that wins; classifiers never consulted.
- **Provider produced only off-taxonomy values (e.g. Discogs "Electronic")** → resolver walks past them, reaches the classifier hits, picks the first taxonomy-matching classifier label.
- **Both provider and classifier produced off-taxonomy values** → fallback path takes `genreCandidates[0]` (still a provider, not the classifier — by design, prevents a low-confidence classifier from polluting an off-taxonomy-but-meaningful provider label like "Trip-Hop").
- **Lookup disabled / no provider hits + classifier hits available** → classifier list is the entire candidate stream; first taxonomy-matching classifier hit wins.

This ordering gives classifiers the explicit role of "rescue the genre detection when providers don't know" without ever overriding a provider that *does* know — the right defensive default for v1. A future config toggle could invert this; out of scope here.

### 5.1a Label normalisation (classifier → taxonomy)

`TaxonomyGenreResolver` matches whole-word case-insensitive. Two pitfalls:

1. Model labels like `"deep_house"` or `"drum-n-bass"` use underscores/dashes; taxonomy entries use spaces (`"Deep House"`). Whole-word regex `\bdeep_house\b` matches *only* the literal underscore form because `_` is a word character.
2. Models occasionally emit prefixes like `"electronic / house"` or pluralised forms `"trances"`.

**Normalisation pipeline applied inside each classifier before emitting `GenreCandidate.Value`** (one helper, used by both heuristic and TF wrapper):

```
raw label → lowercase
         → replace [_\-/] with single space
         → collapse multiple spaces
         → strip trailing 's' iff length > 4 AND result still ASCII letters/spaces (rough English depluralisation)
         → trim
```

So `"deep_house"` → `"deep house"`, `"electronic/house"` → `"electronic house"` (two whole words — `\bhouse\b` still matches in the taxonomy regex), `"trances"` → `"trance"`. Casing is taxonomy's job (the regex is case-insensitive; the taxonomy provides the canonical PascalCase when writing).

Heuristic's labels start in canonical form already (`"House"`, `"Techno"`, `"Trance"`) — they go through the same normaliser for symmetry but the operation is a no-op for them.

Edge case: TF models can emit a label like `"non_electronic"` or `"unknown"`. These are not in the taxonomy and harmlessly skipped by the resolver. We do *not* hard-filter them in the classifier — the trace stays honest, the resolver does its job.

### 5.2 Logging & trace

`CandidateTraceEntry` already captures `Source`. The classifier source string format (`classifier:<name>`) keeps the trace human-readable without a schema change. The CLI's `explain` verb gets a new "Klassifikator-Vorschläge" section listing each classifier's top-3 candidates with confidence — purely diagnostic, no behaviour change.

### 5.3 UI

For Phase A: no UI change required. The classifier candidates flow through the same `GenreLookupTrace` the existing `AppliedRulesDialog` renders. The "API" column highlight already covers off-taxonomy values.

Optional for Phase A/B (out of scope for the first cut, listed for completeness): a new "Klassifikator" column in the results grid showing the top classifier hit and its confidence, parallel to the existing genre column. Implementing this requires adding a field to `ResolvedTrackTags` — defer until after the classifier itself is proven on real tracks.

### 5.4 Migration & rollback

- Code-level rollback: every change lands behind a flag (`enabled: false` defaults). Removing the feature reduces to deleting the decorator wrapping and the `genre_classifier` section in YAML; existing configs without that section keep working because POCO defaults are all `Enabled = false`.
- Schema migration: `version` in `tagger.yaml` stays at 1 — we're only adding optional fields with backwards-compatible defaults. Bump to 2 only if a future change is breaking.
- Cache: classifier output is not separately cached. The Essentia cache (per file+mtime) is shared with the heuristic; TF models are re-invoked per scan. If TF inference proves slow on large libraries, add a sidecar cache keyed by `(file, mtime, model-key)` — but only after measuring on real workloads.

### 5.5 Performance budget

- Heuristic: ~0 ms after Essentia run (pure arithmetic on already-parsed JSON).
- TF model: 200–500 ms per track per model on Apple Silicon (rough estimate based on Essentia-TF published benchmarks). With three models enabled and parallelism=8, a 1000-track library adds ~3 minutes wall-clock. Acceptable for opt-in. If the user runs many libraries, the per-track cache (§5.4) makes the second run free.
- Memory: each Python invocation forks fresh. No long-running Python daemon — keeps the .NET process isolated from TF's memory characteristics. Cost is ~200ms Python startup overhead per track; if that becomes a bottleneck on large libraries, batch multiple tracks per Python invocation (deferred optimisation).

### 5.6 Security

- Subprocess uses `ProcessStartInfo.ArgumentList` (already enforced by `NativeProcessRunner`) — no shell injection surface.
- Downloaded model files validated by sha256 in the manifest (rejected if hash mismatch).
- Python script ships with Tagger — user-installed Python interpreter is the only foreign code path.
- Tagger never executes arbitrary Python; the script path is pinned to a known location and rejected if it escapes the expected directory (defence in depth against a malicious tagger.yaml override).

### 5.7 Deferred optimisations

Tracked here so they don't get lost when each phase wraps. None block Phase B from shipping — each is a "fix when measured pain shows up" item.

| # | Source phase | Optimisation | Why deferred |
| --- | --- | --- | --- |
| 1 | §4.0b (B1) | **Long-running Python daemon** (process pool with stdin/stdout protocol) instead of per-track subprocess. Per-track cost would drop from ~3 s to inference-only (~200–500 ms). | Complex C# side (process lifecycle, graceful shutdown). Measure actual pain in B6 against a real library before designing. |
| 2 | §4.0b (B1) | **Batch mode** (`--audio-list manifest.txt` → one JSON per line). Simpler than daemon. | Breaks the streaming-channel pipeline pattern (Phase A6 accumulates all paths before kicking the classifier → deferred outcomes → worse UX during long scans). |
| 3 | §4.0a / §5.1a | **Hyphenated taxonomy entries** (`Lo-fi`, `Jump-up`) don't match through the normaliser (which converts `-` → space). Heuristic doesn't emit such labels; TF labels are remapped to non-hyphenated taxonomy phrases where possible. | The "fix" would require resolver changes — out of scope for v1. Workaround: edit `taxonomy.yaml` to drop the hyphen if you need these subgenres detected. |
| 4 | B4 | **Cross-model file dedup**: `discogs-effnet-bs64-1.pb` (18 MB embedding) is downloaded 3× when all three models are enabled — once per model directory. | Disk is cheap (~54 MB redundant). Dedup would need a content-addressed store + symlinks, plus per-OS link semantics; not worth the complexity for v1. |
| 5 | B4 | **Resume-on-failure**: if a 3-file model download breaks at file 2, the next `EnsureModelAsync` re-downloads all 3 from scratch (staging dir is purged in the `finally`). | TF model files are 5–18 MB each; a full retry on a flaky network is acceptable. Becomes worth fixing only if model archives grow ≫100 MB. |

---

## 6. Implementation order

### Phase A — Heuristic classifier (✅ DONE)

1. **Phase A1** ✅ — Extend `EssentiaResult` + `EssentiaJsonParser` + tests. Independent of everything else.
2. **Phase A2** ✅ — Add `IGenreClassifier` interface + `GenreClassificationResult` + the `ClassifierLabelNormaliser` helper (§5.1a).
3. **Phase A3** ✅ — Implement `HeuristicGenreClassifier` + scoring + tests (unit). Initial lineup was House/Techno/Trance; extended in the retrospective pass to the full **8-genre lineup** (House, Techno, Trance, Drum and Bass, Dubstep, Hip Hop, Ambient, Downtempo). `BeatsLoudnessProfile` enum extended with `VeryLow` and `Low` bands; per-genre `danceabilityScores` triple replaces the original two-value form.
4. **Phase A4** ✅ — Add `GenreClassifierOptions` + `HeuristicClassifierOptions` + `TensorflowClassifierOptions` + `TensorflowModelOptions` to TaggerOptions; update validator + loader + `tagger.example.yaml`.
5. **Phase A5** ✅ — Add `IGenreClassifierRunner` + `NoopGenreClassifierRunner` + `GenreClassifierRunner` + `ClassifierTraceEntry` + tests.
6. **Phase A6** ✅ — Wire `TagPipeline` constructor + `ProcessFileAsync` to the new stage; extend `ResolvedTrackTags` with optional `ClassifierTrace` (`[init]` property on the existing record, no constructor change). Updated **10 `new TagPipeline(...)` call sites** (2 production + 8 tests):
   - `src/RayTagger.Cli/Commands/ScanHandler.cs`
   - `src/RayTagger.Ui/Services/ScanCoordinator.cs`
   - `tests/RayTagger.Core.Tests/Pipeline/TagPipelineTests.cs` (8 occurrences — pass `NoopGenreClassifierRunner.Instance`)
7. **Phase A7** ✅ — Wire `PipelineFactory` to construct the heuristic classifier + `GenreClassifierRunner` when enabled; `ServiceCollectionComposer` registers Noop as default.
8. **Phase A8** ✅ — Integration test against `./music/` confirms 8 candidates emitted, ranked by score, against the live Essentia binary.

### Phase B — TensorFlow classifiers (in progress)

1. **Phase B1** ✅ — `raytagger_genre_classifier.py` + `requirements.txt` + per-model `remap/*.json` + smoke-tested against the `electronic` (two-stage) and `discogs-effnet` (single-stage) pipelines with real Essentia models on real DJ tracks. Key implementation notes:
   - **Auto-discovery of TF input/output node names** from the model's published `metadata.json` (renamed to `labels.json` per the manifest convention). Replaces hardcoded Essentia defaults that broke on heads using `model/Softmax` instead of the default `model/Sigmoid`.
   - **Log-silencing** of TF + Essentia info-level chatter on stderr unless `RAYTAGGER_DEBUG=1`. C# wrapper sees clean stderr (two unavoidable absl/MLIR init lines remain — emitted by TF's C++ side before our env var is read).
   - **Verified subprocess timing** in §4.0b — ~3 sec/track/model on Apple Silicon.
2. **Phase B2** — Add `TensorflowGenreClassifier` C# class with the two-stage label pipeline (remap → §5.1a normaliser, see §4.2) + unit tests with stubbed `NativeProcessRunner`. Discogs-effnet wrapper additionally splits output between `GenreCandidates` and `SubGenreCandidates`.
3. **Phase B3** — `TensorflowClassifierOptions` POCO already shipped in Phase A4 (default `Enabled = false`). B3 raises the default `min_confidence` for `genre_electronic` from `0.50` to `0.65` per §4.0 (redundancy with heuristic motivates a higher floor). YAML round-trip test extended to cover the new defaults.
4. **Phase B4** ✅ — Extended `native-tools.yaml` schema with `models:` section + download path in `NativeToolBootstrapper`. Implementation notes:
   - **POCO**: added `NativeModelEntry` (version + files list) and `NativeModelFile` (URL + SHA-256 + optional `rename_to`). `NativeToolsManifest.Models` is a dict keyed by model-key (`electronic`/`jamendo`/`discogs-effnet`).
   - **Validator**: per-model SHA-256 hex check, HTTPS-only URLs, and a **path-traversal guard on `rename_to`** (rejects slashes, backslashes, `..`, absolute paths — `rename_to` feeds `Path.Combine` against the cache root).
   - **Cache layout** is flat: `<cache>/models/<model-key>/<files>` with a `.version` sentinel file. Manifest version bump → sentinel mismatch → re-download. Lives **next to** the tools cache, not inside it (different from `CacheRoot` which appends `/tools/` — see `ModelsRoot` in `NativeToolBootstrapper`).
   - **Atomic-ish promote**: download all files into a staging directory + write sentinel last + rename staging into place. Partial-download cleanup happens in `finally`.
   - **Concurrency**: separate `_modelsInFlight` `ConcurrentDictionary<…, Lazy<…>>` mirrors the existing `_inFlight` map for tools. Parallel `EnsureModelAsync(modelKey)` calls share a single download Task; transient failures evict the entry so retries replay from scratch.
   - `remap/*.json` is **not** downloaded — it ships in the repo alongside the Python bridge per §4.0a.
   - SHA-256s for the 5 actual model files (3 model entries with `discogs-effnet-bs64-1.pb` shared 3× — redundant but acceptable for v1) pinned in `samples/native-tools.example.yaml` from real downloads on 2026-05-22.
5. **Phase B5** ✅ — Wired `PipelineFactory` to construct TF classifiers when enabled. Implementation notes:
   - **Shared dependency probing**: Python interpreter (`python3` via PATH, or `tensorflow.python_executable` override) probed once before any model is built. If missing, every enabled TF model reports the same failure line so the user sees the cause once per slot in the startup banner.
   - **Script auto-discovery** walks upward from `AppContext.BaseDirectory` looking for `tools/raytagger-genre-classifier/raytagger_genre_classifier.py` — covers both `dotnet run` from `bin/Debug/net10.0/` (5 levels below the repo root) and published self-contained binaries that ship the tools dir next to the exe.
   - **Per-model bootstrap**: each enabled model calls `INativeToolBootstrapper.EnsureModelAsync(modelKey)` (B4's API). Failures are isolated — one model failing to download doesn't block the others. When no `native-tools.yaml` is reachable, the wiring falls back to a pre-staged model directory at `<models-dir>/<key>/` if one exists, otherwise reports missing.
   - **`NativeToolResolver.Bootstrapper`** exposed as a public read-only property so `BuildClassifierRunnerAsync` can reach the bootstrapper without re-instantiating one (shares the concurrent-download deduplication).
   - **§4.0b subprocess-cost banner** fires exactly when at least one TF classifier was successfully built — surfaces the ~3 sec/track/model overhead and links the plan section so a user with a 1k-track library sees the tradeoff before scan-time.
   - **Per-model status lines** show each active TF classifier with its display name, `min_confidence`, and `top_k` — same shape as the heuristic's status line so the UX is consistent.
6.5. **Phase B6.5** ✅ — Aggregation across fine-grained classifier classes (see §4.0c). Triggered by the user's observation that the discogs-effnet top-1 misses the "spread across multiple subgenres of the same parent" signal. Implementation:
   - Extracted `TaxonomyGenreMatcher` (public class in `RayTagger.Core.Mapping`) from the resolver's internal `CompiledTaxonomy` — single source of truth for whole-word taxonomy matching.
   - Added 4 fields to `TensorflowModelOptions`: `AggregateTopK` (default false; true for `DiscogsEffnet`), `AggregateMinTotal` (default 0.25), `AggregatePerCandidateFloor` (default 0.02), `AggregateFallbackOnDiffuse` (default true). All validated by `TaggerOptionsValidator` ∈ [0,1].
   - New `AggregationConfig` record bundles the matcher + 3 numeric knobs into the `TensorflowGenreClassifier` constructor as an optional parameter. Backward-compatible (existing callers pass null = no aggregation).
   - `PipelineFactory.TryBuildTfClassifierAsync` constructs the `AggregationConfig` when the model opts in AND a non-empty `Taxonomy` is loaded.
   - 7 new unit tests cover: the user's exact example, the null-aggregation legacy path, per-candidate floor filtering, diffuse output with/without fallback, the "min_confidence doesn't gut aggregation" invariant, and the "no taxonomy match" no-op case.
   - Documentation: §4.0c new section + sample YAML extended.

6. **Phase B6** ✅ — Integration tests for the TF classifier against real Python + Essentia models downloaded on first run + real audio from `./music/`. Lives in `tests/RayTagger.Analysis.Tests/Genre/TensorflowGenreClassifierIntegrationTests.cs`, marked `[Trait("Category", "Integration")]` so it stays out of the default unit-test pass. Two tests:
   - **`Genre_electronic_classifies_a_real_track_end_to_end`** — fetches the electronic model files (~18 MB embedding + ~500 KB head + labels), runs the classifier, verifies 5 candidates + normalised labels + descending confidence order + ≤10s wall-clock guard against §4.0b regression.
   - **`Discogs_effnet_candidates_resolve_through_taxonomy_end_to_end`** — the chain test the user asked for: discogs-effnet predictions → Python remap → C# §5.1a normaliser → `TaxonomyGenreResolver` → asserts at least one candidate produces a taxonomy match. Verified locally with `One Self - Bluebird.mp3`: top-10 includes `electronic downtempo` which resolves to `Downtempo`.
   - **Test-side dedup**: cross-model file dedup keyed by URL (not filename) — saves re-downloading the 18 MB embedding `.pb` between tests. Initial filename-based dedup was a bug (would copy `electronic/labels.json` over `discogs-effnet/labels.json`, breaking class-count assertion).
   - **§4.0b measurement**: actual per-track cost on Apple Silicon was **~1.5 s/model/track** (vs. ~3 s estimate). Plan §4.0b updated with the real number; the conservative estimate stays in the table for headroom planning.
   - Silent skip pattern: when Python/essentia-tensorflow/`./music/`/network are unavailable, tests early-return with a `SKIP:` line in xUnit output instead of throwing — matches the existing `HeuristicGenreClassifierIntegrationTests` style.
7. **Phase B7** ✅ — Updated `CLAUDE.md`:
   - Top-line status now includes "audio-based genre classification (heuristic + 3 TensorFlow models with per-parent aggregation)" as implemented.
   - `IGenreClassifier` abstraction description no longer hedged with "(planned)".
   - "Genre Classification" section fully rewritten — describes the implemented architecture: 8-genre heuristic lineup, 3 TF models with their min_confidence defaults and roles, §4.0c aggregation with discogs-effnet default-on, pipeline integration via `IGenreClassifierRunner` between Lookup and Merge, label normalisation + per-model remap, subprocess-cost surface in CLI banner, license note preserved.
   - Pending Work list trimmed: "Genre classification" entry removed (now done); replaced with "Genre-classifier follow-ups" pointing at §5.7 deferred optimisations (Python daemon, batch mode, file dedup, hyphenated-taxonomy handling).

After phase A is merged, the feature is usable. Phase B is additive.

---

## 7. Open questions

None. All design decisions are made above. Anything that surfaces during implementation that isn't covered here should be added to this document and re-reviewed before merging.

---

## 8. Review changelog

Captured during the self-review pass (2026-05-22) — important corrections to the first draft:

1. **Decorator pattern → pipeline stage.** Initial design wrapped `ILookupRunner` with a `ClassifierEnrichedLookupRunner` decorator. Rejected after discovering `LookupQuery` is text-only (no `TrackFile`) and `ILookupRunner.RunAsync` returns `LookupRunResult` (not `LookupResult` directly). The classifier needs the file path; threading it through `LookupQuery` would mix concerns with the HTTP providers. Replaced with a dedicated `IGenreClassifierRunner` stage called from `TagPipeline.ProcessFileAsync` between lookup and merge — `TrackFile` is already in scope there.
2. **Resolver semantics nailed down.** Confirmed by reading `TaxonomyGenreResolver.Resolve`: it iterates `genreCandidates` and stops at the first one that produces a taxonomy match. Appending classifier hits at the end therefore yields the right defensive default — classifiers only "rescue" when providers produced nothing taxonomy-shaped. The fallback path (`genreCandidates[0]` when nothing matched) keeps using provider top-1, by design.
3. **Label normalisation (§5.1a) added.** Realised that whole-word regex against `\bdeep_house\b` won't match `\bDeep House\b` in the taxonomy because `_` is a word character. All classifiers normalise `_/-` → space before emitting candidates.
4. **ResolvedTrackTags extension is non-breaking.** The record already uses `[init]`-property pattern for `GenreLookupTrace` and `ProviderTrace`; `ClassifierTrace` reuses that pattern — no record constructor change, no migration of the 30+ call sites that construct `ResolvedTrackTags`.
5. **TagPipeline call-site list expanded.** Verified by grep: 11 occurrences (2 production + 9 tests). Plan now lists them explicitly so the implementation pass doesn't miss one. Actual count during implementation was 10 (2 production + 8 tests) — one expected test site didn't exist.

### Post-Phase A retrospective (2026-05-22, after first implementation pass)

6. **Heuristic lineup expanded from 3 to 8 genres.** Original plan covered House/Techno/Trance only. Implementation extended to Drum and Bass / Dubstep / Hip Hop / Ambient / Downtempo because their DSP signatures (BPM band, beats-loudness band, dynamic complexity) are cleanly separable with the existing feature set. §3.5 was rewritten with an 8-column scoring table. Knock-on changes:
   - `BeatsLoudnessProfile` enum extended with `VeryLow` and `Low` bands for ambient / downtempo material.
   - `_danceabilityHighScore` / `_danceabilityMidScore` pair replaced by a single `_danceabilityScores: (low, mid, high)` triple — uniform with the other three-band fields.
   - Hip Hop ↔ Downtempo overlap at 90 BPM forced sharper per-band scoring: Hip Hop wins on Mid bands (mid chord motion + Mid beats-loudness from boom-bap kicks), Downtempo wins on High chord-rate + Low beats-loudness.
   - Test archetypes for the 5 new genres added; one (`Downtempo_archetype_scores_highest_for_downtempo`) tuned twice to land cleanly between Hip Hop and Ambient on a true downtempo signature (high chord-rate + soft beat, but still rhythmic enough not to ambient-out).
7. **Phase B role re-scoped.** Heuristic now covers what `genre_electronic` was originally going to be the primary source for. Phase B's value shifts to:
   - `mtg_jamendo` covers Rock/Pop/R&B/Soul/Jazz/Funk — genres the heuristic deliberately skips.
   - `discogs_effnet` is the **only** subgenre source (Tech House, Melodic Techno, Liquid DnB, …); routes to `SubGenreCandidates` not `GenreCandidates`.
   - `genre_electronic` becomes a sanity-check / second opinion; default `min_confidence` raised to 0.65.
8. **Label canonicalisation pitfall surfaced (§4.0a).** The §5.1a normaliser doesn't expand `_n_` → `and` or `&` → `and`. TF model labels (`drum_n_bass`, `r&b`) would silently fail to match taxonomy entries (`Drum and Bass`, `R&B`). Phase B now ships a per-model `labels_remap.json` consulted **before** the normaliser. Heuristic is unaffected — it already emits canonical taxonomy phrases.
9. **`ClassifierTrace` only populates when classifiers actually ran.** Initial implementation assigned `classifierTrace = runResult.Trace` unconditionally; the Noop runner's empty list polluted the diagnostic surface with empty traces on every track. Fixed to skip the assignment when `runResult.Trace.Count == 0` — distinguishes "stage skipped" from "stage ran with no hits".

### Phase B retrospective (2026-05-22, continuing)

10. **B3 asymmetric defaults are encoded in `TensorflowClassifierOptions` initialisers, not in `TensorflowModelOptions`.** Each model's `MinConfidence` is overridable per-model in `tagger.yaml`. The defaults differ on purpose: `GenreElectronic = 0.65` (overlaps with the Phase A heuristic, raised floor acts as second-opinion threshold) versus `MtgJamendo = 0.50` and `DiscogsEffnet = 0.50` (unique coverage, lower floor captures more signal). Three tests anchor the behaviour: a defaults-when-missing test, a per-model-override test (verifies independent configurability), and a design-documentation test (asserts `GenreElectronic > MtgJamendo` so a future refactor that homogenises defaults can't slip through silently).

## 9. References

- Discussion transcript that produced this plan (Tier 1/3 selection).
- Essentia models index: https://essentia.upf.edu/models.html
- `essentia-tensorflow` PyPI: https://pypi.org/project/essentia-tensorflow/
- Existing `EssentiaAnalysisService.cs` for the subprocess-orchestration pattern this plan mirrors.
- Existing `LookupRunner.cs` / `LookupRunnerBuilder.cs` for the wrapping target.
- Existing `TaxonomyGenreResolver.cs` for the resolver code path classifiers feed into.
