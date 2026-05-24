# RayTagger

Cross-platform .NET 10 command-line tool for automatic tagging of music files (MP3, FLAC, AIFF).

Tagger walks a directory, runs each track through an analysis pipeline (BPM, key, energy, audio fingerprint), enriches metadata from online sources (AcoustID → MusicBrainz / Discogs / Last.fm) plus optional audio-based genre classifiers (heuristic + 3 TensorFlow models), applies your **configurable mapping rules** to consolidate messy genre strings into a clean taxonomy, writes the resulting tags, and optionally sorts files into a structured library directory.

> **Status: working application.** All pipeline stages, analyzers, lookup providers, the Avalonia UI, energy calibration, taxonomy-aware genre resolution, native-tool auto-bootstrap and audio-based genre classification are implemented. Open work items (MIK fixtures, watch-mode, etc.) live in [`docs/ROADMAP.md`](docs/ROADMAP.md).

## Features

- **Multi-format tagging** — MP3 (ID3v2.4), FLAC (Vorbis comments), AIFF (ID3v2.4 chunk).
- **Audio analysis** — BPM, musical key (Camelot + standard notation written together), 1-10 energy level, Chromaprint fingerprint. One Essentia fork per track feeds BPM/Key/Energy.
- **Hybrid online lookup** — works fully offline; when the network is up, it enriches genre/sub-genre from AcoustID + MusicBrainz / Discogs / Last.fm with a taxonomy-aware resolver.
- **Audio-based genre classification** *(opt-in)* — pure-rule heuristic (8 parent genres) plus 3 TensorFlow models (5-class electronic, 87-class Jamendo, 400-class Discogs styles with per-parent aggregation). All default-off → byte-identical behaviour vs. classifier-less build until you flip a switch.
- **Per-genre BPM tempo fold** — DJ-convention half/double-time correction driven by `analysis.bpm.tempo_ranges_by_genre`. Out-of-range tempo flagged in the UI dark-blue.
- **Mapping rule engine** — YAML-defined rules with conditions (genre patterns, BPM ranges, keys, energy bands, mood, set-position, regex, path-glob, any_of/all_of/not). Specificity-based "best match wins" overwrite policy.
- **Mood + Set-Position axes** — written as `TXXX:MOOD` / `TXXX:SETPOSITION` (round-trip with Traktor/Beatport).
- **Avalonia desktop UI** — folder-pick → live scan with per-row apply, rule-editor with live preview, AppliedRulesDialog with provider + resolver trace, energy-calibration wizard.
- **Optional auto-sort** — move files into `{genre}/{subgenre?}/{artist} - {title}.{ext}` (template configurable).
- **DJ-tool-friendly frames** — writes `TXXX:CAMELOTKEY` / `TXXX:ENERGYLEVEL` / `TXXX:MOOD` and the standard `TKEY` / `TBPM` / `TCON` frames recognised by Traktor, Beatport, Virtual DJ, rekordbox export and similar tools. (Mixed-In-Key fixture round-trip is a planned verification — see ROADMAP Phase 7.)
- **Native-tool auto-bootstrap** — `raytagger setup` downloads Essentia + fpcalc (SHA-256 pinned) into the local cache; subsequent scans are offline-clean.
- **Dry-run mode** + timestamped sidecar tag backups + atomic temp-rename writes + `raytagger restore` for rollback.

## Requirements

- .NET 10 SDK
- Native analysis binaries on `PATH` (or use `raytagger setup` to auto-download into the user cache — see [`docs/INSTALL.md`](docs/INSTALL.md) for the full manifest, manual fallback per OS, and known issues).

```bash
# Quickest path: let Tagger fetch the binaries
raytagger setup

# Or install manually (macOS shown)
brew install chromaprint                    # fpcalc — fingerprinting for AcoustID
brew install MTG/essentia/essentia          # BPM, key (EDMA profile), energy
                                            # (AGPL — shelled out, not linked)
```

**Optional — TensorFlow genre classifiers.** If you enable any
`analysis.genre_classifier.tensorflow.*` model in `tagger.yaml`:

```bash
pip install essentia-tensorflow      # Python 3.9–3.12; pulls TensorFlow in
```

Tagger invokes the helper script under `tools/raytagger-genre-classifier/` as a
subprocess. The `.pb` model files are downloaded into the user cache on first
use by the same bootstrap that handles Essentia/fpcalc — SHA-256 pinned.

Tagger uses Essentia's `streaming_extractor_music` for **all three** signal-derived dimensions (BPM, key, energy). One fork+exec per track yields the full JSON output, which the analyzers share via an in-process cache — no triple-cost from running three separate tools. Essentia's EDMA key profile is trained on Beatport material and outperforms libKeyFinder's Shaath profile on DJ-oriented tracks; its rhythm tracker exposes a real confidence value where aubio gave none.

**License note on Essentia.** Essentia is AGPL-3.0. Tagger never links against it — it shells out to the `essentia_streaming_extractor_music` binary via `Process`. This process boundary keeps Tagger Apache-2.0 compatible. If you bundle Essentia binaries in your own distribution, the AGPL still applies to that distribution.

## Quick start

```bash
# Build
dotnet build RayTagger.slnx

# Copy and edit configuration
cp samples/tagger.example.yaml   ./tagger.yaml
cp samples/mappings.example.yaml ./mappings.yaml
cp samples/.env.example          ./.env       # add your API keys

# Dry-run against a music folder
dotnet run --project src/RayTagger.Cli -- scan --config ./tagger.yaml --dry-run

# Apply tags
dotnet run --project src/RayTagger.Cli -- scan --config ./tagger.yaml
```

Produce a standalone binary:

```bash
dotnet publish src/RayTagger.Cli -c Release -r osx-arm64 --self-contained
# binary at src/RayTagger.Cli/bin/Release/net10.0/osx-arm64/publish/tagger
```

## Repository layout

```
src/
├── RayTagger.Core/        Domain, abstractions, pipeline, mapping engine,
│                          taxonomy resolver, config.
├── RayTagger.Metadata/    TagLib# wrappers; tag-frame mapping per format.
├── RayTagger.Analysis/    Audio analysis adapters (BPM/Key/Energy/Fingerprint)
│                          + genre classifiers (heuristic + 3 TF models).
├── RayTagger.Lookup/      Online providers (AcoustID/MusicBrainz/Discogs/Last.fm).
├── RayTagger.Hosting/     DI composition, PipelineFactory, LookupRunnerBuilder —
│                          shared by CLI and UI.
├── RayTagger.Ui/          Avalonia desktop app.
└── RayTagger.Cli/         CLI executable.

tests/                          xUnit + FluentAssertions + NSubstitute.
samples/                        Example configuration files.
tools/raytagger-genre-classifier/  Python helper for TF genre classifiers.
docs/                           Architecture, install, roadmap, field resolution.
config/                         Active runtime config (tagger.yaml, mappings.yaml,
                                taxonomy.yaml). Gitignored.
```

## Documentation

- [`CLAUDE.md`](CLAUDE.md) — orientation for Claude Code (and humans new to the repo).
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — pipeline diagram, mapping DSL spec, tag-frame matrix, design decisions.
- [`docs/FIELD_RESOLUTION.md`](docs/FIELD_RESOLUTION.md) — per-field „wie kommt der Wert zustande" (Quellenkette, Entscheidungsknoten, Output-Frame).
- [`docs/PLAN_GENRE_CLASSIFICATION.md`](docs/PLAN_GENRE_CLASSIFICATION.md) — Genre-Klassifikator-Design (Heuristik + TF-Modelle + Aggregation).
- [`docs/INSTALL.md`](docs/INSTALL.md) — native-tool install (Essentia, fpcalc, optional TensorFlow) per OS.
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — phased implementation plan + offene Arbeiten.

## License

Apache-2.0. See `LICENSE`.

Copyright © 2025 RAYCOON.com GmbH.
