# Feld-Resolution: Wie ein Track zu seinen Tag-Werten kommt

Diese Datei ergänzt `ARCHITECTURE.md` (Design & Pipeline-Übersicht) und
`PLAN_GENRE_CLASSIFICATION.md` (Genre-Spezialfall) um die Sicht **„pro Feld":**
Welche Stationen ein Wert nimmt, welche Konfigurationsschalter ihn an welcher
Stelle beeinflussen und ob das Ergebnis tatsächlich in einen Tag-Frame landet.

Lies dieses Dokument, wenn du wissen willst, **„woher kommt mein BPM-Wert?"**
oder **„warum wurde mein Genre überschrieben?"**. Für das Gesamt-Pipeline-Bild
siehe `ARCHITECTURE.md §1`; für die Genre-Klassifikatoren im Detail siehe
`PLAN_GENRE_CLASSIFICATION.md`.

---

## 0. Pipeline-Stationen in Reihenfolge

Volle Beschreibung in `ARCHITECTURE.md §1`. Kurz pro Track:

1. **Discover** — Datei im Scan-Pfad gefunden, `TrackFile` mit `SizeBytes`/`LastModifiedUtc` erzeugt.
2. **Read existing tags** — `TagLib#` liest bestehende Tags inkl. `DurationSeconds` in `TrackTags`.
3. **Analyze** — Aktive Analyzer laufen parallel; BPM/Key/Energy aus einem einzigen Essentia-Fork, Fingerprint via `fpcalc`.
4. **Lookup** *(optional)* — AcoustID → MusicBrainz/Discogs/Last.fm Kette.
5. **GenreClassify** *(optional)* — Heuristik + bis zu 3 TF-Modelle ergänzen `LookupResult.GenreCandidates`.
6. **Merge** — `TagMerger` konsolidiert pro Feld nach `min_confidence` + `existing_confidence`; Genre/SubGenre läuft durch `TaxonomyGenreResolver`.
7. **ApplyMappingRules** — `mappings.yaml`-Regeln in deklarierter Reihenfolge; Source = `Rules`.
8. **Write** — Logische Felder → Format-Frames; atomares Replace + Sidecar-Backup.
9. **Sort** *(optional)* — Datei nach `sort.pattern` verschieben.
10. **BPM Snap (post)** — Final-Grid-Snap auf den aufgelösten BPM für Cache/Log-Konsistenz; löst keinen erneuten Write aus.

**Failure-Isolation:** Ein Fehler in einer Stage markiert den Track `Failed` und
die Pipeline läuft mit dem nächsten Track weiter (`ARCHITECTURE.md §1`).

---

## 1. Globale Entscheidungsknoten

Diese Schalter wirken **feldübergreifend** und sind die Top-Stellschrauben. Alle
Defaults sind in `samples/tagger.example.yaml` dokumentiert.

| Knoten | Wo | Effekt |
|---|---|---|
| `analysis.<dim>.existing_confidence` | `tagger.yaml` | Pro Dimension in `[0,1]`. `1.0` *(default)* = bestehender Tag gewinnt (klassisches skip-if-present). `0.0` = jeder usable Analyzer-Hit überschreibt (per-Dimension always-overwrite). Werte dazwischen = Analyzer muss eigene Confidence > diese Schwelle haben, um zu gewinnen. **`Rules`-Werte überschreiben immer** — bewusste Invariante. |
| `lookup.existing_confidence` | `tagger.yaml` | Selbe Semantik wie oben, aber für den Legacy-Lookup-Pfad (nur aktiv wenn `lookup.taxonomy_resolution: false`). Resolver-Pfad nutzt stattdessen taxonomy-membership-Schutz. |
| `analysis.<dim>.enabled` | `tagger.yaml` | Schaltet BPM/Key/Energy/Fingerprint einzeln aus. Aus = der jeweilige `*Result` ist leer und der Pfad „Analysis" fällt für dieses Feld weg. |
| `analysis.<dim>.min_confidence` | `tagger.yaml` | Schwelle in `[0,1]`. Werte unterhalb werden **vor** dem `existing_confidence`-Vergleich verworfen — der existierende Tag bleibt unangetastet. |
| `analysis.genre_classifier.*.enabled` | `tagger.yaml` | Schaltet die einzelnen Klassifikatoren (Heuristik + 3 TF-Modelle). Defaults: **alle aus** → byte-identisches Pipeline-Verhalten wie ohne Classifier. |
| `lookup.enabled` | `tagger.yaml` | Aus = keine Online-Provider-Anfragen im Scan. Der UI-„API"-Button pro Track **ignoriert** diesen Schalter (siehe CLAUDE.md). |
| `lookup.providers` | `tagger.yaml` | Liste in Prioritätsreihenfolge: erster Provider mit Treffer „gewinnt" für ein Feld; Genre-Kandidaten werden zu einer rangierten Liste fusioniert. |
| `lookup.taxonomy_resolution` | `tagger.yaml` | *(default `true`)* Genre/SubGenre laufen durch `TaxonomyGenreResolver`. Aus = „Top-1-Kandidat blind übernehmen". |
| `lookup.api_keys.*` | `.env` / `tagger.yaml` | Fehlender Key disabled den jeweiligen Provider lautlos beim Start. |
| `mapping.source_priority` | `tagger.yaml` | Reihenfolge der Klassifikator-/Provider-Tiers, die der Resolver bei Tie-Breaks verwendet. |
| `mappings.yaml` | separate Datei | Deklarative Regeln; Treffer setzt Feld mit `Source = Rules` → überschreibt **alle** vorherigen Quellen und ignoriert `existing_confidence`. |
| `write.tag_fields.<field>` | `tagger.yaml` | Override des Format-Frames pro logischem Feld (z. B. `SUBGENRE` → `STYLE`). |
| `write.backup` | `tagger.yaml` | `true` = Sidecar-YAML mit Zeitstempel vor jedem Write. |
| `dry_run` (CLI / UI) | Flag | Stages 8 und 9 emittieren nur „would write … / would move …", schreiben aber nichts. |
| `--force-overwrite` (CLI Flag) | `tagger scan` | Setzt alle `existing_confidence` für diesen einen Run auf `0` (BPM, Key, Energy, Lookup). Praktisch für komplettes Re-Tagging nach einem Tagger-Update, ohne `tagger.yaml` editieren zu müssen. `tagger validate` aktiviert dieses Verhalten intern fest. |

**Legacy-Migration:** Das alte `read.existing_tags_policy` wurde entfernt. Der Loader
strippt den Key aus dem YAML, übersetzt den Wert auf die per-Dim Defaults und gibt
eine Deprecation-Warning aus:
- `skip_if_present` / `fill_only_empty` → `existing_confidence: 1.0` *(default, nichts zu tun)*
- `always_overwrite` → `existing_confidence: 0.0` über alle Dimensionen

**Confidence-Reihenfolge wichtig:** `min_confidence` filtert **vor** dem
`existing_confidence`-Vergleich. Ein BPM-Wert mit Confidence `0.35` (unter dem
Default `0.4`) erreicht den Confidence-Vergleich gar nicht erst — der bestehende
Tag bleibt unverändert, unabhängig von `existing_confidence`.

---

## 2. Pro Feld: Quellenkette und Entscheidungsknoten

Schema pro Abschnitt:
- **Quellenkette** *(in Auswertungsreihenfolge)*
- **Aktiv wenn** *(welche Schalter den Pfad freigeben)*
- **Entscheidungsknoten** *(Konfig-Werte, die das Ergebnis ändern)*
- **Output-Frame** *(was wird tatsächlich geschrieben)*
- **Confidence-Schwelle / Defaults**

### 2.1 Genre

- **Quellenkette:**
  1. `mappings.yaml`-Regel mit `set: { genre: … }` *(höchste Priorität, `Source = Rules`)*
  2. `TaxonomyGenreResolver` über fusionierte Kandidatenliste:
     - Provider-Kandidaten aus `lookup.providers` *(in Reihenfolge)*
     - Klassifikator-Kandidaten aus aktiven `IGenreClassifier`s *(appended, nicht prepended)*
  3. Bestehender `TCON`/`GENRE`-Tag *(falls in Taxonomie und Policy es zulässt)*
  4. **Fallback:** Top-1 Roh-Kandidat aus Lookup *(nur wenn `existing_genre` leer)*
- **Aktiv wenn:** Track erreicht Merge-Stage. `lookup.enabled = false` & alle Classifier aus → es gibt nur Existing + Rules.
- **Entscheidungsknoten:**
  - `lookup.taxonomy_resolution` *(true → Whole-Word/Longest-Match-Suche in `taxonomy.yaml`; false → Top-1 blind)*
  - `lookup.providers` Reihenfolge
  - `analysis.genre_classifier.*.enabled` + jeweilige `min_confidence`
  - `analysis.genre_classifier.tensorflow.discogs_effnet.aggregate_*` *(Per-Parent-Summing; siehe `PLAN_GENRE_CLASSIFICATION.md §4.0c`)*
  - `lookup.existing_confidence` *(steuert, ob `Lookup`-Werte einen Existing-Tag überschreiben — Legacy-Pfad only, Resolver hat eigene Logik)*
  - `mapping.source_priority` *(Tie-Break bei gleichwertigen Kandidaten)*
- **Output-Frame:** `TCON` (MP3/AIFF) bzw. `GENRE` (FLAC).
- **Sichtbarmachung in der UI:** Genre-Wert, der nicht in `taxonomy.yaml` steht, wird dunkelblau hervorgehoben (`TaxonomyHighlightBrushConverter`).
- **Audit-Trail:** `ResolvedTrackTags.GenreLookupTrace` listet `CandidateTraceEntry` pro evaluiertem Kandidaten.

### 2.2 SubGenre

- **Quellenkette:**
  1. `mappings.yaml` `set: { subgenre: … }` *(`Source = Rules`)*
  2. `TaxonomyGenreResolver` Phase 2: nur ausgewertet, wenn Phase 1 ein Genre gefunden hat. Sucht in `taxonomy.subgenres[chosen_genre]`:
     - im Restwort der gematchten Genre-Kandidate (z. B. `"Tech House"` → Genre `House`, Subgenre-Suchraum: `"Tech"`)
     - in allen `SubGenreCandidate`s der Provider *(z. B. Discogs `style`)*
  3. Bestehender Subgenre-Tag *(falls in Taxonomie und Policy es zulässt)*
- **Kein Fallback:** Ohne erkanntes Genre wird **kein** SubGenre gesetzt — ein Subgenre ohne Genre-Anker ist bedeutungslos.
- **Aktiv wenn:** Genre wurde aufgelöst.
- **Entscheidungsknoten:** Wie Genre (alle), plus `taxonomy.yaml`-Pflege des Subgenre-Vokabulars.
- **Output-Frame:** `TXXX:SUBGENRE` (MP3/AIFF) bzw. `SUBGENRE` (FLAC). Override via `write.tag_fields.subgenre`.
- **Hinweis zu aggregiertem Discogs-EffNet:** Mit `aggregate_top_k: true` *(Default)* werden nur Eltern-Genres emittiert → Subgenre-Detection aus diesem Modell wird **unterdrückt**. Auf `false` setzen, um Roh-Subgenres zurückzubekommen *(Trade-off: parent-Vote schwächer)*.

### 2.3 BPM

- **Quellenkette:**
  1. `mappings.yaml`-Regel mit `set: { bpm: … }` *(falls vorhanden — selten verwendet, da BPM analytisch ist; `Source = Rules`)*
  2. `EssentiaBpmAnalyzer` aus dem geteilten Essentia-JSON: `rhythm.bpm`, Confidence aus `rhythm.bpm_histogram_first_peak_weight`
  3. Bestehender `TBPM`-Tag *(falls Policy/Confidence es zulässt)*
- **Tempo-Fold *(in `EssentiaBpmAnalyzer` nach Essentia-Output, vor Merge)*:**
  1. Bestehenden Genre-Tag lesen → über `taxonomy.yaml` normalisieren *(z. B. `"Tech House"` → `"House"`)*
  2. In `analysis.bpm.tempo_ranges_by_genre[normalized]` nachschlagen *(case-insensitive)*
  3. Kein Treffer → `analysis.bpm.tempo_range_fallback` *(default `null` → kein Fold, Raw passiert weiter)*
  4. Fold-Algorithmus:
     - `raw ∈ [min, max]` → `snap(raw)`
     - `raw < min` → `raw × 2`, dann snap; wenn jetzt in Range → akzeptiere
     - `raw > max` → `raw / 2`, dann snap; wenn jetzt in Range → akzeptiere
     - Immer noch out-of-range → `snap(raw)` zurück + `IsForcedFallback = true` → UI rendert die BPM-Zelle **dunkelblau**
- **Aktiv wenn:** `analysis.bpm.enabled = true`.
- **Entscheidungsknoten:**
  - `analysis.bpm.enabled`
  - `analysis.bpm.min_confidence` *(default `0.4` — Werte darunter werden verworfen, Existing bleibt)*
  - `analysis.bpm.tempo_ranges_by_genre` *(steuert Fold-Verhalten)*
  - `analysis.bpm.tempo_range_fallback`
  - `analysis.<dim>.existing_confidence`
- **Output-Frame:** `TBPM` (MP3/AIFF) bzw. `BPM` (FLAC). Standardframe per ID3-Spec — nicht via `write.tag_fields` umbenennbar.
- **Post-Write-Schritt:** Pipeline-Stufe 10 macht einen finalen Snap auf den aufgelösten Wert; relevant für Cache-/Log-Konsistenz, keine erneute Tag-Mutation.
- **DJ-Konventions-Beispiele** *(aus `ARCHITECTURE.md §3`)*:
  - 86 BPM DnB-Intro, Genre `Drum and Bass` `[130, 200]` → Fold `×2` → 172.
  - 154 BPM Dubstep, Genre `Dubstep` `[50, 100]` → Fold `÷2` → 77.

### 2.4 Key

- **Quellenkette:**
  1. `mappings.yaml`-Regel *(selten; `Source = Rules`)*
  2. `EssentiaKeyAnalyzer` aus geteiltem Essentia-JSON: `tonal.key_edma.{key,scale,strength}` → EDMA-Profil, Beatport-trainiert
  3. `KeyNotationConverter.FromEither(standard, camelot: null)` leitet **beide** Notationen aus dem Essentia-Output ab → `MusicalKey(Standard, Camelot)`
  4. Bestehender `TKEY`/`INITIALKEY`-Tag
- **Aktiv wenn:** `analysis.key.enabled = true`.
- **Entscheidungsknoten:**
  - `analysis.key.enabled`
  - `analysis.key.min_confidence` *(default `0.55`)*
  - `analysis.key.display_notation` *(steuert nur CLI-/Log-Anzeige; geschrieben werden **immer beide** Notationen, wenn Key-Analyse aktiv ist)*
  - `analysis.<dim>.existing_confidence`
- **Output-Frames (immer beide bei aktiver Analyse):**
  - **Standard-Notation** (`Am`, `F#m`): `TKEY` (MP3/AIFF) bzw. `INITIALKEY` (FLAC) — fix per ID3v2.4-Spec.
  - **Camelot-Notation** (`8A`, `5B`): `TXXX:CAMELOTKEY` (MP3/AIFF) bzw. `CAMELOTKEY` (FLAC) — Override via `write.tag_fields.camelot_key`.
- **Invariante:** Camelot **niemals** in `TKEY` — Player parsen `TKEY` als Roman-Numeral und korrumpieren stillschweigend.

### 2.5 Energy

- **Quellenkette:**
  1. `mappings.yaml`-Regel *(selten; `Source = Rules`)*
  2. `EssentiaEnergyAnalyzer` aus geteiltem Essentia-JSON: 5-Feature-Composite, gewichtete Summe:
     - `lowlevel.spectral_flux` × 0.35
     - `rhythm.beats_loudness` × 0.25
     - `rhythm.onset_rate` × 0.15
     - `rhythm.danceability` × 0.15
     - `lowlevel.average_loudness` × 0.10
  3. Bestehender `TXXX:ENERGYLEVEL`-Tag
- **Output-Mapping:** `Math.Clamp((int)Math.Round(1 + 9 * composite), 1, 10)` → Integer `1..10`.
- **Aktiv wenn:** `analysis.energy.enabled = true`.
- **Entscheidungsknoten:**
  - `analysis.energy.enabled`
  - `analysis.energy.min_confidence` *(default `0.5`)*
  - `analysis.energy.calibration_file` *(optionaler Pfad; leer/`""` → Built-in-Defaults)* — Per-Library-Kalibrierung wird via CLI `tagger calibrate-energy <folder>` oder UI „Energie kalibrieren…" erzeugt; modifiziert die Per-Feature-Ranges, die in die Composite-Normalisierung eingehen.
  - `analysis.<dim>.existing_confidence`
- **Output-Frame:** `TXXX:ENERGYLEVEL` (MP3/AIFF) bzw. `ENERGYLEVEL` (FLAC). Override via `write.tag_fields.energy`.

### 2.6 Mood — **Rule-gespeist (kein Audio-Analyzer)**

- **Quellenkette:**
  1. `mappings.yaml`-Regel mit `set: { mood: … }` *(`Source = Rules`)* — direkter Key, **nicht** `tag.mood`
  2. Bestehender Mood-Tag aus der Datei *(`TXXX:MOOD` / `MOOD`)*
- **Kein Analyzer, kein Lookup-Provider speist dieses Feld.** Das Feld ist im Domänenmodell (`TrackTags.Mood`, `ResolvedTrackTags.Mood`) und im `TagFieldMap` voll implementiert.
- **Aktive Sample-Konfiguration**: Die mitgelieferte `config/mappings.yaml` deckt Mood-Rules für **~16 Top-Level-Genres** ab (House, Techno, Trance, Drum and Bass, Indie Dance, Dubstep, Hip Hop, Ambient, Trip Hop, Downtempo, Jazz, Funk, Soul, Reggae, Gqom, Amapiano). Pop/Rock/R&B/Rap sind bewusst ausgelassen — zu breites Spektrum für eine sinnvolle Default-Mood. Welche Werte erlaubt sind, kontrolliert `taxonomy.moods` (mit `enforce: true` werden ungültige Werte beim Config-Load abgelehnt).
- **Aktiv wenn:** Die Datei hat bereits einen Mood-Tag **oder** eine Mapping-Regel setzt ihn explizit.
- **Entscheidungsknoten:**
  - `mappings.yaml` *(Regelkette + Specificity — `Rules` überschreibt Existing immer, unabhängig von `existing_confidence`)*
  - `taxonomy.moods` *(Whitelist bei `enforce: true`)*
  - `write.tag_fields.mood` *(Frame-Description-Override)*
- **Output-Frame:** `TXXX:MOOD` (MP3/AIFF) bzw. `MOOD` (FLAC).

### 2.7 Set Position — **Rule-gespeist (kein Audio-Analyzer)**

- **Hinweis:** Im Code heißt das Feld `SetPosition` (nicht „Set Time"). Es bezeichnet eine freie Set-Position-Annotation (z. B. „Warm-up", „Peak Time", „Late Peak") — keine zeitlich-numerische DJ-Set-Position.
- **Quellenkette:**
  1. `mappings.yaml`-Regel mit `set: { set_position: … }` *(`Source = Rules`)*
  2. Bestehender SetPosition-Tag aus der Datei *(`TXXX:SETPOSITION` / `SETPOSITION`)*
- **Kein Analyzer, kein Lookup-Provider speist dieses Feld.**
- **Aktive Sample-Konfiguration**: vier Rules ausschließlich basierend auf `energy: { min, max }` (Warm-up 1-3, Build-up 4-5, Peak Time 6-8, Late Peak 9-10). **Wenn Energy null/nicht analysiert ist, feuert keine SetPosition-Rule.** Erlaubte Werte aus `taxonomy.set_positions`: Warm-up, Build-up, Peak Time, Late Peak, Closing, Cool-down, After-hours.
- **Aktiv wenn:** Existing-Tag vorhanden **oder** Energy ist gefüllt und eine Energy-Range-Rule matched.
- **Entscheidungsknoten:** wie bei Mood, plus implizit alle Knoten der **§2.5 Energy** (denn ohne Energy keine SetPosition).
- **Output-Frame:** `TXXX:SETPOSITION` (MP3/AIFF) bzw. `SETPOSITION` (FLAC).

### 2.8 Länge / Dauer (`DurationSeconds`) — **Read-only, intern**

- **Quelle:** `TagLib# Properties.Duration` beim Read-Schritt (Stage 2). Wird in `TrackTags.DurationSeconds` abgelegt.
- **Wird nicht in einen Tag-Frame geschrieben.** Container-Metadaten (`mpeg`/`flac`/`aiff`-Header) liefern die Dauer beim nächsten Read automatisch — eine redundante Tag-Speicherung wäre eine Inkonsistenzquelle.
- **Interner Verwendungszweck:**
  - **AcoustID-Lookup-Query:** Der AcoustID-Endpoint **erfordert** `duration`. Ohne Dauer fällt der Provider-Chain auf MusicBrainz-Freitextsuche durch (statt MBID-anchored Lookup). Siehe `ARCHITECTURE.md §4`.
  - UI-Anzeige in der Ergebnis-Tabelle.

### 2.9 Dateigröße (`SizeBytes`) — **Read-only, intern**

- **Quelle:** `FileInfo.Length` in `FileDiscoveryService` beim Discover-Schritt (Stage 1).
- **Wird nicht in einen Tag-Frame geschrieben.** Reines Filesystem-Metadatum.
- **Interner Verwendungszweck:**
  - UI-Anzeige (`TrackOutcomeViewModel`).
  - Bestandteil des Cache-Identitäts-Triples `(path, size, mtime)` *(`ARCHITECTURE.md §2`)*.

### 2.10 Chromaprint-Fingerprint — **Intern, kein Tag-Frame**

- **Quelle:** `ChromaprintFingerprintAnalyzer` shellt `fpcalc` aus (Stage 3).
- **Aktiv wenn:** `analysis.fingerprint.enabled = true` *(Default, erforderlich für AcoustID)*.
- **Verwendung:** Geht in den AcoustID-Lookup als Hauptidentifier. Nicht in die Datei zurückgeschrieben.
- **Confidence:** Binär (vorhanden oder nicht); `min_confidence` Default `0.0`.

---

## 3. Standard-Metadaten (Title, Artist, Album, AlbumArtist, Year)

- **Quellenkette:**
  1. `mappings.yaml`-Regel *(selten — Mapping fokussiert auf Genre/SubGenre, aber `set: { tag.title: … }` funktioniert)*
  2. Bestehender Tag in der Datei *(`TIT2`/`TITLE` etc.)*
  3. Lookup-Provider *(MusicBrainz/Discogs liefern Recording-/Release-Tags; aktuell nicht in `TagMerger` integriert — Provider-Werte fließen nur in Genre/SubGenre-Kandidaten ein)*
- **Effektiv heute:** Diese Felder sind **Passthrough** wie Mood/SetPosition. Der Lookup-Pfad sieht sie zwar in `MetadataResult`, aber `TagMerger` schreibt sie nicht in `ResolvedTrackTags`. Wer Title/Artist via Lookup überschreiben will, braucht aktuell eine explizite `set:`-Regel.
- **Output-Frames:** Siehe `ARCHITECTURE.md §6.1`.

---

## 4. Pfadvarianten je nach Aktivierung

Vier praktische Szenarien — jeweils mit Pipeline-Verhalten und Output.

### 4.1 Setup A — Minimal („nur Lesen und Backup", alles aus)

```yaml
analysis:    { bpm: { enabled: false }, key: { enabled: false },
               energy: { enabled: false }, fingerprint: { enabled: false } }
lookup:      { enabled: false }
analysis.genre_classifier:  { heuristic: { enabled: false }, tensorflow: { ... all false ... } }
```

- **Pipeline:** Discover → Read → Merge *(passthrough)* → ApplyRules → Write *(nur falls Regel-Treffer)*.
- **Output:** Identisch zu Input, außer eine Mapping-Regel feuert.
- **Sinnvoll für:** Trockenes Erkunden, Regel-Entwicklung, Backup-Erzeugung.

### 4.2 Setup B — Default („nur lokale Analyse")

```yaml
analysis:    { bpm/key/energy/fingerprint alle enabled: true }
lookup:      { enabled: false }
analysis.genre_classifier: alle false
```

- **Pipeline:** Discover → Read → Analyze *(Essentia-Fork + fpcalc)* → Merge → ApplyRules → Write → Sort.
- **Output:** BPM/Key/Energy aus Audio, Genre/SubGenre/Mood/SetPosition **passthrough**.
- **Sinnvoll für:** Offline-Workflow, schneller Run, vorhandene Genre-Tags vertrauen.

### 4.3 Setup C — Voll („Analyse + Online + Klassifikator")

```yaml
analysis:                   alle enabled: true
lookup:                     { enabled: true, taxonomy_resolution: true,
                              providers: [acoustid, musicbrainz, discogs, lastfm] }
analysis.genre_classifier:  { heuristic: { enabled: true },
                              tensorflow.discogs_effnet: { enabled: true, aggregate_top_k: true } }
```

- **Pipeline:** Alle 10 Stages aktiv.
- **Performance-Hinweis:** TF-Modelle kosten ~1.5 s/Track/Modell auf Apple Silicon — CLI-Banner surfacet kumulative Schätzung beim Start *(siehe CLAUDE.md „Subprocess cost")*.
- **Genre-Resolution:** Provider-Kandidaten zuerst, Klassifikator-Kandidaten danach in der Liste. Resolver „stoppt bei erstem Taxonomy-Match" → Klassifikator-Stimmen retten nur Tracks, bei denen Provider keinen Taxonomy-Treffer hatten.
- **Sinnvoll für:** Erst-Inventarisierung, lückenhafte Genre-Tags füllen.

### 4.4 Setup D — Restore

```bash
tagger restore <pfad>
```

- **Pipeline:** Liest Sidecar-Backup `<file>.tagger.bak.<timestamp>.yaml`, stellt Original-Tag-Block wieder her. Keine Analyze/Lookup/Merge-Stages.
- **Voraussetzung:** Vorheriger Run mit `write.backup: true`.

---

## 5. Merge-Policy — Pro Feld kompakt

Wiederholung der Matrix aus `ARCHITECTURE.md §6.2` mit Feld-Perspektive:

| Feld | `skip_if_present` | `fill_only_empty` | `always_overwrite` |
|---|---|---|---|
| Genre | Existing bleibt, falls vorhanden; sonst Resolver-Wert | wie skip_if_present | Resolver-Wert immer |
| SubGenre | wie Genre | wie Genre | Resolver-Wert immer |
| BPM | Existing bleibt; sonst Analyse-Wert *(falls ≥ `min_confidence`)* | wie skip_if_present | Analyse-Wert immer |
| Key | wie BPM | wie BPM | wie BPM |
| Energy | wie BPM | wie BPM | wie BPM |
| Mood | Existing bleibt *(keine andere Quelle)* | wie skip_if_present | Existing bleibt |
| SetPosition | wie Mood | wie Mood | wie Mood |
| **Alle Felder** | **`Source = Rules` überschreibt immer** | **dito** | **dito** |

**Reihenfolge der Filter (top → bottom):**

```
Analyzer/Lookup-Output
     │
     ▼
[min_confidence Filter]   ← unterhalb fällt der Wert hier raus, Existing bleibt
     │
     ▼
[existing_confidence]     ← Analyzer-Confidence vs. existing_confidence-Floor
     │
     ▼
[Mapping-Regeln]          ← Rules-Treffer überschreiben das Ergebnis
     │
     ▼
ResolvedTrackTags → Writer
```

---

## 6. Backup & Reversibilität

- `write.backup: true` *(empfohlen)* schreibt vor jedem Write einen Sidecar `<file>.tagger.bak.<timestamp>.yaml` mit dem vollen Tag-Block.
- Atomic-Replace: TagLib# schreibt nach `<file>.tagger.tmp`, dann `File.Move` mit Overwrite → POSIX `rename(2)` / Windows `MoveFileEx(MOVEFILE_REPLACE_EXISTING)`. Reader sehen entweder alt **oder** neu, nie torn.
- File-Lock-Pre-Flight: Öffnen mit `FileShare.None`. Wenn ein anderer Prozess (DJ-Software, Indexer) die Datei hält, scheitert der Writer mit `MetadataException` als per-Track `Failed` *(statt Mid-Save-Crash)*.
- `tagger restore <pfad>` rollt zurück.

---

## 7. Was **nicht** in einem Tag-Frame landet

Auch wenn die Felder im Domänenmodell existieren:

| Feld | Wo gelesen | Wo verwendet | Warum nicht geschrieben |
|---|---|---|---|
| `DurationSeconds` | TagLib# `Properties.Duration` | AcoustID-Query, UI | Container-Header liefern's beim nächsten Read; redundant + Inkonsistenzrisiko |
| `SizeBytes` | `FileInfo.Length` | UI, Cache-Key | Filesystem-Metadatum, kein Tag-Konzept |
| `Chromaprint` | `fpcalc`-Output | AcoustID-Query | Identifier, kein Metadatum |
| `AcoustIdMbid` | AcoustID-Response | MusicBrainz-Folgequery | Wandert in MB-Lookup, MBID landet nicht standardmäßig im Tag *(könnte via `set: { tag.musicbrainz_recordingid: … }` ergänzt werden)* |

---

## 8. Verweise

- `ARCHITECTURE.md` — Pipeline-Design, Domänenmodell, Schreib-Policy, Trade-offs
- `PLAN_GENRE_CLASSIFICATION.md` — Genre-Klassifikator-Tiers, Aggregation, Label-Normalisierung
- `CLAUDE.md` *(Repo-Root)* — Konventionen, Native-Tool-Boundaries, aktuelle Implementierungs-Caveats
- `samples/tagger.example.yaml` — alle Konfig-Schlüssel mit Defaults und Kommentaren
- `samples/mappings.example.yaml` — Beispiel-Regelblock mit `when`/`set`-Syntax
