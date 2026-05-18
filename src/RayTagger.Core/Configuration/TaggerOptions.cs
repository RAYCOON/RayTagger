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
    public int Parallelism { get; set; } = Environment.ProcessorCount;
}

public enum ExistingTagsPolicy
{
    /// <summary>
    /// Default. Analysis/Lookup values are preserved when the existing tag is non-empty AND the
    /// analysis value would replace it; Rules-sourced values always write. See ARCHITECTURE §6.2.
    /// </summary>
    SkipIfPresent,

    /// <summary>
    /// Stricter than <see cref="SkipIfPresent"/>: Analysis and Lookup values are written ONLY when
    /// the existing tag is empty. Rules-sourced values still always write.
    /// </summary>
    FillOnlyEmpty,

    /// <summary>Overwrite anything that isn't empty.</summary>
    AlwaysOverwrite,
}

public sealed class ReadOptions
{
    public ExistingTagsPolicy ExistingTagsPolicy { get; set; } = ExistingTagsPolicy.SkipIfPresent;
}

public sealed class AnalysisOptions
{
    public AnalyzerOptions Bpm { get; set; } = new() { Provider = "essentia", MinConfidence = 0.4 };
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

public sealed class EnergyAnalyzerOptions : AnalyzerOptions
{
    public string Scale { get; set; } = "1-10";
}

public sealed class LookupOptions
{
    public bool Enabled { get; set; } = true;
    public bool OnlineRequired { get; set; }
    public List<string> Providers { get; set; } = ["acoustid", "musicbrainz", "discogs", "lastfm"];
    public CacheOptions Cache { get; set; } = new();
    public ApiKeysOptions ApiKeys { get; set; } = new();
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
    public Dictionary<string, List<string>> TagFields { get; set; } = new(StringComparer.Ordinal);
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
