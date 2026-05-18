# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**RayTagger** is a cross-platform .NET 10 tool for automatic tagging of music files (MP3, FLAC, AIFF). It detects BPM, musical key, energy level and genre by orchestrating native audio-analysis CLIs (Essentia for BPM/key/energy via a single shared run; Chromaprint/fpcalc for fingerprinting) and online metadata providers (AcoustID, MusicBrainz, Discogs, Last.fm). A user-defined mapping rule engine consolidates messy upstream genres into a clean target taxonomy and optionally sorts files into a structured library directory.

**Status: scaffold phase** — solution skeleton + architecture decisions in place, business logic not yet implemented. See `docs/ARCHITECTURE.md` for the full design and `docs/ROADMAP.md` for implementation order.

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
│                             mapping rule engine, YAML configuration.
├── RayTagger.Metadata/  TagLib# wrappers; per-format tag-frame mapping
│                             (MP3/ID3v2, FLAC/Vorbis, AIFF/ID3).
├── RayTagger.Analysis/  Audio analysis adapters — shells out to
│                             fpcalc (Chromaprint) and essentia_streaming_extractor_music.
├── RayTagger.Lookup/    Online providers (AcoustID, MusicBrainz,
│                             Discogs, Last.fm) with resilient HTTP.
└── RayTagger.Cli/       Entry point. System.CommandLine 2.0.8 + Serilog +
                              Microsoft.Extensions.Hosting. Has InternalsVisibleTo
                              for RayTagger.Cli.Tests (command-parsing tests).
```

**Pipeline stages** (detail in `docs/ARCHITECTURE.md`):
`Discover → ReadExistingTags → Analyze → Lookup → ApplyMappingRules → WriteTags → (optional) MoveFile`

**Key abstractions:**
- `IBpmAnalyzer`, `IKeyAnalyzer`, `IEnergyAnalyzer`, `IFingerprintAnalyzer` — strategy per dimension. User picks implementation via config.
- `IMetadataProvider` — online providers chained with fallback order.
- `IMappingRuleEngine` — evaluates YAML-defined rules with conditions (genre patterns, BPM range, key, energy range, regex).
- `ITagWriter` — resolves logical fields (genre, bpm, key, …) to format-specific frames per the `tag_fields` map.

**Dependency direction (no cycles):**
`Cli` → {`Core`, `Metadata`, `Analysis`, `Lookup`}; `Analysis` → {`Core`, `Metadata`}; `Metadata`/`Lookup` → `Core`. Never add an upstream reference back.

## Native Tool Dependencies

.NET orchestrates; audio analysis runs in external binaries that must be on `PATH`.

macOS:
```bash
brew install chromaprint              # fpcalc — fingerprinting for AcoustID
brew install MTG/essentia/essentia    # BPM, key (EDMA profile), energy
```

`RayTagger.Analysis` **probes PATH at startup** via `IAnalysisToolProbe` and degrades gracefully — logs a warning and disables the affected dimension if a binary is missing, never crashes.

**One-shot Essentia design.** BPM, key and energy all derive from a single `essentia_streaming_extractor_music` JSON output per track. The fork+exec happens in `EssentiaAnalysisService`, which caches results per `(path, last-write-time)` so the three analyzers share one run. Don't add a second BPM or key tool unless there's a concrete reason (e.g. Essentia install friction on a new platform) — Essentia's EDMA key profile and tempo-histogram confidence make it the strongest single source for DJ-oriented material.

**Essentia license boundary.** Essentia is AGPL-3.0. We invoke `essentia_streaming_extractor_music` as a subprocess and **never link against it** — the process boundary keeps Tagger Apache-2.0 compatible. Don't link Essentia's C++ libraries into managed code or bundle its binaries inside Tagger's distribution.

**Adding a new analyzer.** For a *new dimension* (e.g. mood, sub-genre via ML), implement the appropriate interface in `RayTagger.Analysis/`. If the new dimension is derivable from the Essentia JSON, extend `EssentiaJsonParser` + `EssentiaResult` and inject `IEssentiaAnalysisService` into the analyzer — no extra fork. Only reach for `NativeProcessRunner` directly if a separate binary is genuinely required.

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

## What's NOT Yet Implemented

Architecture documented, projects build clean, **business logic is empty**. First implementation step is the domain model in `RayTagger.Core` (TrackFile, TrackTags, AnalysisResult, MappingRule), then proceed feature-by-feature per `docs/ROADMAP.md`. The CLI currently prints a stub message.
