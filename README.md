# RayTagger

Cross-platform .NET 10 command-line tool for automatic tagging of music files (MP3, FLAC, AIFF).

Tagger walks a directory, runs each track through an analysis pipeline (BPM, key, energy, audio fingerprint), enriches metadata from online sources (AcoustID → MusicBrainz / Discogs / Last.fm), applies your **configurable mapping rules** to consolidate messy genre strings into a clean taxonomy, writes the resulting tags, and optionally sorts files into a structured library directory.

> **Status:** scaffold phase. Solution skeleton + architecture are in place; the audio pipeline is not yet implemented. See [`docs/ROADMAP.md`](docs/ROADMAP.md).

## Features (target scope)

- **Multi-format tagging** — MP3 (ID3v2.4), FLAC (Vorbis comments), AIFF (ID3v2.4 chunk).
- **Audio analysis** — BPM, musical key (Camelot or standard notation), 1-10 energy level, Chromaprint fingerprint. Each dimension is a pluggable strategy; pick the analyzer you trust per dimension.
- **Hybrid online lookup** — works fully offline; when the network is up, it enriches genre/sub-genre from AcoustID + MusicBrainz / Discogs / Last.fm.
- **Mapping rule engine** — YAML-defined rules with conditions (genre patterns, BPM ranges, keys, energy bands). Reorder/refine your library's genre taxonomy without touching code.
- **Optional auto-sort** — move files into `{genre}/{subgenre?}/{artist} - {title}.{ext}` (template configurable).
- **DJ-tool-friendly frames** — writes `TXXX:CAMELOTKEY` / `TXXX:ENERGYLEVEL` and the standard `TKEY` / `BPM` frames recognised by Traktor, Beatport, rekordbox export and similar tools. (Mixed-In-Key compatibility is a planned verification, not a current claim.)
- **Dry-run mode** + tag backups before any write.

## Requirements

- .NET 10 SDK
- Native analysis binaries on `PATH`. Tagger probes them at startup and gracefully disables the affected dimension if one is missing.

```bash
# macOS
brew install chromaprint                    # fpcalc — fingerprinting for AcoustID
brew install MTG/essentia/essentia          # BPM, key (EDMA profile), energy
                                            # (AGPL — shelled out, not linked)

# Linux (Debian/Ubuntu)
sudo apt install libchromaprint-tools
# Essentia: build from source — see https://essentia.upf.edu/installing.html

# Windows
scoop install chromaprint
# Essentia: best-effort, build from source
```

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
├── RayTagger.Core/        Domain, abstractions, pipeline, mapping engine, config.
├── RayTagger.Metadata/    TagLib# wrappers; tag-frame mapping per format.
├── RayTagger.Analysis/    Audio analysis adapters (BPM/Key/Energy/Fingerprint).
├── RayTagger.Lookup/      Online providers (AcoustID/MusicBrainz/Discogs/Last.fm).
└── RayTagger.Cli/         CLI executable.

tests/                          xUnit + FluentAssertions + NSubstitute.
samples/                        Example configuration files.
docs/                           Architecture and roadmap.
```

## Documentation

- [`CLAUDE.md`](CLAUDE.md) — orientation for Claude Code (and humans new to the repo).
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — pipeline diagram, mapping DSL spec, tag-frame matrix, design decisions.
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — phased implementation plan.

## License

Apache-2.0. See `LICENSE`.

Copyright © 2025 RAYCOON.com GmbH.
