using RayTagger.Core.Models;

namespace RayTagger.Core.Configuration;

/// <summary>
/// Root configuration POCO. The shape mirrors <c>samples/tagger.example.yaml</c>; the example file
/// IS the documented contract — update both in lockstep. Properties are <c>get/set</c> rather than
/// records because YamlDotNet's default deserializer prefers settable properties.
/// </summary>
public sealed class TaggerOptions
{
    public int Version { get; set; } = 1;

    public ScanOptions Scan { get; set; } = new();
    public ReadOptions Read { get; set; } = new();
    public AnalysisOptions Analysis { get; set; } = new();
    public LookupOptions Lookup { get; set; } = new();
    public MappingOptions Mapping { get; set; } = new();
    public WriteOptions Write { get; set; } = new();
    public SortOptions Sort { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public NativeToolsOptions NativeTools { get; set; } = new();
    public TaxonomyOptions Taxonomy { get; set; } = new();

    /// <summary>
    /// Runtime-only diagnostic messages collected by <c>TaggerOptionsLoader</c> — currently used
    /// for deprecation warnings (e.g. the legacy <c>read.existing_tags_policy</c> key, removed
    /// in favour of per-dimension <c>existing_confidence</c>). CLI handlers surface these to the
    /// user; the field is not serialized to/from YAML.
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public List<string> Deprecations { get; set; } = [];
}

/// <summary>
/// Points at the external taxonomy YAML file. Lives in its own pair of POCO + loaded snapshot
/// so the rule engine and rule loader can consult the loaded taxonomy without parsing it twice.
/// </summary>
public sealed class TaxonomyOptions
{
    /// <summary>
    /// Path to the taxonomy YAML. Empty disables taxonomy features entirely — the rule engine
    /// runs without genre normalisation and without enforcement.
    /// </summary>
    public string File { get; set; } = string.Empty;

    /// <summary>
    /// Loaded taxonomy (set by <c>TaggerOptionsLoader</c> after parsing <see cref="File"/>).
    /// <see cref="RayTagger.Core.Mapping.Taxonomy.Empty"/> when <see cref="File"/> is unset.
    /// </summary>
    public RayTagger.Core.Mapping.Taxonomy Loaded { get; set; }
        = RayTagger.Core.Mapping.Taxonomy.Empty;
}

/// <summary>
/// Where Tagger finds the manifest that lists download URLs/hashes for the native analysis tools
/// (Essentia, fpcalc) and where it caches the downloaded binaries. Letting the user override the
/// manifest path is intentional: power users can point at an internal mirror without forking Tagger.
/// </summary>
public sealed class NativeToolsOptions
{
    /// <summary>
    /// Path to the YAML manifest. Empty = look for <c>native-tools.yaml</c> next to <c>tagger.yaml</c>,
    /// then fall back to <c>native-tools.yaml</c> next to the running executable. If neither
    /// exists, the auto-bootstrap is disabled and the user has to put the binaries on PATH herself.
    /// </summary>
    public string ManifestFile { get; set; } = string.Empty;

    /// <summary>
    /// Whether to auto-download missing tools at scan startup. Disable to keep behaviour identical
    /// to pre-bootstrapper releases (PATH probe only, missing binary disables the dimension).
    /// </summary>
    public bool AutoBootstrap { get; set; } = true;
}

public sealed class ScanOptions
{
    public string Source { get; set; } = string.Empty;
    public bool Recursive { get; set; } = true;
    public List<string> Formats { get; set; } = ["mp3", "flac", "aiff"];
    public List<string> IncludeGlobs { get; set; } = [];
    public List<string> ExcludeGlobs { get; set; } = [];

    /// <summary>
    /// Number of pipeline workers to run in parallel. <c>0</c> (the default) is the
    /// "auto"-sentinel: <see cref="EffectiveParallelism"/> resolves it to
    /// <see cref="Environment.ProcessorCount"/> at runtime. <c>1</c> picks the simple sequential
    /// code path (deterministic ordering, useful for debugging). Any positive integer is honoured
    /// as-is.
    /// </summary>
    public int Parallelism { get; set; }

    /// <summary>Resolves <see cref="Parallelism"/> to a concrete worker count at runtime.</summary>
    public int EffectiveParallelism => Parallelism > 0 ? Parallelism : Environment.ProcessorCount;
}

/// <summary>
/// Placeholder for the <c>read:</c> block in tagger.yaml. Currently empty — the historical
/// <c>existing_tags_policy</c> knob has been replaced by per-dimension
/// <see cref="AnalyzerOptions.ExistingConfidence"/> and <see cref="LookupOptions.ExistingConfidence"/>.
/// Kept as a class (rather than removing the <c>read:</c> mapping entirely) so future
/// read-stage knobs have a place to land without another YAML schema break.
/// </summary>
public sealed class ReadOptions
{
}

public sealed class AnalysisOptions
{
    public BpmAnalyzerOptions Bpm { get; set; } = new() { Provider = "essentia", MinConfidence = 0.4 };
    public KeyAnalyzerOptions Key { get; set; } = new() { Provider = "essentia", MinConfidence = 0.55 };
    public EnergyAnalyzerOptions Energy { get; set; } = new() { Provider = "essentia", MinConfidence = 0.5 };
    public AnalyzerOptions Fingerprint { get; set; } = new() { Provider = "chromaprint", MinConfidence = 0.0 };
    public GenreClassifierOptions GenreClassifier { get; set; } = new();
}

/// <summary>
/// Opt-in audio-based genre classification. See <c>docs/PLAN_GENRE_CLASSIFICATION.md</c>.
/// Everything default-off — switching all flags off yields byte-identical pipeline behaviour
/// (the DI container plugs in a <c>NoopGenreClassifierRunner</c>).
/// </summary>
public sealed class GenreClassifierOptions
{
    public HeuristicClassifierOptions Heuristic { get; set; } = new();
    public TensorflowClassifierOptions Tensorflow { get; set; } = new();
}

/// <summary>
/// Tier-3 heuristic classifier — pure-rule scoring over DSP descriptors from the existing
/// Essentia run. Zero new dependencies. Scoring table in
/// <c>docs/PLAN_GENRE_CLASSIFICATION.md §3.5</c>.
/// </summary>
public sealed class HeuristicClassifierOptions
{
    /// <summary>
    /// On by default — adds House/Techno/Trance/Drum and Bass/Dubstep/Hip Hop/Ambient/Downtempo
    /// candidates to the resolver's input. Uses only DSP descriptors from the existing Essentia
    /// run (no extra subprocess), so the cost is negligible. Set to <c>false</c> to disable.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Below this score the classifier emits no candidate for that genre. [0,1].</summary>
    public double MinConfidence { get; set; } = 0.55;

    /// <summary>
    /// How to combine the 7 per-feature scores (BPM, KeyScale, Chords, Centroid, Dynamics,
    /// Danceability, BeatsLoudness) into a single composite confidence.
    /// </summary>
    public HeuristicScoringMode ScoringMode { get; set; } = HeuristicScoringMode.ArithmeticMean;
}

/// <summary>
/// Aggregation strategy for the heuristic classifier's per-feature scores. Implemented in
/// <c>HeuristicGenreClassifier.ScoreGenre</c> (RayTagger.Analysis).
/// </summary>
public enum HeuristicScoringMode
{
    /// <summary>
    /// Default. Composite = arithmetic mean of the present feature scores. Forgiving towards
    /// individual low-confidence features. Backward-compatible with the pre-Sprint-4 behaviour
    /// — the 1795-track backtest from 2026-05-23 measured F1 = 0.65 (Precision 0.48, Recall 0.997)
    /// at <c>min_confidence: 0.55</c> in this mode.
    /// </summary>
    ArithmeticMean,

    /// <summary>
    /// Composite = geometric mean of the present feature scores (with a 0.05 floor to avoid the
    /// product collapsing to zero from a single absent-but-not-null feature). The geometric mean
    /// is more sensitive to low scores than the arithmetic mean — a track with one strongly
    /// disagreeing feature drops below the threshold even when the other six all line up.
    /// Trades recall for precision; the 0.55 default min-confidence should be re-tuned downward
    /// (try 0.40) when switching to this mode.
    /// </summary>
    GeometricMean,
}

/// <summary>
/// Tier-1 TensorFlow classifiers. Each model is an independent switch with its own confidence
/// floor; the user installs <c>essentia-tensorflow</c> via <c>pip</c> and Tagger spawns the
/// Python bridge script. See <c>docs/PLAN_GENRE_CLASSIFICATION.md §4</c>.
/// </summary>
/// <remarks>
/// Per-model <see cref="TensorflowModelOptions.MinConfidence"/> is independently overridable
/// in <c>tagger.yaml</c>. The asymmetric defaults below (0.65 vs 0.50) reflect the role each
/// model plays — <see cref="GenreElectronic"/> overlaps with the Phase A heuristic so we set
/// a higher floor; <see cref="MtgJamendo"/> and <see cref="DiscogsEffnet"/> cover material the
/// heuristic deliberately skips (Rock/Pop/Soul/Jazz; fine-grained subgenres) and benefit from
/// a lower floor that captures genuine signal across larger label vocabularies.
/// </remarks>
public sealed class TensorflowClassifierOptions
{
    /// <summary>
    /// Default <c>min_confidence: 0.65</c> — higher than the other two models because
    /// <see cref="HeuristicClassifierOptions"/> already covers the same 5 classes
    /// (ambient/dnb/house/techno/trance). The raised floor turns this model into a
    /// "second opinion" that only fires when it disagrees with the heuristic from a
    /// position of genuine confidence.
    /// </summary>
    public TensorflowModelOptions GenreElectronic { get; set; } = new() { MinConfidence = 0.65 };

    /// <summary>
    /// Default <c>min_confidence: 0.50</c>, <c>aggregate_top_k: true</c>, <c>aggregate_min_total:
    /// 0.30</c> — the 87-class Jamendo tagger has unique coverage of Rock/Pop/R&amp;B/Soul/Jazz/
    /// Funk/Reggae/Classical that the heuristic doesn't try to handle. Aggregation is on by
    /// default because 87 classes produces the same diffuse-output pattern as discogs-effnet:
    /// a track's probability mass spreads across multiple sibling buckets of the same parent
    /// genre, and the raw top-1 is brittle. The aggregate threshold is set slightly higher than
    /// discogs-effnet (0.30 vs 0.25) because Jamendo's parent classes are coarser — a clear
    /// winner should clear a wider bar.
    /// </summary>
    public TensorflowModelOptions MtgJamendo { get; set; } = new()
    {
        AggregateTopK = true,
        AggregateMinTotal = 0.30,
    };

    /// <summary>
    /// Default <c>min_confidence: 0.50</c> and <c>aggregate_top_k: true</c> — the 400-class
    /// discogs-effnet model is the only source of subgenre detection in the pipeline AND its
    /// fine granularity benefits the most from parent-genre aggregation (see §4.0c). With
    /// aggregation on, raw subgenre detection is suppressed in favour of a robust parent-genre
    /// vote; set <c>aggregate_top_k: false</c> to recover the raw top-1 behaviour.
    /// </summary>
    public TensorflowModelOptions DiscogsEffnet { get; set; } = new() { AggregateTopK = true };

    /// <summary>Empty = look up <c>python3</c> on PATH.</summary>
    public string PythonExecutable { get; set; } = string.Empty;

    /// <summary>Empty = auto-discover next to the Tagger executable.</summary>
    public string ScriptPath { get; set; } = string.Empty;

    /// <summary>Empty = <c>&lt;local-app-data&gt;/RayTagger/models/</c>.</summary>
    public string ModelsDirectory { get; set; } = string.Empty;
}

/// <summary>
/// Per-TF-model knob. Each model has independent <see cref="Enabled"/> and
/// <see cref="MinConfidence"/> — both are overridable in <c>tagger.yaml</c>. The default
/// constructor sets the universal defaults; <see cref="TensorflowClassifierOptions"/> overrides
/// these in per-model initialisers when the role demands a different baseline.
/// </summary>
/// <remarks>
/// The aggregation knobs (<see cref="AggregateTopK"/> and friends) implement the parent-genre
/// summation described in <c>docs/PLAN_GENRE_CLASSIFICATION.md §4.0c</c>. Default-off here so a
/// freshly-constructed instance behaves like before B6.5; <see cref="TensorflowClassifierOptions"/>
/// flips it on only for <c>DiscogsEffnet</c> where the 400-class output benefits most.
/// </remarks>
public sealed class TensorflowModelOptions
{
    public bool Enabled { get; set; }
    public double MinConfidence { get; set; } = 0.5;

    /// <summary>
    /// When true, the classifier sums the top-K predictions' probabilities by their resolved
    /// taxonomy parent genre and emits aggregated candidate(s) in addition to the raw top-K.
    /// Captures the "model distributes confidence across many subgenres of the same parent"
    /// case (see §4.0c). Default off; only the 400-class discogs-effnet enables this by default.
    /// </summary>
    public bool AggregateTopK { get; set; }

    /// <summary>
    /// Per-candidate probability floor for inclusion in the aggregation sum. Long-tail
    /// predictions (e.g. p &lt; 0.02 in a top-10 output) contribute almost no information but
    /// can dilute the parent-genre signal in aggregate. [0, 1].
    /// </summary>
    public double AggregatePerCandidateFloor { get; set; } = 0.02;

    /// <summary>
    /// Minimum aggregated sum for a parent genre to count as a "clear winner" and be emitted
    /// as a standalone aggregated candidate. When NO parent genre clears this threshold the
    /// model's output is considered diffuse — see <see cref="AggregateFallbackOnDiffuse"/> for
    /// what happens next. [0, 1].
    /// </summary>
    public double AggregateMinTotal { get; set; } = 0.25;

    /// <summary>
    /// When true and the aggregation produced no parent above <see cref="AggregateMinTotal"/>,
    /// the parent with the highest sum is still emitted — tagged with the <c>:aggregated-fallback</c>
    /// source suffix so the trace makes the uncertainty visible. When false, diffuse output
    /// produces no aggregated candidate at all (raw top-K still emit). Default true: a
    /// reasonable best-guess beats no answer for DJ-library tagging.
    /// </summary>
    public bool AggregateFallbackOnDiffuse { get; set; } = true;
}

public class AnalyzerOptions
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public double MinConfidence { get; set; }

    /// <summary>
    /// How much to trust an existing tag for this dimension, on the same [0,1] scale as the
    /// analyzer's <c>MinConfidence</c>. Higher = harder to displace.
    /// <list type="bullet">
    ///   <item><c>1.0</c> (default) — existing tags always win unless the policy is
    ///   <c>always_overwrite</c>; reproduces classic <c>skip_if_present</c> behaviour.</item>
    ///   <item><c>0.0</c> — analyzer/lookup wins whenever its value is usable
    ///   (i.e. confidence ≥ <see cref="MinConfidence"/>); per-dimension always-overwrite.</item>
    ///   <item>Any value in-between — the analyzer's confidence has to clear this floor to
    ///   displace the existing tag. Useful when the existing tags come from a trusted source
    ///   (e.g. Mixed-In-Key) and only a high-confidence analyzer hit should override.</item>
    /// </list>
    /// The <c>tagger scan --force-overwrite</c> CLI flag zeroes this floor for a single run
    /// across all dimensions; <c>tagger validate</c> does the same internally so the backtest
    /// doesn't measure existing tags passing through.
    /// </summary>
    public double ExistingConfidence { get; set; } = 1.0;
}

/// <summary>
/// Which notation Tagger uses when displaying keys to the user (CLI output, logs). The frame
/// writes themselves always use the canonical notation per format: <c>TKEY</c>/<c>INITIALKEY</c>
/// = standard, <c>TXXX:CAMELOTKEY</c>/<c>CAMELOTKEY</c> = Camelot. See ARCHITECTURE §6.1.
/// </summary>
public enum KeyDisplayNotation
{
    Camelot,
    Standard,
}

public sealed class KeyAnalyzerOptions : AnalyzerOptions
{
    /// <summary>CLI/log display only — TKEY and TXXX:CAMELOTKEY are always written in their canonical notation.</summary>
    public KeyDisplayNotation DisplayNotation { get; set; } = KeyDisplayNotation.Camelot;

    /// <summary>
    /// Which Essentia profile (or combination) to consume when reading the key. Essentia's music
    /// extractor always emits all three profiles in parallel; this knob picks which one becomes
    /// the resolved key. EDMA is the historical default — DJ-tuned and strongest on EDM/Beatport
    /// material. <c>EnsembleVoting</c> compares all three and picks the majority winner, which
    /// the 1795-track backtest from 2026-05-23 shows is the right choice for libraries with
    /// non-EDM material (Hip-Hop, Trip-Hop, Reggae) where EDMA underperforms.
    /// </summary>
    public KeyProfileSelection Profile { get; set; } = KeyProfileSelection.Edma;

    /// <summary>
    /// Strength floor under which the <see cref="KeyProfileSelection.EdmaWithFallback"/> mode
    /// abandons EDMA and switches to the higher-strength among Temperley / Krumhansl. The 1795-
    /// track backtest shows EDMA's strength sits in [0.5, 0.9] when it's right; anything below
    /// 0.5 correlates with material it wasn't trained for.
    /// </summary>
    public double EdmaWithFallbackThreshold { get; set; } = 0.5;
}

/// <summary>
/// How the EssentiaKeyAnalyzer chooses between Essentia's three key profiles.
/// </summary>
public enum KeyProfileSelection
{
    /// <summary>EDMA only (default, backward-compatible). DJ-tuned profile.</summary>
    Edma,

    /// <summary>Temperley only. Better than EDMA on rock / pop / jazz material.</summary>
    Temperley,

    /// <summary>Krumhansl only. Classical-trained tonal-hierarchy model.</summary>
    Krumhansl,

    /// <summary>
    /// Run all three profiles; pick the key that two or more profiles agree on. When no two
    /// profiles agree, fall back to the highest-strength single profile. Captures the case
    /// where one profile (typically EDMA on non-EDM) is wrong — the other two outvoting it
    /// rescue the result. Confidence is the average of the strengths from the agreeing
    /// profiles (or the single fallback's strength when there's no agreement).
    /// </summary>
    EnsembleVoting,

    /// <summary>
    /// Use EDMA unless its strength is below the configured threshold
    /// (<c>KeyAnalyzerOptions.EdmaWithFallbackThreshold</c>), in which case fall back to
    /// whichever of Temperley / Krumhansl reports the highest strength. Cheaper than full
    /// ensemble voting and preserves EDMA's edge on EDM tracks.
    /// </summary>
    EdmaWithFallback,
}

/// <summary>
/// BPM analyzer settings — extends the base <see cref="AnalyzerOptions"/> with a snap-to-grid
/// post-processing step that rounds noise-floor decimal output (e.g. Essentia's 122.07) to the
/// nearest multiple of <see cref="SnapStep"/> when within the configured tolerance.
/// </summary>
public sealed class BpmAnalyzerOptions : AnalyzerOptions
{
    /// <summary>
    /// Maximum allowed percentage drift from the nearest <see cref="SnapStep"/> grid point for
    /// the snap to fire. <c>0.12</c> catches Essentia's typical noise-floor drift (e.g. 122.07 →
    /// 122 at 0.057% drift) but leaves genuinely fractional results alone. Set <c>0</c> to disable.
    /// </summary>
    public double SnapTolerancePercent { get; set; } = 0.12;

    /// <summary>
    /// Grid granularity in BPM. <c>1.0</c> snaps to whole integers; <c>0.5</c> (default) also
    /// catches half-integer values — e.g. 173.48 → 173.5 (drift 0.012%) where step=1 would have
    /// left it alone (drift to 173 = 0.28%, above any reasonable tolerance). Must be &gt; 0.
    /// </summary>
    public double SnapStep { get; set; } = 0.5;

    /// <summary>
    /// Per-genre snap-step overrides. Looked up after the genre is resolved (same normalisation
    /// as <see cref="TempoRangesByGenre"/>). When the resolved genre has an entry here, the
    /// analyzer snaps to multiples of that step instead of the global <see cref="SnapStep"/>.
    /// Useful when one genre's tempos are inherently coarser than the rest of the library —
    /// e.g. Pop/Rock at integer BPMs (1.0) while House/Techno stay on the half-grid (0.5) and
    /// DnB needs finer resolution (0.25) because 174.5 vs 174 matters for harmonic mixing.
    /// </summary>
    public Dictionary<string, double> SnapStepByGenre { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-genre BPM ranges. The key is a canonical genre name (matched against the genre
    /// resolved from <see cref="RayTagger.Core.Mapping.Taxonomy.NormaliseByAlias"/> — e.g.
    /// <c>"Tech House"</c> normalises to <c>"House"</c> and looks up the <c>"House"</c> entry).
    /// Lookup is case-insensitive. When the resolved genre has an entry here, Essentia is
    /// invoked with a per-run profile pinning <c>minTempo</c>/<c>maxTempo</c> to that range —
    /// which eliminates half/double-time errors for genre-typed material. When no entry
    /// matches, <see cref="TempoRangeFallback"/> is used; when that's also <c>null</c>,
    /// Essentia falls back to its own default range (40–208).
    /// </summary>
    public Dictionary<string, BpmTempoRange> TempoRangesByGenre { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fallback range used when no entry in <see cref="TempoRangesByGenre"/> matches the track's
    /// genre. <c>null</c> = no range, Essentia uses its own default. Set this only if your
    /// library has a known overall tempo envelope that holds even for un-genre-tagged tracks.
    /// </summary>
    public BpmTempoRange? TempoRangeFallback { get; set; }
}

public sealed class EnergyAnalyzerOptions : AnalyzerOptions
{
    public string Scale { get; set; } = "1-10";

    /// <summary>
    /// Path to a frozen calibration profile file. Default: <c>./energy-calibration.yaml</c>
    /// (relative to tagger.yaml). Set to empty string to disable calibration and always use the
    /// built-in defaults. The file is written by <c>tagger calibrate-energy</c> or the UI
    /// "Calibrate Energy" button — never hand-maintained.
    /// </summary>
    public string CalibrationFile { get; set; } = "./energy-calibration.yaml";

    /// <summary>
    /// Loaded calibration profile (set by <c>TaggerOptionsLoader</c> after parsing
    /// <see cref="CalibrationFile"/>). <c>null</c> when the file is missing or empty —
    /// the analyzer falls back to the built-in defaults in that case.
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public EnergyCalibrationProfile? LoadedCalibration { get; set; }
}

/// <summary>
/// Frozen per-library calibration profile for <see cref="EnergyAnalyzerOptions"/>. Each
/// <see cref="FeatureAnchor"/> defines the 10th/90th-percentile range of one of the five
/// composite features observed across the source folder. Values outside the anchors clamp;
/// values within map linearly to [0,1] for the composite.
/// </summary>
/// <remarks>
/// "Frozen" means: once written, the profile persists in <c>tagger.yaml</c> verbatim until the
/// user explicitly re-calibrates. Re-tagging already-written <c>TXXX:ENERGYLEVEL</c> frames to
/// match a new profile is the user's responsibility — Tagger never silently rewrites tags.
/// </remarks>
public sealed class EnergyCalibrationProfile
{
    public FeatureAnchor SpectralFlux { get; set; } = new();
    public FeatureAnchor BeatsLoudness { get; set; } = new();
    public FeatureAnchor OnsetRate { get; set; } = new();
    public FeatureAnchor Danceability { get; set; } = new();
    public FeatureAnchor AverageLoudness { get; set; } = new();

    /// <summary>Number of tracks that contributed to the profile (post-failure filter).</summary>
    public int SampleCount { get; set; }

    /// <summary>UTC timestamp when the profile was generated. ISO 8601 in YAML.</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>Absolute path of the source folder used to generate the profile (diagnostic only).</summary>
    public string SourcePath { get; set; } = string.Empty;
}

/// <summary>
/// Linear-normalisation anchors for one feature. Values &lt;= <see cref="Floor"/> clamp to 0,
/// values &gt;= <see cref="Ceiling"/> clamp to 1, anything in between scales linearly.
/// </summary>
public sealed class FeatureAnchor
{
    public double Floor { get; set; }
    public double Ceiling { get; set; }
}

public sealed class LookupOptions
{
    public bool Enabled { get; set; } = true;
    public bool OnlineRequired { get; set; }
    public List<string> Providers { get; set; } = ["acoustid", "musicbrainz", "discogs", "lastfm"];
    public CacheOptions Cache { get; set; } = new();
    public ApiKeysOptions ApiKeys { get; set; } = new();
    public RateLimitsOptions RateLimits { get; set; } = new();

    /// <summary>
    /// Optional contact (URL or email) embedded in the outbound <c>User-Agent</c> header so the
    /// MusicBrainz operations team can reach this deployment's maintainers per
    /// <see href="https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting"/>. Empty or
    /// whitespace = use the project's default (the public RayTagger repository URL).
    /// Override this when running a fork or an internal deployment where the upstream repo is
    /// not the right point of contact.
    /// </summary>
    public string UserAgentContact { get; set; } = string.Empty;

    /// <summary>
    /// When <c>true</c> (default), the new taxonomy-aware genre resolver is used: API candidates
    /// are matched whole-word against <c>taxonomy.genres</c> / <c>taxonomy.subgenres</c>, longest
    /// match wins, and the canonical YAML casing is written. When <c>false</c>, the legacy
    /// "top-1 candidate stur übernehmen" path is used (kept for users who curate their tags by
    /// other means and don't want taxonomy filtering).
    /// </summary>
    public bool TaxonomyResolution { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, the lookup-merge applies a Noisy-OR boost to candidate genres that
    /// multiple providers independently returned: the merged confidence is <c>1 − Π(1 − cᵢ)</c>
    /// over every contributing candidate. Captures the statistical insight that two independent
    /// signals at confidence 0.5 are more reliable than a single signal at 0.8 — the boost only
    /// fires when at least two distinct provider names contribute, so a single provider returning
    /// the same value twice (rare, but happens with MusicBrainz tag aggregation) doesn't trigger
    /// it. Default <c>false</c>: keeps the historical "highest-confidence per value wins"
    /// behaviour. Worth enabling once the per-provider win-rates in <c>tagger validate</c>'s
    /// Resolver-Trace section show overlapping but disagreeing providers.
    /// </summary>
    public bool ConsensusBoost { get; set; }

    /// <summary>
    /// Existing-tag confidence floor for genre/subgenre in the LEGACY merge path (only fires when
    /// <see cref="TaxonomyResolution"/> is <c>false</c> or no taxonomy is loaded). Same semantics
    /// as the per-analyzer <see cref="AnalyzerOptions.ExistingConfidence"/>:
    /// <list type="bullet">
    ///   <item><c>1.0</c> (default) — existing genre always wins (classic skip-if-present).</item>
    ///   <item><c>0.0</c> — any non-empty lookup candidate overrides existing.</item>
    ///   <item>any value in-between — lookup candidate's confidence must clear this floor to win.</item>
    /// </list>
    /// Has no effect when the taxonomy resolver is active — that path uses taxonomy-membership
    /// protection instead, which is binary by design (existing-in-taxonomy is preserved).
    /// </summary>
    public double ExistingConfidence { get; set; } = 1.0;
}

/// <summary>
/// Per-provider minimum interval (milliseconds) between consecutive outbound requests. The
/// values are enforced client-side by each provider's <c>RateLimiter</c>; setting them above the
/// API's documented limit is the right move when sharing an API key across multiple machines
/// or when an upstream provider has tightened its policy. Setting them below risks 429/503
/// responses and — for MusicBrainz — outright IP blocks.
/// </summary>
/// <remarks>
/// Defaults match the published policies as of late 2025:
/// <list type="bullet">
///   <item>AcoustID: 3 req/s per key (350 ms keeps us safely under).</item>
///   <item>MusicBrainz: 1 req/s per IP+UA (1100 ms with a small safety margin).</item>
///   <item>Discogs: 60 req/min authenticated (1100 ms ≈ 54 req/min).</item>
///   <item>Last.fm: no published limit; 200 ms (5 req/s) is conservative.</item>
/// </list>
/// </remarks>
public sealed class RateLimitsOptions
{
    /// <summary>Default for <see cref="AcoustidMs"/>. Public so providers and tests can reference one source of truth.</summary>
    public const int DefaultAcoustidMs = 350;
    /// <summary>Default for <see cref="MusicbrainzMs"/>.</summary>
    public const int DefaultMusicbrainzMs = 1100;
    /// <summary>Default for <see cref="DiscogsMs"/>.</summary>
    public const int DefaultDiscogsMs = 1100;
    /// <summary>Default for <see cref="LastfmMs"/>.</summary>
    public const int DefaultLastfmMs = 200;

    public int AcoustidMs { get; set; } = DefaultAcoustidMs;
    public int MusicbrainzMs { get; set; } = DefaultMusicbrainzMs;
    public int DiscogsMs { get; set; } = DefaultDiscogsMs;
    public int LastfmMs { get; set; } = DefaultLastfmMs;
}

public sealed class CacheOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Empty = OS-appropriate default via <see cref="IO.IUserDataDirectoryProvider"/>.</summary>
    public string Directory { get; set; } = string.Empty;

    public int TtlDays { get; set; } = 30;
}

public sealed class ApiKeysOptions
{
    public string Acoustid { get; set; } = string.Empty;
    public string Discogs { get; set; } = string.Empty;
    public string Lastfm { get; set; } = string.Empty;
}

public sealed class MappingOptions
{
    public string RulesFile { get; set; } = "./mappings.yaml";

    /// <summary>
    /// Tier values used by <see cref="Mapping.TaxonomyGenreResolver"/> to order candidates before
    /// the first-match-wins walk. Higher tier = considered first. Defaults reflect "providers win
    /// over classifiers; TF-aggregated wins over heuristic". See
    /// <c>docs/PLAN_GENRE_CLASSIFICATION.md §4.0d</c>.
    /// </summary>
    public SourcePriorityOptions SourcePriority { get; set; } = new();
}

/// <summary>
/// Per-source-tier priority for the genre resolver's candidate ordering (B6.6). All values are
/// independently overridable in <c>tagger.yaml</c>. Examples for typical use:
/// <list type="bullet">
///   <item>Heuristic-first library (TF noisy on your material): set <see cref="ClassifierHeuristic"/>
///         &gt; <see cref="ClassifierAggregated"/>.</item>
///   <item>De-prioritise a single provider (e.g. Discogs returns weird Style tags on your library):
///         put a name-keyed override into <see cref="Providers"/> — e.g. <c>discogs: 60</c> drops it
///         below the classifier-aggregated tier.</item>
/// </list>
/// </summary>
public sealed class SourcePriorityOptions
{
    /// <summary>
    /// Default tier for online provider hits when <see cref="Providers"/> has no entry for the
    /// provider's name. Default: 100.
    /// </summary>
    public int Provider { get; set; } = 100;

    /// <summary>
    /// Per-provider tier overrides keyed by provider name (case-insensitive). When the resolver
    /// inspects a candidate whose <c>Source</c> equals one of these keys, the matching value
    /// wins over <see cref="Provider"/>. Use this to encode "MusicBrainz is more trustworthy than
    /// Last.fm on my library" or to demote a noisy source below the classifier tiers without
    /// disabling it entirely.
    /// </summary>
    /// <example>
    /// <code>
    /// providers:
    ///   musicbrainz: 110
    ///   discogs:     105
    ///   lastfm:       40   # below ClassifierHeuristic — only used as last-resort signal
    /// </code>
    /// </example>
    public Dictionary<string, int> Providers { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tier for TF classifier candidates with <c>:aggregated</c> suffix (B6.5 clear winner). Default: 80.</summary>
    public int ClassifierAggregated { get; set; } = 80;

    /// <summary>Tier for TF classifier candidates with <c>:aggregated-fallback</c> suffix (diffuse output). Default: 70.</summary>
    public int ClassifierAggregatedFallback { get; set; } = 70;

    /// <summary>Tier for TF classifier raw top-K (single-class softmax). Default: 60.</summary>
    public int ClassifierTfRaw { get; set; } = 60;

    /// <summary>Tier for future classifier sources between heuristic and TF-raw. Default: 55.</summary>
    public int ClassifierOther { get; set; } = 55;

    /// <summary>Tier for the rule-based heuristic classifier. Default: 50.</summary>
    public int ClassifierHeuristic { get; set; } = 50;

    /// <summary>
    /// Fresh-instance accessor returning the hardcoded defaults. Implemented as a property-getter
    /// (not a cached field) so accidental mutation by a caller can't poison the shared baseline —
    /// every read yields its own instance.
    /// </summary>
    public static SourcePriorityOptions Defaults => new();
}

public sealed class WriteOptions
{
    public bool DryRun { get; set; }
    public bool Backup { get; set; } = true;

    /// <summary>
    /// Raw YAML-side override of per-format frame names for each logical field. Parsed into a
    /// typed <see cref="TagFieldMap"/> at load time via <see cref="TagFieldMapBuilder.Build"/>;
    /// validation errors surface alongside other configuration errors. See
    /// <c>samples/tagger.example.yaml</c> for the token syntax.
    /// </summary>
    public Dictionary<string, List<string>> TagFields { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Parsed view of <see cref="TagFields"/>. Set by the loader; <see cref="TagFieldMap.Default"/>
    /// when the user didn't override anything.
    /// </summary>
    public TagFieldMap TagFieldMap { get; set; } = TagFieldMap.Default;
}

public enum SortConflictPolicy
{
    Rename,
    Skip,
    Overwrite,
}

public sealed class SortOptions
{
    public bool Enabled { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string Pattern { get; set; } = "{genre}/{subgenre?}/{artist} - {title}.{ext}";
    public SortConflictPolicy Conflict { get; set; } = SortConflictPolicy.Rename;
    public bool PreserveSource { get; set; }
}

public sealed class LoggingOptions
{
    public string Level { get; set; } = "information";
    public bool Console { get; set; } = true;
    public LoggingFileOptions File { get; set; } = new();
    public LoggingSqliteOptions Sqlite { get; set; } = new();
}

public sealed class LoggingFileOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "./logs";
}

public sealed class LoggingSqliteOptions
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "./logs/tagger.db";
}
