# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**RayTagger** is a cross-platform .NET 10 tool for automatic tagging of music files (MP3, FLAC, AIFF). It detects BPM, musical key, energy level and genre by orchestrating native audio-analysis CLIs (Essentia for BPM/key/energy via a single shared run; Chromaprint/fpcalc for fingerprinting) and online metadata providers (AcoustID, MusicBrainz, Discogs, Last.fm). A user-defined mapping rule engine consolidates messy upstream genres into a clean target taxonomy and optionally sorts files into a structured library directory.

**Status: working application.** All pipeline stages, analyzers, lookup providers, the Avalonia UI, energy calibration, taxonomy-aware genre resolution, native-tool auto-bootstrap, the SQLite log sink, and audio-based genre classification (heuristic + 3 TensorFlow models with per-parent aggregation) are implemented. Open work items live in `docs/ROADMAP.md` (Phase 7 fixture round-trip, MIK validation). See `docs/ARCHITECTURE.md` for the full design and `docs/PLAN_GENRE_CLASSIFICATION.md` for the genre-classifier specifics.

## Build Commands

```bash
dotnet restore RayTagger.slnx
dotnet build RayTagger.slnx
dotnet build RayTagger.slnx -c Release
dotnet test  RayTagger.slnx
dotnet test --filter "FullyQualifiedName~MappingRuleEngineTests"      # single test class
dotnet test --filter "Category!=Integration"                          # skip integration tests
dotnet run --project src/RayTagger.Cli -- --help                 # run CLI
dotnet publish src/RayTagger.Cli -c Release -r osx-arm64 --self-contained  # standalone binary
```

`.slnx` is the modern XML solution format (.NET 10 default). `dotnet` accepts it directly.

## Architecture

```
src/
├── RayTagger.Core/      Models, abstractions, pipeline orchestration,
│                             mapping rule engine, taxonomy resolver,
│                             YAML configuration loaders/validators.
├── RayTagger.Metadata/  TagLib# wrappers; per-format tag-frame mapping
│                             (MP3/ID3v2, FLAC/Vorbis, AIFF/ID3); backup sidecars.
├── RayTagger.Analysis/  Audio analysis adapters — shells out to
│                             fpcalc (Chromaprint) and essentia_streaming_extractor_music.
│                             Includes the native-tool bootstrap (download/verify/cache).
├── RayTagger.Lookup/    Online providers (AcoustID, MusicBrainz,
│                             Discogs, Last.fm) with resilient HTTP, on-disk cache.
├── RayTagger.Hosting/   DI composition (ServiceCollectionComposer), PipelineFactory,
│                             LookupRunnerBuilder, per-track lookup service, status
│                             reporting, User-Agent state. Shared by CLI and UI.
├── RayTagger.Ui/        Avalonia desktop app — ScanViewModel, results grid,
│                             rule editor, AppliedRulesDialog, energy-calibration
│                             coordinator, per-track API button.
└── RayTagger.Cli/       Entry point. System.CommandLine 2.0.8 + Serilog +
                              Microsoft.Extensions.Hosting. Verbs: scan, explain,
                              setup, restore, calibrate-energy.
```

**Pipeline stages** (detail in `docs/ARCHITECTURE.md`):
`Discover → ReadExistingTags → Analyze → Lookup (+ optional GenreClassify) → Merge (TagMerger + TaxonomyGenreResolver) → ApplyMappingRules → WriteTags → (optional) MoveFile`

**Key abstractions:**
- `IBpmAnalyzer`, `IKeyAnalyzer`, `IEnergyAnalyzer`, `IFingerprintAnalyzer` — strategy per dimension. User picks implementation via config.
- `IMetadataProvider` — online providers chained with fallback order.
- `IGenreClassifier` — audio-based genre voters (heuristic + TF models) whose output is merged into the candidate stream the resolver sees. See "Genre Classification" below.
- `IMappingRuleEngine` — evaluates YAML-defined rules with conditions (genre patterns, BPM range, key, energy range, regex).
- `ITagWriter` — resolves logical fields (genre, bpm, key, …) to format-specific frames per the `tag_fields` map.

**Dependency direction (no cycles):**
`Cli`/`Ui` → `Hosting` → {`Core`, `Metadata`, `Analysis`, `Lookup`}; `Analysis` → {`Core`, `Metadata`}; `Metadata`/`Lookup` → `Core`. Never add an upstream reference back. The `Hosting` project exists to keep CLI and UI from re-implementing the DI/pipeline wiring twice — both consume `PipelineFactory` and `LookupRunnerBuilder` from there.

## Native Tool Dependencies

.NET orchestrates; audio analysis runs in external binaries that must be on `PATH` (or auto-downloaded via `native-tools.yaml`).

macOS:
```bash
brew install chromaprint              # fpcalc — fingerprinting for AcoustID
brew install MTG/essentia/essentia    # BPM, key (EDMA profile), energy
```

**Optional — TensorFlow genre classifiers** (when the corresponding flags in `analysis.genre_classifier.tensorflow` are enabled):
```bash
pip install essentia-tensorflow       # Python 3.10+, installs TensorFlow as transitive dep
```
Tagger invokes the helper script in `tools/raytagger-genre-classifier/` as a subprocess; the model `.pb` files are auto-downloaded into the user data directory on first run.

`RayTagger.Analysis` **probes PATH at startup** via `IAnalysisToolProbe` and degrades gracefully — logs a warning and disables the affected dimension if a binary is missing, never crashes.

**One-shot Essentia design.** BPM, key and energy all derive from a single `essentia_streaming_extractor_music` JSON output per track. The fork+exec happens in `EssentiaAnalysisService`, which caches results per `(path, last-write-time)` so the three analyzers share one run. Don't add a second BPM or key tool unless there's a concrete reason (e.g. Essentia install friction on a new platform) — Essentia's EDMA key profile and tempo-histogram confidence make it the strongest single source for DJ-oriented material.

**Essentia license boundary.** Essentia is AGPL-3.0. We invoke `essentia_streaming_extractor_music` as a subprocess and **never link against it** — the process boundary keeps Tagger Apache-2.0 compatible. Don't link Essentia's C++ libraries into managed code or bundle its binaries inside Tagger's distribution.

**Adding a new analyzer.** For a *new dimension* (e.g. mood), implement the appropriate interface in `RayTagger.Analysis/`. If the new dimension is derivable from the Essentia JSON, extend `EssentiaJsonParser` + `EssentiaResult` and inject `IEssentiaAnalysisService` into the analyzer — no extra fork. Only reach for `NativeProcessRunner` directly if a separate binary is genuinely required. **For genre specifically**, implement `IGenreClassifier` instead of a new analyzer interface — the output flows into the resolver's candidate stream, not into `AnalysisResult`. See `docs/PLAN_GENRE_CLASSIFICATION.md`.

## Configuration

Three artifacts (paths CLI-overridable):
- `tagger.yaml` — scan paths, analyzer selection per dimension, lookup providers, tag-field map, sort destination, logging.
- `mappings.yaml` — ordered genre/sub-genre rules with optional BPM/key/energy conditions.
- `.env` (gitignored) — API keys: `ACOUSTID_API_KEY`, `DISCOGS_TOKEN`, `LASTFM_API_KEY`.

Schemas live in `RayTagger.Core/Configuration/*Options.cs` and **must** be kept in sync with `samples/*.example.yaml`. Treat the example files as the documented contract — when you change schema, update the example in the same commit.

**Path normalisation** (every path-typed value):
- `~`/`~/foo` → user home (cross-platform).
- Relative paths resolve against the directory of `tagger.yaml`, *not* `Environment.CurrentDirectory`.
- `${env:VAR}` placeholders substituted at load time; missing required vars fail loud with the YAML path quoted.

**Cache directory default** (cross-platform): `Environment.SpecialFolder.LocalApplicationData / "RayTagger" / "cache"` — *not* `~/.cache/tagger` (that's Linux-XDG-specific). Override via `lookup.cache.directory`.

## Tag-Frame Mapping

Logical fields → format-specific frames (defaults; user-overridable in `tag_fields`):

| Logical     | MP3 (ID3v2.4)        | FLAC (Vorbis)    | AIFF (ID3v2.4)         |
|-------------|----------------------|------------------|-------------------------|
| Genre       | `TCON`               | `GENRE`          | `TCON`                  |
| Sub-Genre   | `TXXX:SUBGENRE`      | `SUBGENRE`       | `TXXX:SUBGENRE`         |
| BPM         | `TBPM`               | `BPM`            | `TBPM`                  |
| Key (std.)  | `TKEY`               | `INITIALKEY`     | `TKEY`                  |
| Camelot Key | `TXXX:CAMELOTKEY`    | `CAMELOTKEY`     | `TXXX:CAMELOTKEY`       |
| Energy 1-10 | `TXXX:ENERGYLEVEL`   | `ENERGYLEVEL`    | `TXXX:ENERGYLEVEL`      |

**Key-notation policy:** `TKEY` / `INITIALKEY` **always** receive standard notation (`Am`, `F#m`) per ID3v2.4 spec; `TXXX:CAMELOTKEY` / `CAMELOTKEY` **always** receive Camelot Wheel (`8A`). When key analysis is enabled, both frames are written. Never mix the two — players parse `TKEY` as Roman-numeral and silently corrupt Camelot values placed there.

The `TXXX:*` frame choices follow conventions used by Traktor / Beatport / various DJ-tool ecosystems. **MIK compatibility is not yet verified** on real fixtures — claim it as "DJ-tool-friendly" until fixture tests confirm round-trip with a current MIK install.

## Write Policy / Field-Source Resolution

The write stage operates on `ResolvedTrackTags` where each field is tagged with its `TagFieldSource ∈ {Existing, Analysis, Lookup, Rules}`. `existing_tags_policy` rules:

- `skip_if_present`: preserve fields with source `Existing`; write fields from `Analysis`, `Lookup`, **and `Rules`**.
- `fill_only_empty`: write `Analysis`/`Lookup` only when existing is empty; `Rules` always writes.
- `always_overwrite`: write everything.

**Key invariant:** mapping rules (`Rules` source) always win, regardless of policy. Rules are the user's explicit declarative intent — silently dropping them because of `skip_if_present` would break user expectations.

Per-dimension `min_confidence` thresholds gate `Analysis` / `Lookup` values *before* this policy table applies — a low-confidence Essentia BPM (from `bpm_histogram_first_peak_weight`) never reaches the policy.

See `docs/ARCHITECTURE.md §6.2` for the full matrix.

## Genre Resolution (taxonomy-aware)

When `lookup.taxonomy_resolution: true` (default), genre / sub-genre values from the API are filtered through `taxonomy.yaml` before they touch `ResolvedTrackTags`. The flow lives in `RayTagger.Core.Mapping.TaxonomyGenreResolver` and is called from `TagMerger`:

1. **Genre match search.** For each API `GenreCandidate` in confidence order, find every `taxonomy.genres` entry that appears as a whole word (case-insensitive) inside the candidate string. Longest match wins, ties broken by YAML order. Stop at the first candidate that produced any match.
2. **Subgenre match search.** With `chosen_genre` fixed, search for `taxonomy.subgenres[chosen_genre]` entries in two sources: (a) the *rest* of the matched genre candidate (genre word cut out with `\b…\b` + whitespace collapse), and (b) every `SubGenreCandidate` from the API (e.g. Discogs `style`). Same "longest wins" rule, tiebreak by confidence then YAML order.
3. **Apply with existing-tag protection.**
   - **Genre:** write `chosen_genre` only if `existing_genre` is empty OR not in `taxonomy.genres`. Else keep existing.
   - **Subgenre:** same rule against `taxonomy.subgenres[chosen_genre]`.
4. **Fallback** (no genre matched at all + `existing_genre` empty): take the top-1 raw candidate value into `proposed_genre` so the user sees *something* they can later add to taxonomy. No fallback for subgenre — without a genre anchor it has no meaning.

The resolver exposes a `CandidateTraceEntry` audit trail on `ResolvedTrackTags.GenreLookupTrace`. The UI shows it in the AppliedRulesDialog's second section; the CLI logs the chosen path per track.

**Per-track button (UI):** The "API" column in the results grid calls `IPerTrackLookupService` which reuses the same `LookupRunnerBuilder` + `TaxonomyGenreResolver`. The button deliberately ignores `lookup.enabled` (that flag only gates the automatic scan-time lookup); it works whenever `lookup.providers` contains at least one provider with credentials (MusicBrainz always counts — no key needed).

**Non-taxonomy highlight:** Genre / sub-genre values that are non-empty but missing from the taxonomy render in dark blue (`TaxonomyHighlightBrushConverter`) — same colour the BPM cell uses for "forced fallback", so the user has one consistent "this is off-vocabulary" cue.

**Disable the resolver:** Set `lookup.taxonomy_resolution: false` to fall back to the legacy "top-1 candidate wins blindly" behaviour. Useful only when the user maintains tags by other means and doesn't want a taxonomy.

## Genre Classification (audio-based)

Two opt-in classifier families enrich the `GenreCandidate` stream the `TaxonomyGenreResolver` consumes. Each individual classifier is an `IGenreClassifier`, runs in parallel via `GenreClassifierRunner`, and feeds its output into `LookupResult.GenreCandidates` with `Source = "classifier:<name>"`. The resolver applies the same whole-word + longest-match logic to classifier hits as it does to provider hits — no special-casing in the merger. Full design in `docs/PLAN_GENRE_CLASSIFICATION.md`.

**All flags default off**: switching every `analysis.genre_classifier.*.enabled` to `false` (the defaults) yields byte-identical pipeline behaviour to a Tagger build without classifiers. The DI registers `NoopGenreClassifierRunner` and the pipeline stage is a pure pass-through.

- **Tier 3 — `HeuristicGenreClassifier` (`heuristic`).** Pure-rule scoring over DSP-only Essentia descriptors (`tonal.chords_changes_rate`, `tonal.key_scale`, `lowlevel.spectral_centroid`, `lowlevel.dynamic_complexity`, plus the existing BPM / danceability / beats-loudness fields). Zero new dependencies — runs inside the existing single Essentia fork via `IEssentiaAnalysisService`. Lineup: **8 parent genres** (House / Techno / Trance / Drum and Bass / Dubstep / Hip Hop / Ambient / Downtempo). Same-genre subgenres (Tech House vs Deep House) are deliberately out of scope; that's the TF tier's job.

- **Tier 1 — `TensorflowGenreClassifier` (3 models).** Essentia-pretrained models invoked via a Python helper script (`tools/raytagger-genre-classifier/`) that ships with Tagger and requires `pip install essentia-tensorflow` (Python 3.9–3.12). Each model is independently switchable in `tagger.yaml`:
  - `genre_electronic` — 5 classes (ambient / dnb / house / techno / trance). Default `min_confidence: 0.65` because it overlaps with the heuristic; acts as a second-opinion check.
  - `mtg_jamendo` — 87 classes (Rock / Pop / R&B / Soul / Jazz / Funk / Reggae / Classical / etc.). Default `min_confidence: 0.50`.
  - `discogs_effnet` — 400 fine-grained Discogs styles (Deep House, Tech House, Melodic Techno, Liquid DnB, …). Default `min_confidence: 0.50`. Aggregation (see below) defaults on.

  Models live under `<data-dir>/models/<model-key>/` and are downloaded on first use via the same `NativeToolBootstrapper` that handles Essentia/fpcalc — model entries in `native-tools.yaml` under a `models:` section, SHA-256 pinned, atomic-promote into the cache after verification. Python presence + script + model files are probed per scan; missing pieces disable that specific classifier with a friendly status line, never crash.

  **Subprocess cost**: ~1.5 s per track per model on Apple Silicon (measured in B6 integration tests). The CLI startup banner surfaces the cumulative estimate when ≥1 TF model is active so users with large libraries see the tradeoff before scan-time. Deferred optimisations (daemon mode, batch mode, file dedup) tracked in `docs/PLAN_GENRE_CLASSIFICATION.md §5.7`.

- **§4.0c Aggregation — per-parent summing of fine-grained outputs.** discogs-effnet (400 classes) routinely spreads probability mass over many subgenres of the same parent — e.g. a Hip Hop track gets `Hip Hop---Conscious 0.11`, `Hip Hop---Jazzy 0.07`, `Hip Hop---RnB/Swing 0.07`, etc., while the top-1 raw pick might be a single `Electronic---Downtempo 0.16`. The TF classifier aggregates by parent genre (sum of contributing p, filtered by `aggregate_per_candidate_floor` to suppress long-tail noise) and emits a synthesised "Hip Hop" candidate with source suffix `:aggregated` ahead of the raw top-K. When no parent clears `aggregate_min_total` (default 0.25) and `aggregate_fallback_on_diffuse: true` (default), the argmax parent emits anyway with `:aggregated-fallback` so the trace shows the uncertainty. Aggregation is per-model configurable; default on for `discogs_effnet`, off for the others. Trade-off: aggregated candidates have parent-only `Value`, so subgenre detection from discogs-effnet is suppressed — set `aggregate_top_k: false` to recover raw subgenre detection.

**Pipeline integration**: the `IGenreClassifierRunner` stage in `TagPipeline.ProcessFileAsync` sits between Lookup and Merge. Classifier candidates are **appended** to `LookupResult.GenreCandidates` (provider hits retain top spots — resolver "stops at first taxonomy match" semantics mean classifiers only rescue when providers had nothing in the taxonomy). Per-classifier failures are isolated; a failing TF model doesn't kill the others.

**Label normalisation** (`ClassifierLabelNormaliser`): every emitted label goes through `_/-//` → space, lowercase, English depluralisation (with "ss" guard). TF wrappers additionally apply a per-model `remap/*.json` BEFORE the normaliser to handle model-specific abbreviations the resolver wouldn't otherwise match (e.g. `Drum n Bass` → `Drum and Bass`, `dnb` → `Drum and Bass`). A dev tool at `tools/raytagger-genre-classifier/dev/analyze_remap_coverage.py` simulates the full pipeline against every label of every model and reports coverage — re-run when editing taxonomy or remap files.

**License note**: the Python bridge invokes `essentia-tensorflow` as a subprocess — same Apache-2.0/AGPL boundary as the existing `essentia_streaming_extractor_music` call. Don't link Essentia's Python module into the .NET process and don't bundle the `.pb` model files inside Tagger's distribution.

## Raycoon Conventions (mirrors sibling projects)

- **Central Package Management**: all versions in `Directory.Packages.props`. Csproj `<PackageReference>` entries carry **no** `Version` attribute.
- **Common build properties** in `Directory.Build.props` — do not redeclare `TargetFramework`/`Nullable`/`ImplicitUsings` in individual csproj files.
- **Strict analyzers**: `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-all`. A global suppression baseline (CS1591, CA1303, CA2007, CA1031, CA1305, CA2227, CA1805, CA1002) lives in `Directory.Build.props` — see the inline comments for each rule. Project-level suppressions go in the csproj with their own justification.
- **File-scoped namespaces** (`namespace Foo;` not braced).
- **Private fields** prefixed `_camelCase`.
- **`var`** preferred throughout.
- **Doc comments** in English; user-facing strings (CLI messages, log messages) in German.
- **License**: Apache-2.0.
- **Logging**: Serilog (consistent with `Raycoon.Serilog.Sinks.SQLite`). The SQLite sink is available if persistent log storage becomes desirable.

## Testing

- **xUnit + FluentAssertions + NSubstitute** — global usings configured in each test csproj, so individual test files don't need to import.
- Tests that need real audio files: fixtures live in `tests/<Project>/fixtures/audio/` (gitignored). Add a `fixtures/README.md` linking public-domain samples rather than committing copyrighted media.
- Tests that invoke native CLIs: mark `[Trait("Category", "Integration")]` and check binary availability in a fixture — skip rather than fail when the tool is missing.

## Pending Work

- **MIK / DJ-tool round-trip fixtures** — Phase 7 of `docs/ROADMAP.md`. The `TXXX:*` frame choices follow Traktor/Beatport conventions, but we still need fixture tests against a real MIK install before claiming compatibility.
- **Linux/Windows native-tool packaging** — auto-bootstrap manifest currently ships macOS-arm64 entries only.
- **Genre-classifier follow-ups** — see `docs/PLAN_GENRE_CLASSIFICATION.md §5.7` for deferred optimisations: Python daemon mode (avoid per-track subprocess cold-start), batch mode, cross-model file dedup, hyphenated-taxonomy-entry handling (`Lo-fi`).
