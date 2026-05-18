# Roadmap

Phased implementation plan. Each phase is **shippable on its own** — the tool does something useful at the end of every phase, and later phases extend rather than rework.

## Phase 0 — Scaffolding ✅

Goal: empty solution that builds clean and reflects the target architecture.

- [x] Solution + 5 src projects + 5 test projects (incl. `RayTagger.Cli.Tests`).
- [x] Central Package Management, Directory.Build.props with Raycoon conventions + analyzer-suppression baseline.
- [x] CLAUDE.md, README.md, docs/ARCHITECTURE.md, docs/ROADMAP.md.
- [x] Sample `tagger.example.yaml`, `mappings.example.yaml`, `.env.example`.
- [x] CLI stub that prints version and args.

## Phase 1a — Domain + Configuration (offline, no I/O) ✅

Goal: domain model + YAML config + mapping rule loader compile and pass unit tests. Tool can't do anything end-to-end yet, but every subsequent stage has a stable data model and config surface to bind to.

- [x] `RayTagger.Core/Models`: domain records (`TrackFile`, `TrackTags`, `MusicalKey`, `AnalysisResult`, `LookupResult`, `GenreCandidate`, `PipelineOutcome`, `TagFieldSource` enum, per-field source tracking via `ResolvedField<T>` / `ResolvedTrackTags`).
- [x] `RayTagger.Core/Configuration`: option POCOs matching `tagger.example.yaml`, `TaggerOptionsLoader` (YamlDotNet) with tilde-expansion + `${env:VAR}` substitution that's YAML-comment-aware, structured validation errors via `ConfigurationException(YamlPath, Reason)`.
- [x] `RayTagger.Core/Mapping`: `MappingRuleSet` / `MappingRule` / `WhenClause` / `SetClause` / `GenrePattern` POCOs, `MappingRulesLoader` with `:missing:` / `:any:` sentinel support, catch-all rule semantics, `tag.NAME` flattening into `ExtraTags`. (Rule evaluator engine in Phase 3.)
- [x] Cross-platform cache-directory resolution helper (`IUserDataDirectoryProvider` / `UserDataDirectoryProvider`).
- [x] Unit tests in `RayTagger.Core.Tests`: 33 tests covering schema validation, env-var substitution (incl. YAML-comment skip), tilde expansion, mapping-rules schema, sentinel handling, catch-all rule, range validation, duplicate names, unknown keys.

**Done:** `dotnet build` clean, 33/33 tests green. `samples/tagger.example.yaml` and `samples/mappings.example.yaml` load and parse correctly.

## Phase 1b — Metadata read/write + CLI wire-up ✅

Goal: `raytagger scan <dir> --dry-run` walks a directory, reads existing tags via TagLib#, prints what it would change. `--write` performs idempotent tag I/O. Still no analysis or lookup.

- [x] `RayTagger.Metadata`: `ITagReader`/`ITagWriter` + TagLib# implementations, `AudioFormatDetector`, `KeyNotationConverter` (Standard↔Camelot bidirectional), `BackupSidecarWriter` (timestamped YAML sidecars), `FrameMapper` for per-format TXXX/Vorbis field writes, `PipelineAdapters` bridging Metadata to Core's pipeline-level abstractions.
- [x] `RayTagger.Core/Pipeline`: `IFileDiscoveryService` + impl with glob filtering, `ITagPipeline` + `TagPipeline` with per-file failure isolation, `ITagReaderAdapter`/`ITagWriterAdapter` interfaces so Core stays free of TagLib# dependency.
- [x] `RayTagger.Cli`: System.CommandLine 2.0.8 stable, `scan` verb with `--config`/`--source`/`--dry-run`/`--write`/`--verbose`, Serilog via `Serilog.Extensions.Logging` (no Hosting indirection yet — deferred until Phase 2 brings more services), Spectre.Console for tabular outcome rendering.
- [x] `InternalsVisibleTo` for `RayTagger.Cli.Tests`; 9 command-parsing + end-to-end tests including conflict-detection (--dry-run + --write).
- [x] Note: round-trip tests on real audio fixtures deferred — requires bundled silence MP3/FLAC/AIFF files. Will add in Phase 2 when we generate fixtures programmatically alongside analyzer tests.

**Done:** End-to-end `raytagger scan --config tagger.yaml --dry-run` works against any directory. Failures are per-file isolated. Exit codes: 0 success, 1 per-file failures, 2 config error, 64 invalid arguments. 94 tests green across the solution.

## Phase 2 — Audio analysis (offline) ✅

Goal: real BPM/Key/Energy detection via native CLIs. Still no online lookups.

- [x] `RayTagger.Analysis.Internal.NativeProcessRunner` — shared CLI wrapping with `ProcessStartInfo.ArgumentList` (no shell-injection), per-process timeout with kill-tree on expiry, cancellation propagation.
- [x] `IAnalysisToolProbe` / `AnalysisToolProbe` — caching PATH detection with version capture.
- [x] `EssentiaAnalysisService` — single fork+exec per track, parsed JSON cached by `(path, mtime)`; serves BPM, key (EDMA profile) and energy from one source. Replaced the earlier aubio + keyfinder-cli stack once Essentia proved both more accurate (EDMA on Beatport benchmarks, real BPM confidence) and architecturally cheaper (one process instead of three).
- [x] `EssentiaBpmAnalyzer`, `EssentiaKeyAnalyzer`, `EssentiaEnergyAnalyzer`, `ChromaprintFingerprintAnalyzer` (Essentia AGPL — process boundary, never linked).
- [x] Parsers (Essentia JSON, Chromaprint) as `internal static` classes with dedicated unit tests against synthetic outputs.
- [x] Pipeline stage Analyze: `IAnalysisRunner` (Core interface) / `AnalysisRunner` (Analysis impl) runs configured analyzers in parallel via `Task.WhenAll`, swallows per-analyzer failures.
- [x] `TagMerger` merges existing tags + analysis results per `existing_tags_policy` and per-dimension `min_confidence` thresholds; produces ResolvedTrackTags with proper field sources.
- [x] Analyzer selection wired through config (`analysis.<dim>.provider`) in `ScanHandler.BuildAnalysisRunner` — `essentia` is the supported value for BPM/Key/Energy.
- [x] CLI smoke test verifies graceful degrade with no native tools installed.
- [ ] Integration tests gated on `Category=Integration` trait — deferred until at least one native tool is installable in CI.

**Done:** All tests green across the solution. Live `raytagger scan` with analyzers configured runs cleanly even when no native binaries are present (each analyzer falls back to "tool not found → empty result"). Audio analysis is *enabled* at the architecture level; actual detection waits on the user installing Essentia and fpcalc per the README.

## Phase 3 — Mapping rule engine ✅

Goal: messy genres get consolidated into your taxonomy.

- [x] `RayTagger.Core/Mapping`: `MappingRuleSet` / `MappingRule` / `WhenClause` / `SetClause` / `GenrePattern` POCOs + `MappingRulesLoader` (delivered in Phase 1a).
- [x] `IMappingRuleEngine` / `MappingRuleEngine` evaluator covering the full predicate matrix from ARCHITECTURE.md §5.2: genre/subgenre literals + sentinels (`:missing:`/`:any:`), regex variants, BPM/energy ranges (skip on null), key (camelot or standard), key_mode, artist (literal + regex from existing `TrackTags.Artist`), path_glob (custom glob-to-regex translation), composition operators `any_of`/`all_of`/`not`, catch-all (empty or omitted `when`), and `on_match: stop` / `continue` with per-rule override.
- [x] `set` actions: genre/subgenre with empty-string-clears semantics, `tag.<name>` extra-tag flattening, `add_keyword` with `;` concatenation — all stamped with `TagFieldSource.Rules`.
- [x] Pipeline stage 5 (Map) wired into `TagPipeline.ProcessFileAsync`; `AppliedRules` populated on every `PipelineOutcome`.
- [x] CLI `raytagger explain <file>` verb — loads config + rules, reads existing tags, runs the engine and renders a Spectre table: existing values → matched rules → final values with source.
- [x] Predicate-matrix tests in `RayTagger.Core.Tests/Mapping/MappingRuleEngineTests` (23 tests covering every predicate class plus composition operators).

**Done:** 167 tests green. `raytagger scan` end-to-end runs through Map-stage cleanly; `raytagger explain <file>` provides a debugging view of which rules fire for a single file.

## Phase 4 — Online lookup (hybrid) ✅

Goal: `online_required: false` works — pipeline runs offline, but enriches genre/sub-genre from online sources when the network is up.

- [x] `RayTagger.Lookup`: `IMetadataProvider` interface + four implementations — `AcoustIdProvider` (fingerprint → MBID handshake into MusicBrainz), `MusicBrainzProvider` (keyless; MBID-anchored when AcoustID provided one, free-text otherwise; reads both `genres` and `tags`), `DiscogsProvider` (search → release `style` + `genre`; styles win for sub-genre slot), `LastFmProvider` (top tags with relative-count normalisation).
- [x] Per-provider rate limit via shared `Internal.RateLimiter` (SemaphoreSlim + Stopwatch with documented minimum intervals: MusicBrainz 1.1s, AcoustID 350ms, Discogs 1.1s, Last.fm 200ms).
- [x] Disk cache: `ILookupCache` + `FileLookupCache` (JSON files, atomic temp-rename writes, TTL enforced on read by `LastWriteTimeUtc`). Key derived via `LookupCacheKey.From(...)` — fingerprint hashed when present, otherwise normalised `artist|title|album` SHA256.
- [x] Provider chain orchestration in `LookupRunner` — runs providers in `lookup.providers` order, propagates AcoustID-discovered MBIDs into downstream queries, merges all `GenreCandidate`s into a ranked aggregate (highest confidence per value wins), and stamps cache. Fail-open contract: a provider that throws unexpectedly is isolated.
- [x] Pipeline stage 4 (Lookup) wired into `TagPipeline.ProcessFileAsync` between Analyze and Map. `TagMerger` seeds Genre/SubGenre from the highest-confidence candidate with source `TagFieldSource.Lookup`, respecting `existing_tags_policy`.
- [x] `RayTagger.Core.Pipeline` houses `ILookupRunner`, `LookupQuery`, and `NoopLookupRunner`, keeping Core free of Lookup-project references. Lookup's concrete implementations import them.
- [x] CLI smoke-tested: startup shows `✓ lookup via acoustid / musicbrainz / discogs / lastfm`; scan exits cleanly even when only stub keys are present.

**Note on AcoustID duration:** AcoustID's `lookup` endpoint requires a duration (integer seconds) alongside the Chromaprint fingerprint. `TrackTags` doesn't track duration yet — extending it via TagLib# is straightforward but is deferred until Essentia + fpcalc are installed locally so the path can be validated end-to-end. Until then `AcoustIdProvider.CanHandle` short-circuits and the chain falls through to MusicBrainz/Discogs/Last.fm.

**Done:** 184 tests green (Core 82, Metadata 36, Analysis 40, Lookup 17, Cli 9). New tests cover cache-key determinism, file-cache round-trip + TTL + corruption tolerance, runner ordering, MBID propagation, provider-failure isolation, and cache integration.

## Phase 5 — File sorting ✅

Goal: optionally relocate files into a clean library tree.

- [x] `SortTemplateEngine` — placeholder substitution from `ResolvedTrackTags` + `TrackTags`, `{name?}` optional segments collapse missing path components, reserved-char sanitisation (union of macOS/Linux/Windows-forbidden + trailing-dot/space trim).
- [x] `SortService` — `Rename` (append ` (2)`, ` (3)`, …), `Skip`, `Overwrite` conflict policies. `preserve_source: true` copies, false moves. `File.Move` in .NET 8+ already falls back to copy+delete across volumes.
- [x] Sidecar tag-backups (`*.tagger.bak.*.yaml`) follow the audio file when sorted — keeps `raytagger restore` correct after a sort run.
- [x] Pipeline stage 7 (Sort) wired into `TagPipeline.ProcessFileAsync` after Write, before returning the outcome. `PipelineOutcome.DestinationPath` populated.
- [x] `SidecarRestoreService` + CLI `raytagger restore <file>` — finds the most-recent sidecar, deserialises, writes the snapshot back via `TagFieldSource.Rules` (forced overwrite), removes the sidecar after success (unless `--keep`). `--dry-run` previews.
- [x] Test coverage: 21 new tests (`SortTemplateEngineTests` 9, `SortServiceTests` 10, plus 2 in existing pipeline tests covering disabled/empty cases) — every conflict policy, sidecar-carry, optional/required placeholder handling, sanitisation rules, dry-run paths.

**Done:** 205 tests green. End-to-end smoke against the 7-track fixture set: scan + write + sort moved every file into `{Electronic|Tech House}/{House|Driving|Peak Time}/{Artist} - {Title}.{ext}`, sidecars travelled along, `raytagger restore` recovered the pre-write tags. Originals untouched (write-test-protocol followed).

## Phase 6 — Avalonia UI

Goal: cross-platform desktop frontend on top of the same `RayTagger.Core` pipeline.

- [ ] `RayTagger.Ui` Avalonia project.
- [ ] Folder picker → live scan → results grid with diff (existing vs proposed tags).
- [ ] Rule-set editor with live preview ("if I add this rule, here's what changes").
- [ ] Apply/revert per row or batch.

## Phase 7 — Polish

- [ ] Watch mode (file-system events).
- [ ] AOT single-binary publish for macOS/Linux/Windows in CI.
- [ ] Persistent run history via `Raycoon.Serilog.Sinks.SQLite`.
- [ ] Docs: per-provider setup guides with screenshots.

---

## Not on the roadmap (and why)

- **Mixed In Key API integration** — no public API. Whether MIK-produced tags read cleanly depends on which frames the installed MIK version writes; verify on real fixtures before promising compatibility.
- **rekordbox / Serato library import** — proprietary DB formats; defer to a separate adapter package.
- **Custom ML genre classifier** — accuracy on a generic library is poor without per-collection training; not worth shipping a 200 MB model. Online metadata lookups solve the genre problem better in practice.
- **Web UI** — Avalonia covers the cross-platform desktop story; web adds hosting/auth concerns disproportionate to the value here.
