# RayTagger — Konsolidierter Verbesserungsplan

> **Stand**: 2026-05-25, post Backtest v9 (Commit `ba21a53`).
> Frühere Pläne in `~/.claude/plans/` sind durch dieses Dokument abgelöst.
> Aufwand/Nutzen/Risiko-Schätzungen sind grobe Orientierung — Phasen-Reihenfolge
> ist datengetrieben aus dem Backtest gegen `./music/Tagged/` (1795 Tracks,
> MIK-getagged) und `./music/Tagged_VDJ/` (Virtual DJ 2026, zweite Truth).

---

## 1. Baseline (Backtest v9, OR-Match)

1795 Tracks gegen `./music/Tagged/` (MIK) + `./music/Tagged_VDJ/` (VDJ).
Genre/SubGenre-Truth aus Subfolder-Pfad (vom User händisch gepflegt).
BPM/Key-Truth aus MIK-Comment + VDJ-TBPM/TKEY (OR-Match).

| Dimension | Exact % | Tol % | Headroom | Misses absolut |
|---|---:|---:|---:|---:|
| Genre | **99,2** | 99,2 | 0,8 pp | 15 |
| SubGenre | **72,7** | 72,7 | 27,3 pp | 24 (von n=88 evaluable) |
| BPM | **97,5** | 98,1 | 2,5 pp | 34 |
| **Key** | **40,5** | 62,1 | **59,5 pp** | **679** |
| Energy | 25,2 | 63,5 | (out-of-scope, siehe Sprint Q) | 654 |

VDJ-OR-Match-Befund: VDJ rettet exakt **1 BPM-Track** und **0 Key-Tracks** —
die zwei Truth-Quellen sind nach Camelot-Fold praktisch identisch
(0 echte Key-Diffs, 23 musikalische Beat-Grid-Diffs bei BPM).

**Folgerung**: Key ist die einzige Dimension mit zweistelliger Verbesserungs-Reserve.
Alle anderen sind über 97 % oder strukturell limitiert (SubGenre: zu wenig Truth).

---

## 2. Priorisierungs-Konvention

| Symbol | Aufwand | | Symbol | Nutzen | | Symbol | Risiko |
|---|---|---|---|---|---|---|---|
| **XS** | <0,5 d | | **★★★** | hoch (>5 pp) | | **🟢** | niedrig |
| **S** | 0,5–1 d | | **★★** | mittel (1–5 pp) | | **🟡** | mittel |
| **M** | 2–3 d | | **★** | niedrig | | **🔴** | hoch |
| **L** | 5+ d | | | | | | |

---

## 3. Sprint K — Key-Erkennung (Top-Priorität)

**Ziel**: 40,5 % → 60 %+ exact, 62,1 % → 75 %+ mit Camelot-Toleranz.
679 Miss-Tracks sind als Optimierungs-Datenbasis verfügbar.

| # | Maßnahme | Aufwand | Nutzen | Risiko |
|---|---|---|---|---|
| **K1** | **Per-Genre Key-Profile-Wahl**. Heuristik: EDM (House/Techno/Trance) → EDMA, Tonal-Material (HipHop/TripHop/Downtempo/Reggae) → Temperley, Rest → Ensemble. Pro Track die genre-passende Profil-Schätzung als Primary. Genre kommt aus Lookup/Classifier ODER Subfolder-Pfad (CLI-Backtest-Hint). | S | ★★★ | 🟢 |
| **K2** | **Confidence-gewichtetes Ensemble** statt Majority-Vote. Jedes Profil emittiert `(Key, Strength)`; Best-Key wird via `Σ weight × match`. Tiebreak: Profil mit höchster Strength. | S | ★★ | 🟡 |
| **K3** | **Backtest-Slice "Key-Misses per Genre" + "Per-Profile Win-Rate"** — neuer Report-Block analog zur bestehenden `BPM Distribution by Genre`. Liefert die empirische Datenbasis für K1 (welches Profil schneidet bei welchem Genre am besten ab). | XS | ★★★ (enabler) | 🟢 |
| **K4** | **Externer Key-Detector** als 2. Source (KeyFinder als CLI, oder mixxx-key wenn verfügbar). Multi-Source-OR-Match analog zum VDJ-Truth-Pattern aus Commit `ba21a53`. | M | ★★ | 🟡 |
| **K5** | **Camelot-Distance-Toleranz konfigurierbar** machen (aktuell hardcoded ≤2 in `BacktestMetrics.IsCamelotNeighbour`). Per CLI / config schaltbar zwischen 0 / 1 / 2 für strenge Validation vs DJ-praktisch. | XS | ★ | 🟢 |
| **K6** | **Key-Conflict-Warning in UI**: wenn EDMA/Temperley/Krumhansl untereinander auseinanderlaufen, Track im UI markieren (dunkelblauer Border analog BPM-Fallback). User-driven manual review als Backstop. | S | ★ | 🟢 |

**Reihenfolge**: K3 (Daten) → K1 (Per-Genre Profil) → K2 (Ensemble) → K4 (externer Detector) → K5/K6 (Polish).

**Critical files**: `src/RayTagger.Analysis/EssentiaKeyAnalyzer.cs`,
`src/RayTagger.Analysis/Internal/EssentiaJsonParser.cs`,
`src/RayTagger.Core/Configuration/TaggerOptions.cs` (KeyAnalyzerOptions),
`src/RayTagger.Cli/Commands/ValidateHandler.cs` (Report-Section für K3).

---

## 4. Sprint G — Genre / SubGenre

**Ziel**: Letzte 15 Genre-Misses adressieren + SubGenre-Truth-Basis erweitern.

| # | Maßnahme | Aufwand | Nutzen | Risiko |
|---|---|---|---|---|
| **G1** | **SubGenre-Truth via SUBGENRE-Frame**. Cross-Check (`/tmp/bpm_sub_compare.md`) zeigt **1136 Tracks** haben `SUBGENRE`-Tag (vs. nur n=88 evaluable über Sub-Subfolder-Pfad). Truth aus dem Tag-Frame ziehen statt nur aus Pfad → ~13× mehr Evaluable, deutlich bessere statistische Aussagekraft für SubGenre-Tuning. | S | ★★★ | 🟢 |
| **G2** | **HipHop-Misses** (8 Tracks: 3× Pop, 3× Downtempo, 1× Trip Hop, 1× Funk — alle 80s-Electro-Funk-Pioniere: Whodini, Grandmaster Flash, Newcleus). Fix-Optionen: (a) explizite Mapping-Rule `artist:["Whodini","Grandmaster Flash","Newcleus",…] → genre:"Hip Hop"`, (b) per-artist override in Discogs-Provider (z. B. höhere Confidence für artist-style-tag). | XS | ★ | 🟢 |
| **G3** | **Per-Provider min_confidence** (alter Task #29). Data: MusicBrainz hat 92,2 % Win-Rate (schwächster Provider), Discogs 98,3 %, TF-DiscogsEffnet 99,8 %. Vorschlag: `musicbrainz.min_confidence: 0.50`, Rest auf default belassen. | S | ★ | 🟢 |
| **G4** | **TF aggregated-fallback Confidence-Penalty**. 78 Tracks fielen in `:aggregated-fallback` (diffuse output, kein Parent klärte `AggregateMinTotal`). Sollten geringer gewichtet werden — aktuell zählen sie voll wie aggregated-Treffer. | XS | ★ | 🟡 |
| **G5** | **Sub-Subfolder-Hierarchie erweitern (User-Aufwand)**. Tagged/ hat aktuell nur `HipHop/OldSchool/` und `Techno/Classic/` als Sub-Sub-Folder. Weitere Sub-Sub-Folder anlegen (House/Tech, House/Deep, Techno/Melodic, etc.) würde SubGenre-Truth-Korpus zusätzlich erweitern (komplementär zu G1). | M (User) | ★★★ (für SubGenre) | 🟢 |

**Critical files**: `src/RayTagger.Core/Validation/BacktestTruthExtractor.cs` (G1),
`config/mappings.yaml` (G2), `config/tagger.yaml` (G3),
`src/RayTagger.Analysis/Genre/TensorflowGenreClassifier.cs` (G4).

---

## 5. Sprint B — BPM

**Ziel**: 97,5 % → 99 %. Restmenge: 23 musikalische Beat-Grid-Diffs + 10 fehlende Predictions.

| # | Maßnahme | Aufwand | Nutzen | Risiko |
|---|---|---|---|---|
| **B1** | **3:2 / 4:3 Beat-Grid-Diffs akzeptieren als musikalisch korrekt**. Backtest zeigt 8× 3:2 und 7× 4:3 Ratios in den 23 echten BPM-Diffs (Triplet vs Even-Beat, 4-Beat vs 3-Beat-Grid). Würde Triplet-Erkennung in Essentia benötigen → out-of-scope. **Aktion**: als bekannte Limitation in `docs/ARCHITECTURE.md §6.2` dokumentieren. | XS | — | 🟢 |
| **B2** | **Reggae-Range nachschärfen**: 84-129 statt 84-135 (im `config/tagger.yaml` bereits angepasst). `samples/tagger.example.yaml` synchron halten. | XS | ★ | 🟢 |
| **B3** | **BPM-Half-Time-Toleranz auf 2 BPM erhöhen** (aktuell ±1). Reggae-Tracks "148 vs 73.34" fallen knapp raus (\|Δ\|=1.32). Toleranz 2 BPM für half/double-Path, 1 BPM für exact — fängt 1 weiteren BPM-Match. | XS | ★ | 🟢 |
| **B4** | **VDJ als BPM-Truth-Override für Sondergenres**. Daten zeigen: VDJ rescuet exakt 1 Track. Kein praktischer Hebel. Eintrag: keine Aktion. | — | — | — |

**Critical files**: `src/RayTagger.Core/Validation/BacktestMetrics.cs` (B3),
`samples/tagger.example.yaml`, `config/tagger.yaml`.

---

## 6. Sprint P — Performance

**Ziel**: Backtest-Laufzeit reduzieren (aktuell ~25–35 min pro Voll-Lauf mit 3 TF-Modellen aktiviert).

| # | Maßnahme | Aufwand | Nutzen | Risiko |
|---|---|---|---|---|
| **P1** | **Python-Daemon-Mode für TF-Classifier**. Per-Track-Kosten ~1,5 s/Modell → ~200–500 ms inference-only = **3-8× speedup**. Process-Pool mit stdin/stdout-Protokoll. Crash-Recovery + Failover zu One-Shot-Modus zwingend. Tracked in `docs/PLAN_GENRE_CLASSIFICATION.md §5.7 #1`. | L | ★★★ | 🟡 |
| **P2** | **TF-Batch-Mode**. Simpler als Daemon: `--audio-list manifest.txt` → eine JSON-Zeile pro Track. **Trade-off**: bricht das Streaming-Channel-Pipeline-Pattern (alle Pfade müssten vorher gesammelt werden → verschlechtert UX bei langen Scans). | M | ★★ | 🟡 |
| **P3** | **TF-Klassifier parallel pro Track ausführen**. Aktuell läuft `GenreClassifierRunner` alle Klassifier sequentiell pro Track. Parallel pro Track ergäbe per-Track-Cost = max(modelle) statt sum(modelle) → ~3× speedup bei 3 aktiven Modellen. | S | ★★ | 🟢 |
| **P4** | **Cross-Model File-Dedup** (PLAN §5.7 #4). `discogs-effnet-bs64-1.pb` (18 MB) wird 3× heruntergeladen. 54 MB Disk savings, niedriger Hebel. Würde content-addressed store + symlinks brauchen (per-OS link semantics). | S | ★ | 🟢 |
| **P5** | **Lookup-Cache-Versionierung**. Cache-Key + Adapter-Version-Hash. Verhindert stale Cache-Entries nach Provider-Adapter-Änderung. | M | ★ | 🟢 |

**Reihenfolge**: P3 (quick win, vor Daemon) → P1 (großer Hebel, längere Implementierung) → P4/P5.

**Critical files**: `src/RayTagger.Analysis/Genre/GenreClassifierRunner.cs` (P3),
`src/RayTagger.Analysis/Genre/TensorflowGenreClassifier.cs` + `tools/raytagger-genre-classifier/` (P1/P2),
`src/RayTagger.Lookup/Caching/FileLookupCache.cs` (P5).

---

## 7. Sprint Q — Quality / Hardening / Roadmap-Restposten

| # | Maßnahme | Aufwand | Nutzen | Risiko |
|---|---|---|---|---|
| **Q1** | **Backtest-Sanity-Check** — Warnung wenn `truth.bpm = null` (oder `truth.camelotKey = null`) für >10 % der Tracks. Hätte den MIK-Comment-Parser-Bug von 2026-05-24 sofort gefangen, statt 1411 Tracks-Mismatch-Maskierung. Konkret: Im `ValidateHandler.RenderSummary` einen "Truth Coverage"-Block ergänzen. | XS | ★★ | 🟢 |
| **Q2** | **MIK / DJ-tool round-trip fixtures** (ROADMAP Phase 7). Verifiziert `TXXX:CAMELOTKEY` / `TXXX:ENERGYLEVEL` / `TXXX:MOOD`-Frames mit echtem MIK + VDJ Install. | M | ★★ | 🟡 |
| **Q3** | **Linux/Windows native-tool packaging** in `native-tools.yaml`. SHA-256-pinned Manifest-Entries für die fehlenden Plattformen. | S | ★ | 🟢 |
| **Q4** | **Watch mode** — file-system events, automatischer Rescan auf neue Files. | M | ★ | 🟢 |
| **Q5** | **AOT single-binary publish** für mac/linux/win im CI. | M | ★ | 🟡 |
| **Q6** | **UI-Settings-Panel** für live-reloading `tagger.yaml` / `mappings.yaml`. | M | ★ | 🟢 |
| **Q7** | **Persistent run history** via `Raycoon.Serilog.Sinks.SQLite` (sink wired, persistent schema TBD). | M | ★ | 🟢 |
| **Q8** | **Resolver-Strategy "best-of-all"** (alter Task #19 aus den Sprint-1-5-Plänen). Per Flag aktivierbar. Würde aktuelle "stop-at-first-match"-Logik in `TaxonomyGenreResolver` aufweichen. AB-Test im Backtest. | M | ★★ | 🟡 |
| **Q9** | **Energy-Recalibration**. Aktueller Stand: 25,2 % exact, 63,5 % mit ±1-Toleranz. Vorherige Calibration scheiterte weil Tagged/ enge Spannweite hat (Flux 0.08-0.13 vs default 0.05-0.15 → Bucket-Clamping). Optionen: (a) Calibration auf größere/diversere Library, (b) als out-of-scope akzeptieren. | M | ★ | 🟡 |

**Critical files**: `src/RayTagger.Cli/Commands/ValidateHandler.cs` (Q1),
`tests/RayTagger.Metadata.Tests/fixtures/` (Q2),
`samples/native-tools.example.yaml` (Q3),
`src/RayTagger.Core/Mapping/TaxonomyGenreResolver.cs` (Q8).

---

## 8. Empfohlene Sprint-Reihenfolge

| Sprint | Fokus | Erwartete Verbesserung |
|---|---|---|
| **1** | **K3 + K1 + K2** (Key-Daten-Slice → Per-Genre-Profil-Wahl → Confidence-Ensemble) | Key 40,5 % → 55–60 %+ exact |
| **2** | **G1 + G3 + G4** (SubGenre-Truth-Expansion + MB-min_confidence + TF-fallback-penalty) | SubGenre evaluable n=88 → ~1100; Genre +0,3-0,5 pp |
| **3** | **P3 + P1** (TF parallelism + Daemon-Mode) | Backtest-Laufzeit ~30 min → ~5 min |
| **4** | **K4 + Q1 + Q2** (externer Key-Detector + Sanity-Check + MIK-Fixtures) | Toolchain-Robustheit, Confidence in Truth-Quelle |
| **5** | **Restposten** (Q3-Q9 nach ROI) | Plattform-Coverage + UX-Polish |

---

## 9. Out-of-Scope-Entscheidungen (dokumentiert)

| Item | Begründung |
|---|---|
| **Mixed In Key API integration** | Kein public API. Verifikation über Frame-Round-Trip-Fixtures (Q2) ist der gangbare Weg. |
| **rekordbox / Serato library import** | Proprietäre DB-Formate; separates Adapter-Package wäre angemessen. |
| **Audio-based mood + set-position analyzers** | Felder sind im Domain-Modell + Writer; Mood-from-Audio-Heuristik ist schwach, `set_position` hat per-file kein Signal. |
| **Web UI** | Avalonia deckt cross-platform Desktop ab; Web würde Hosting/Auth-Komplexität bringen die der Nutzen nicht rechtfertigt. |
| **3:2 / 4:3 BPM Beat-Grid-Diffs (15 Tracks)** | Musikalisch korrekt — Triplet/Polymeter-Detection würde Essentia-Erweiterung benötigen. |
| **Energy >25 % Exact-Match** | Aktuell out-of-scope laut Plan-Charta; Q9 hält die Tür offen. |

---

## 10. Verweise

- **Aktueller Backtest-Output**: `/tmp/backtest-v9.json` / `/tmp/backtest-v9.md` (lokal, nicht im Repo).
- **Tagged-Library-Cross-Check**: `/tmp/key_compare.md`, `/tmp/bpm_sub_compare.md`.
- **Architektur**: `docs/ARCHITECTURE.md`.
- **Genre-Classifier-Design**: `docs/PLAN_GENRE_CLASSIFICATION.md` (insb. §5.7 für deferred TF-Optimierungen).
- **ROADMAP**: `docs/ROADMAP.md` (Phasen 0-6.5 abgeschlossen; Phase 7 = "Polish", siehe Sprint Q).
- **Backtest-Harness**: `src/RayTagger.Cli/Commands/ValidateHandler.cs` + `src/RayTagger.Core/Validation/`.
