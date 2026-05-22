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

public enum ExistingTagsPolicy
{
    /// <summary>
    /// Default. Per field: if an existing value is set, Analysis/Lookup do NOT overwrite it.
    /// Rules-sourced values always write regardless of policy (the user's explicit declarative
    /// intent should never be silently dropped). See <see cref="FillOnlyEmpty"/> for the alias
    /// and ARCHITECTURE.md §6.2 for the full matrix.
    /// </summary>
    SkipIfPresent,

    /// <summary>
    /// Alias of <see cref="SkipIfPresent"/>. Kept as a separate enum value because the example
    /// YAML and earlier docs reference both names; merger behaviour is identical. Pick whichever
    /// reads better in the user's <c>tagger.yaml</c>.
    /// </summary>
    FillOnlyEmpty,

    /// <summary>Overwrite anything, including non-empty existing values.</summary>
    AlwaysOverwrite,
}

public sealed class ReadOptions
{
    public ExistingTagsPolicy ExistingTagsPolicy { get; set; } = ExistingTagsPolicy.SkipIfPresent;
}

public sealed class AnalysisOptions
{
    public BpmAnalyzerOptions Bpm { get; set; } = new() { Provider = "essentia", MinConfidence = 0.4 };
    public KeyAnalyzerOptions Key { get; set; } = new() { Provider = "essentia", MinConfidence = 0.55 };
    public EnergyAnalyzerOptions Energy { get; set; } = new() { Provider = "essentia", MinConfidence = 0.5 };
    public AnalyzerOptions Fingerprint { get; set; } = new() { Provider = "chromaprint", MinConfidence = 0.0 };
}

public class AnalyzerOptions
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public double MinConfidence { get; set; }
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
