# Architecture

This document is the design reference for RayTagger. It captures the pipeline, the abstractions that fall out of it, the mapping DSL, the per-format tag-frame matrix, and the trade-offs behind each choice. Update it whenever the design changes — `CLAUDE.md` links here.

---

## 1. Pipeline

A scan run is a directed pipeline. Each stage is a small unit with a single responsibility and a clear input/output contract. The orchestrator (`ITagPipeline` in `RayTagger.Core`) wires them.

```
   ┌──────────────────┐
   │ 1. Discover      │  Enumerate audio files under scan roots, filter by
   │                  │  extension and glob, emit TrackFile.
   └────────┬─────────┘
            ▼
   ┌──────────────────┐
   │ 2. Read tags     │  TagLib# reads existing tags into TrackTags. Used by
   │                  │  the "skip if present" policy and by online lookups.
   └────────┬─────────┘
            ▼
   ┌──────────────────┐
   │ 3. Analyze       │  Resolve per-track hints (e.g. genre-based BPM tempo
   │                  │  range via ITempoRangeResolver), then run enabled
   │                  │  analyzers (BPM/Key/Energy/Fingerprint) in parallel.
   │                  │  Essentia-backed analyzers share one process fork
   │                  │  per (file, range) — see §3.
   └────────┬─────────┘
            ▼
   ┌──────────────────┐
   │ 4. Lookup        │  Optional. Use fingerprint + existing tags to query
   │                  │  AcoustID → MusicBrainz / Discogs / Last.fm. Merge
   │                  │  candidates into a confidence-ranked list.
   └────────┬─────────┘
            ▼
   ┌──────────────────┐
   │ 5. Map           │  Apply mapping rules in order. Resolve final
   │                  │  Genre, SubGenre and any rule-set extra tags.
   └────────┬─────────┘
            ▼
   ┌──────────────────┐
   │ 6. Write tags    │  Resolve logical fields → format-specific frames,
   │                  │  back up file, write via TagLib#.
   └────────┬─────────┘
            ▼
   ┌──────────────────┐
   │ 7. Sort (opt.)   │  Move file to destination per template; handle
   │                  │  collisions per config (rename / skip / overwrite).
   └──────────────────┘
```

**Parallelism:** when `scan.parallelism > 1`, files flow through `System.Threading.Channels` with a producer task feeding a bounded work-channel (`4 × parallelism` slots) and N worker tasks pulling from it. Each worker runs the entire per-file pipeline end-to-end so Essentia subprocess + HTTP-lookup latency overlaps across tracks. Outcomes are yielded in **completion order**, not discovery order — the throughput trade-off. `parallelism = 1` keeps the simple sequential code path for deterministic ordering and debugging.

**Per-file failure isolation:** an exception in any stage marks that file `Failed` with a structured error and continues with the next file. Workers swallow per-file failures (the same `ShouldIsolate` allow-list as the sequential path) but fatal exceptions (cancellation, OOM, stack overflow) propagate. A summary is printed at the end and persisted to a run log.

**Dry-run:** stages 6 and 7 short-circuit to a "would write … / would move …" emitter. Backups are not created.

---

## 2. Domain model (`RayTagger.Core/Models`)

Records, immutable after construction. Names are tentative; finalize when implementation starts.

```csharp
public sealed record TrackFile(string Path, AudioFormat Format, long SizeBytes, DateTime LastModifiedUtc);

public enum AudioFormat { Mp3, Flac, Aiff }

public sealed record TrackTags(
    string? Title, string? Artist, string? AlbumArtist, string? Album,
    int? Year, string? Genre, string? SubGenre,
    double? Bpm, MusicalKey? Key, int? Energy,
    IReadOnlyDictionary<string, string> Custom);

public sealed record MusicalKey(string Standard, string Camelot);  // e.g. ("Am", "8A")

// Every analyzer emits a confidence — write policy can refuse to overwrite an
// existing tag when the new analysis is uncertain.
public sealed record BpmResult(double? Bpm, double Confidence);
public sealed record KeyResult(MusicalKey? Key, double Confidence);
public sealed record EnergyResult(int? Energy, double Confidence);
public sealed record FingerprintResult(string? Chromaprint, double Confidence);

public sealed record AnalysisResult(
    BpmResult Bpm, KeyResult Key, EnergyResult Energy, FingerprintResult Fingerprint,
    Guid? AcoustIdMbid);

public sealed record GenreCandidate(string Value, double Confidence, string Source);

public sealed record LookupResult(
    IReadOnlyList<GenreCandidate> GenreCandidates,
    IReadOnlyList<GenreCandidate> SubGenreCandidates,
    Guid? MbReleaseId, Guid? MbRecordingId);

// Per-field provenance — the write policy decides what to persist based on
// where each value came from. `Existing` is the tag already on disk;
// `Rules` is a value explicitly set by a mapping rule and overrides
// `skip_if_present` (otherwise rules could never re-tag existing files).
public enum TagFieldSource { Existing, Analysis, Lookup, Rules }

public sealed record ResolvedField<T>(T? Value, TagFieldSource Source, double Confidence);

public sealed record ResolvedTrackTags(
    ResolvedField<string>      Genre,
    ResolvedField<string>      SubGenre,
    ResolvedField<double>      Bpm,
    ResolvedField<MusicalKey>  Key,
    ResolvedField<int>         Energy,
    IReadOnlyDictionary<string, ResolvedField<string>> Custom);

public sealed record PipelineOutcome(
    TrackFile File, ResolvedTrackTags Resolved, IReadOnlyList<MappingRuleHit> AppliedRules,
    string? DestinationPath, PipelineStatus Status, IReadOnlyList<StageError> Errors);
```

**Why `LastModifiedUtc` instead of a content hash:** the hash would force a full file read for every track at discovery time — prohibitive for libraries with tens of thousands of files. `path + size + mtime` is sufficient to detect "file changed since last scan" for cache invalidation, and the Chromaprint fingerprint (computed only when analysis is enabled and cached) is the semantically meaningful identity. Add a hash later only if a concrete need (dedup, drift detection) appears.

---

## 3. Audio analysis (`RayTagger.Analysis`)

One interface per dimension. User picks one implementation per dimension in `tagger.yaml`.

```csharp
public interface IBpmAnalyzer        { Task<BpmResult>          AnalyzeAsync(TrackFile f, CancellationToken ct); }
public interface IKeyAnalyzer        { Task<KeyResult>          AnalyzeAsync(TrackFile f, CancellationToken ct); }
public interface IEnergyAnalyzer     { Task<EnergyResult>       AnalyzeAsync(TrackFile f, CancellationToken ct); }
public interface IFingerprintAnalyzer{ Task<FingerprintResult>  AnalyzeAsync(TrackFile f, CancellationToken ct); }
```

| Dimension     | Implementation                                                | Source field in Essentia JSON                                   |
|---------------|---------------------------------------------------------------|----------------------------------------------------------------|
| BPM           | `EssentiaBpmAnalyzer`                                         | `rhythm.bpm` + `rhythm.bpm_histogram_first_peak_weight` (conf) |
| Key           | `EssentiaKeyAnalyzer` (EDMA profile)                          | `tonal.key_edma.{key,scale,strength}`                          |
| Energy        | `EssentiaEnergyAnalyzer` (5-feature composite, 1–10)          | `lowlevel.{spectral_flux,average_loudness}` + `rhythm.{beats_loudness,onset_rate,danceability}` |
| Fingerprint   | `ChromaprintFingerprintAnalyzer` (shells `fpcalc`)            | n/a — separate binary; AcoustID requires Chromaprint           |

**Single-shot Essentia.** BPM, key and energy share one `essentia_streaming_extractor_music` run per track via `EssentiaAnalysisService`, which caches the parsed JSON keyed by `(path, last-write-time)`. Essentia is always invoked with its built-in default tempo range (40-208) — the per-genre tempo hint is applied AFTER detection, not as a CLI argument. That keeps the cache key simple and removes one filesystem indirection (no transient profile YAML written per range).

**Per-genre BPM tempo fold.** After Essentia returns, `EssentiaBpmAnalyzer` consults `ITempoRangeResolver` to look up the genre-resolved range:
1. read the track's existing `TCON`/`GENRE` tag,
2. normalise via `taxonomy.yaml` (e.g. `"Tech House"` → `"House"`),
3. look the normalised genre up in `analysis.bpm.tempo_ranges_by_genre` (case-insensitive),
4. fall back to `analysis.bpm.tempo_range_fallback` if no match — when that's null too, no fold is attempted and the raw value passes through for the pipeline-level snap.

Fold algorithm per track:
- `raw ∈ [min, max]` → `snap(raw)`; `WasSnapped` reflects whether the snap actually changed the value.
- `raw < min` → fold via `raw × 2`, then snap; if the result is in range, accept it.
- `raw > max` → fold via `raw / 2`, then snap; if the result is in range, accept it.
- Folded-and-snapped value still out of range → return `snap(raw)` and set `IsForcedFallback`. The UI renders the BPM cell **dark blue** so the user sees the configured range and the detected tempo couldn't be reconciled — the value is the best raw signal, and the user should investigate whether the genre tag is wrong.

DJ-convention edge cases resolve automatically:
- 86 BPM DnB intro, genre = `Drum and Bass` `[130, 200]` → fold `×2` → 172 (DJ full-time convention).
- 154 BPM DubStep, genre = `DubStep` `[50, 100]` → fold `÷2` → 77 (DJ half-time convention).

This replaces the previous per-genre Essentia profile YAML mechanism and the `post: double` / `post: halve_above_100` YAML hints — those vocabulary items have been removed from `tagger.yaml`. The fold is symmetric and self-correcting; the dark-blue marker surfaces unresolvable disagreement instead of silently forcing a wrong octave.

**Why Essentia over aubio + keyfinder-cli.** Essentia's tempo tracker exposes a real confidence (`bpm_histogram_first_peak_weight`) and its histogram surfaces tempo ambiguity (half/double-time) where aubio silently picked one octave. The EDMA key profile is trained on the Beatport corpus and consistently outperforms libKeyFinder's Shaath profile on DJ-oriented material. Combined, one tool replaces three and produces strictly more information per run.

**CLI wrapping pattern:** each adapter uses `System.Diagnostics.Process` with a configurable timeout, reads stdout, and parses with a small regex/JSON parser. Errors raise `AnalysisException` with stderr captured. The wrapper class lives in `RayTagger.Analysis.Internal.NativeProcessRunner` — shared by both Essentia and Chromaprint.

**Process safety:** arguments are passed via `ProcessStartInfo.ArgumentList` (not the joined `Arguments` string) so file paths with spaces, quotes, or shell metacharacters can't escape into the command line.

**PATH probe:** at host startup, `IAnalysisToolProbe` checks for each binary referenced in config and logs a structured report. Missing tools disable their analyzer; the pipeline runs with reduced output rather than crashing.

**Confidence-aware writing:** each `*Result` carries a `Confidence` in `[0,1]`. The write stage consults a per-dimension `min_confidence` threshold from config — if the analyzed value is below the threshold, the existing tag (if any) is preserved. This avoids overwriting hand-curated metadata with noisy Essentia output on intros/breakdowns or low-confidence key estimates.

---

## 4. Online lookup (`RayTagger.Lookup`)

```csharp
public interface IMetadataProvider
{
    string Name { get; }
    Task<MetadataResult?> LookupAsync(LookupQuery query, CancellationToken ct);
}
```

| Provider       | Requires             | Returns                                                 |
|----------------|----------------------|---------------------------------------------------------|
| AcoustID       | API key + fingerprint| MBID candidates with score                              |
| MusicBrainz    | MBID *or* artist+title| Recording/release tags, genre tags                    |
| Discogs        | OAuth token          | Release styles + genres (good for electronic taxonomy) |
| Last.fm        | API key              | Crowd-sourced top tags (noisy but broad)               |

**Order is config-driven.** A provider chain merges into a single `LookupResult`. Conflicting genres become ranked `GenreCandidate`s — the mapping engine sees the full ranked list and picks via its rules.

**Resilience:** each provider's HttpClient is registered via `IHttpClientFactory` in `ServiceCollectionComposer` and wrapped with `AddStandardResilienceHandler` from `Microsoft.Extensions.Http.Resilience` (Polly v8 under the hood):
- 3 retries with exponential backoff + jitter on transient errors (5xx / 408 / network failures).
- Rate-limit-aware backoff on 429/503; `Retry-After` header is honoured.
- Circuit breaker on sustained failure to one upstream.
- Per-attempt timeout 10s, total-request timeout 45s (each request can retry within that budget).

Each provider also self-throttles via `RateLimiter` to the documented req/s floor (MusicBrainz 1.1s, AcoustID 350ms, Discogs 1.1s, Last.fm 200ms) — the Polly pipeline handles spikes, the self-throttle handles steady-state. When `online_required: false`, lookups fail open — pipeline continues with whatever local analysis produced.

**AcoustID prerequisite:** the AcoustID endpoint refuses without `duration`. `TagLibTagReader` reads container-decoded duration via `TagLib# Properties.Duration` and threads it through `LookupQuery.DurationSeconds`. Without that, the chain falls through to MusicBrainz's free-text search instead of MBID-anchored lookup.

**Cache:** lookup responses cached on disk, keyed by fingerprint or `artist|title|album`. TTL configurable. Honor `--no-cache` and `--refresh-cache`.

**Cache directory (cross-platform default):**
```
Environment.GetFolderPath(SpecialFolder.LocalApplicationData) / "RayTagger" / "cache"
```
Resolves to:
- macOS: `~/Library/Application Support/RayTagger/cache`
- Linux:  `~/.local/share/RayTagger/cache` (or `$XDG_DATA_HOME` when set)
- Windows: `%LOCALAPPDATA%\RayTagger\cache`

Users can override via `lookup.cache.directory` in `tagger.yaml`. Don't hard-code `~/.cache/tagger` — that's Linux-XDG-specific and is unexpected on the other platforms.

---

## 5. Mapping rule engine (`RayTagger.Core/Mapping`)

### 5.1 Schema

```yaml
version: 1

defaults:
  on_match: stop            # 'stop' | 'continue'
  case_sensitive: false

rules:
  - name: "Tech House refinement"
    when:
      genre: ["House", "Tech House"]
      bpm: { min: 122, max: 130 }
      key_mode: minor
    set:
      genre: "Tech House"
      subgenre: "Peak Time"

  - name: "House family → Electronic"
    when:
      genre: ["House", "Deep House", "Soulful House", "Funky House"]
    set:
      genre: "Electronic"
      subgenre: "House"

  - name: "Jazz House variants"
    when:
      genre_regex: "(?i)^(jazz.?house|electronic.?jazz|nu.?jazz)$"
    set:
      genre: "House"
      subgenre: "Jazz"
    on_match: continue       # keep evaluating; later rule may further refine
```

### 5.2 `when` predicates

| Key              | Type                                | Semantics                                                  |
|------------------|-------------------------------------|------------------------------------------------------------|
| `genre`          | list of strings or sentinel         | Any match (case per `defaults.case_sensitive`). Sentinels: `:missing:` (no genre set), `:any:` (any non-empty value). |
| `genre_regex`    | regex string                        | .NET regex on the resolved genre value (never matches when value is null). |
| `subgenre`       | list of strings or sentinel         | As above for sub-genre.                                    |
| `subgenre_regex` | regex string                        |                                                            |
| `bpm`            | `{ min, max }`                      | Inclusive range. Skips rule when BPM is null (no false match against missing data). |
| `key`            | list of strings                     | Match Camelot (`8A`) or standard (`Am`).                   |
| `key_mode`       | `major` \| `minor`                  | Match key mode regardless of root.                         |
| `energy`         | `{ min, max }`                      | Inclusive range, 1-10. Skips when energy is null.          |
| `artist`         | list of strings                     |                                                            |
| `artist_regex`   | regex                               |                                                            |
| `path_glob`      | glob string                         | Match against the source file path.                        |
| `any_of`         | list of `when` blocks               | Logical OR.                                                |
| `all_of`         | list of `when` blocks               | Logical AND (default for sibling keys).                    |
| `not`            | `when` block                        | Logical NOT.                                               |

Multiple sibling keys inside `when` are implicit `all_of`.

**Catch-all rule.** An omitted `when:` block (or an empty one) matches every file. Use it as a final fallback rule:

```yaml
- name: "Default: untagged files become 'Unsorted'"
  when: {}
  set:
    genre: "Unsorted"
```

### 5.3 `set` actions

| Key              | Effect                                                                |
|------------------|-----------------------------------------------------------------------|
| `genre`          | Replace resolved genre. Empty string `""` or explicit `null` clears the field. |
| `subgenre`       | Replace resolved sub-genre. Same null/empty semantics.                |
| `add_keyword`    | Append to a list-like tag (e.g. comments / custom).                   |
| `tag.<name>`     | Write a custom tag field (e.g. `tag.mood: "Driving"`).                |

`set` always marks the affected fields with source `Rules` — see §6.2 on how this interacts with `existing_tags_policy`.

### 5.4 Evaluation

1. Pipeline merges existing tags + analysis + lookup candidates into a `ResolvedTrackTags` (each field tagged with its `TagFieldSource`). The highest-confidence lookup candidate becomes the initial `Genre`/`SubGenre`; existing tags take precedence over lookup if non-empty.
2. Rules evaluated in declared order. Each rule's `when` is evaluated against the current `ResolvedTrackTags`.
3. On match: `set` is applied, mutating fields and stamping their `Source = Rules`. Behavior after match follows `on_match` (default `stop` from `defaults`).
4. After all rules complete, the `ResolvedTrackTags` flows to the writer.

**Determinism:** evaluation must be deterministic — no random tie-breaking inside the engine. Confidence-ranked candidates from the lookup stage are passed in as ordered lists; future versions may expose them to rules via `lookup.discogs.genre` style accessors.

### 5.5 Validation

`mappings.yaml` is validated on load (`IMappingRulesLoader`). Errors: unknown keys under `when`/`set`, invalid regex, BPM/energy ranges inverted, rule name duplication, unrecognised sentinel values. Validation errors abort the run with a clear pointer to the offending rule (file:line if YAML library exposes it).

---

## 6. Tag writing (`RayTagger.Metadata`)

### 6.1 Logical → frame mapping (defaults)

| Logical        | MP3 (ID3v2.4)        | FLAC (Vorbis)    | AIFF (ID3v2.4)         |
|----------------|----------------------|------------------|-------------------------|
| Title          | `TIT2`               | `TITLE`          | `TIT2`                  |
| Artist         | `TPE1`               | `ARTIST`         | `TPE1`                  |
| Album Artist   | `TPE2`               | `ALBUMARTIST`    | `TPE2`                  |
| Album          | `TALB`               | `ALBUM`          | `TALB`                  |
| Year           | `TDRC`               | `DATE`           | `TDRC`                  |
| Genre          | `TCON`               | `GENRE`          | `TCON`                  |
| Sub-Genre      | `TXXX:SUBGENRE`      | `SUBGENRE`       | `TXXX:SUBGENRE`         |
| BPM            | `TBPM`               | `BPM`            | `TBPM`                  |
| Key (standard) | `TKEY`               | `INITIALKEY`     | `TKEY`                  |
| Camelot Key    | `TXXX:CAMELOTKEY`    | `CAMELOTKEY`     | `TXXX:CAMELOTKEY`       |
| Energy (1-10)  | `TXXX:ENERGYLEVEL`   | `ENERGYLEVEL`    | `TXXX:ENERGYLEVEL`      |
| Comment        | `COMM`               | `COMMENT`        | `COMM`                  |

**Key notation is policy:** `TKEY` (and Vorbis `INITIALKEY`) **always** receive the standard notation (`Am`, `F#m`, …) per the ID3v2.4 specification. `TXXX:CAMELOTKEY` (and Vorbis `CAMELOTKEY`) **always** receive Camelot Wheel notation (`8A`, `5B`, …). When key analysis is enabled, both frames are written. Don't put Camelot into `TKEY` — third-party players parse it as Roman-numeral and silently mangle.

**TXXX frame choices** (`TXXX:CAMELOTKEY`, `TXXX:ENERGYLEVEL`, `TXXX:SUBGENRE`) follow conventions used by Traktor / Beatport / various DJ-tool ecosystems. Compatibility with Mixed In Key has *not* been verified on real fixtures yet — MIK's exact write behavior varies by version and includes comment-field writes in addition to (or instead of) TXXX frames. Treat as "DJ-tool-friendly", not "MIK drop-in" until a fixture test confirms otherwise.

**Virtual DJ compatibility.** Virtual DJ reads the standard frames directly: `TBPM` for BPM, `TKEY` (standard notation, e.g. `Am`, `F#m`) for key, `TCON` for genre. These are exactly the frames Tagger writes by default per the table above — no VDJ-specific frame additions are required. VDJ ignores the extra `TXXX:CAMELOTKEY` / `TXXX:ENERGYLEVEL` frames Tagger writes (they don't conflict). Fixture round-trip with a current VDJ library is still pending Phase 7 polish, but the mapping is structurally correct.

User can remap any logical field via the `tag_fields` block in `tagger.yaml`.

### 6.2 Write policy and field-source resolution

`existing_tags_policy` operates on the per-field `TagFieldSource` (see §2), not on the raw on-disk tag. The matrix:

| `existing_tags_policy` | Field source `Existing` | `Analysis` | `Lookup` | `Rules` |
|------------------------|------------------------|------------|----------|---------|
| `skip_if_present`      | preserve               | write      | write    | **write** |
| `fill_only_empty`      | preserve               | write only if existing is empty | write only if existing is empty | **write** |
| `always_overwrite`     | write                  | write      | write    | write   |

The crucial cell is the bottom-right of the first two rows: **mapping rules always overwrite, regardless of policy.** Rationale: rules are the user's explicit declarative intent — they exist precisely *to* re-tag existing files. A user-defined "House → Electronic" rule that fails to fire because of `skip_if_present` would silently break the user's expectation.

Per-dimension `min_confidence` thresholds (see §3) sit *above* this matrix: an analysis or lookup value below threshold is dropped before this table is consulted.

Backup:

| Policy           | Effect                                                              |
|------------------|---------------------------------------------------------------------|
| `backup: true`   | Before any write, dump the current tag block to a sidecar (`<file>.tagger.bak.<timestamp>.yaml`) so writes are reversible. Timestamp suffix prevents collisions on repeated runs. |

### 6.3 Safety

- **Atomic write.** Tagger copies the audio file to `<path>.tagger.tmp`, lets TagLib# rewrite that temp, then `File.Move` with overwrite atomically replaces the original. POSIX `rename(2)` and Windows `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING` are both atomic on the same volume — the reader either sees the old bytes or the new bytes, never a torn write.
- **File-lock pre-flight.** Before backup or temp-staging, Tagger opens the file with `FileShare.None`. If another process holds it (DJ software previewing, an mp3 indexer scanning), the writer fails fast with a structured `MetadataException` mapped to a per-file `StageError` instead of letting TagLib# throw mid-save.
- **Custom-field write-through.** `set: { tag.<name>: value }` rules write to TXXX (MP3/AIFF) and Vorbis (FLAC) frames under the description from the loaded `TagFieldMap`. Existing custom frames (ReplayGain, MBID, …) round-trip through `TrackTags.Custom` without modification.
- **Sidecar safety.** On any write exception, the sidecar backup remains so `raytagger restore` can roll it back.

### 6.4 `tag_fields` override

`write.tag_fields` in `tagger.yaml` overrides the per-format frame name for the user-defined dimensions (sub-genre, Camelot key, energy). The token grammar:

```
ID3:<frame>                e.g. ID3:TBPM         (informational — standard frame fixed by spec)
ID3:TXXX:<description>     e.g. ID3:TXXX:CAMELOTKEY   (override the TXXX description)
VORBIS:<field>             e.g. VORBIS:CAMELOTKEY     (override the Vorbis field name)
```

Standard ID3v2 frames (TCON, TBPM, TKEY, TIT2, TPE1, TDRC) and their Vorbis equivalents (GENRE, BPM, INITIALKEY) are spec-fixed; listing them in the block is accepted but informational. `TagFieldMapBuilder` validates token syntax at config-load time and reports malformed entries as `ConfigurationError`s alongside other validation errors.

---

## 7. Sorting (optional)

```yaml
sort:
  enabled: true
  destination: "/Users/me/Music/library"
  pattern: "{genre}/{subgenre?}/{artist} - {title}.{ext}"
  conflict: rename          # rename | skip | overwrite
  preserve_source: false
```

Placeholders: `{genre}`, `{subgenre}`, `{artist}`, `{title}`, `{album}`, `{year}`, `{bpm}`, `{key}`, `{camelot}`, `{ext}`. The `?` suffix marks a placeholder optional — the path segment collapses if the value is null/empty.

Conflict handling:
- `rename` — append ` (1)`, ` (2)`, … to the file stem.
- `skip` — leave the source file in place; log.
- `overwrite` — replace the destination (only if backup is on; otherwise the destination is logged for review).

---

## 8. Configuration (`RayTagger.Core/Configuration`)

### 8.1 Sources and precedence (highest wins)

1. CLI options (e.g. `--dry-run`, `--config`, `--source`).
2. Environment variables (override `.env`).
3. `.env` file in the working directory.
4. `tagger.yaml` (default `./tagger.yaml`; CLI `--config` overrides).
5. Built-in defaults.

`mappings.yaml` is loaded by a separate `IMappingRulesLoader` and exposed via `IOptionsMonitor<MappingRuleSet>` so it can be hot-reloaded in a future watch mode.

### 8.2 Path expansion

Every path-typed config value runs through a normalizer:

| Form                          | Resolved to                                                       |
|-------------------------------|-------------------------------------------------------------------|
| `~` or `~/foo`                | `$HOME/foo` (or `%USERPROFILE%\foo` on Windows).                  |
| `./foo`, `../foo`             | Relative to the directory of `tagger.yaml`, not `Environment.CurrentDirectory`. |
| Absolute paths                | Used as-is.                                                       |
| Path containing `${env:VAR}`  | Substituted (see §8.3) then re-normalized.                        |

### 8.3 Environment-variable substitution

`${env:NAME}` placeholders are substituted into **any** string value in `tagger.yaml` at load time (not lazy). Resolution order:

1. Process environment (set via shell or system).
2. `.env` file values (if present).
3. If still unresolved: load fails with `ConfigurationException` quoting the YAML path (`lookup.api_keys.acoustid`) and the missing variable name (`ACOUSTID_API_KEY`).

A `${env:NAME:-default}` shell-style fallback is **not** supported in v1; if you want a default, set it in `.env`.

### 8.4 Cache directory

When `lookup.cache.directory` is unset, the resolver returns the OS-appropriate per-user data directory (see §4). This is computed by `RayTagger.Core.IO.IUserDataDirectoryProvider`, which can be substituted in tests.

---

## 9. Logging (Serilog)

`Microsoft.Extensions.Hosting` wires Serilog as the host logger. Console sink by default; file sink writes one log file per run (`logs/tagger-{yyyyMMdd-HHmmss}.log`). Per-file events are enriched with the track path so a `grep` against the log isolates a single file's run.

Optionally, `Raycoon.Serilog.Sinks.SQLite` (sibling project) can be enabled in config to persist structured run history for later analysis — useful when scanning huge libraries.

---

## 10. Design decisions / trade-offs

| Decision                                            | Why                                                                                                              |
|-----------------------------------------------------|------------------------------------------------------------------------------------------------------------------|
| .NET 10 over Python                                 | Team stack consistency; TagLib# is the best tag library; AOT publish gives single-binary distribution.           |
| Shell out to native CLIs over P/Invoke              | Robustness; Essentia has a heavyweight build matrix we'd rather not vendor, and the process boundary also keeps Tagger Apache-2.0-compatible against Essentia's AGPL. |
| One Essentia run feeds three analyzers              | BPM/key/energy come from the same JSON anyway; running the extractor three times would triple the cost for zero benefit. The `(path, mtime)` cache key handles re-tags within the same process. |
| YAML over JSON/TOML for user-edited config          | Comments, multi-line strings, hierarchical mapping rules read naturally.                                         |
| Genre rules engine in-house vs scripting host       | First-class type checking & validation; users get clear errors. Power users can graduate to scripted rules later. |
| DJ-tool-friendly TXXX frames (no MIK promise yet)   | TXXX:CAMELOTKEY / TXXX:ENERGYLEVEL are widely read by DJ ecosystems; MIK-specific compatibility deferred until verified on real fixtures. |
| `.slnx` (XML) over `.sln`                           | .NET 10 default; simpler, diff-friendly; both CLI and Rider support it.                                          |
| Confidence-ranked candidates from lookup            | Mapping rules can prefer "Discogs styles" over "Last.fm tags" when both disagree.                                |

---

## 11. Out of scope (current iteration)

- Real-time / watch mode (file-system events).
- Reading proprietary DJ-app DBs (rekordbox, Serato) — possible later via separate adapters.
- ML-based genre classification (Essentia models exist, but accuracy on a generic library is poor without per-collection tuning).
- Mobile or web UI.
- Mixed In Key API integration (no public API exists at time of writing). Verified MIK-tag-frame round-tripping is a near-term TODO, not a feature claim.
