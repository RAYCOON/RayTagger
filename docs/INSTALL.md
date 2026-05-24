# Native Tools installieren

RayTagger orchestriert nur die Pipeline — die eigentliche Audio-Analyse läuft in zwei externen Binaries:

| Binary                                  | Wofür                                              | Quelle                |
|-----------------------------------------|----------------------------------------------------|-----------------------|
| `essentia_streaming_extractor_music`    | BPM, Tonart (EDMA-Profil), spektrale Energie       | Essentia (AGPL-3.0)   |
| `fpcalc`                                | Chromaprint-Fingerprint für AcoustID-Lookup        | Chromaprint (LGPL)    |

> **Lizenz-Hinweis.** Essentia ist AGPL-3.0. Tagger ruft das Extractor-Binary als **Subprozess** auf und linkt nicht gegen die Essentia-C++-Bibliotheken — die Prozessgrenze hält Tagger Apache-2.0-kompatibel. Tagger liefert die Binaries auch nicht mit, sondern lädt sie zur Laufzeit direkt von der jeweiligen Original-Quelle.

---

## Quickstart — `raytagger setup`

In den meisten Fällen brauchst du nichts manuell zu installieren. Tagger bringt einen Auto-Bootstrap mit:

```bash
raytagger setup
```

Was passiert dabei:

1. Tagger lädt das Manifest `native-tools.yaml` (Pfad konfigurierbar in `tagger.yaml`, Default: neben `tagger.yaml` oder neben der ausführbaren Datei).
2. Für jedes darin gelistete Tool prüft Tagger zuerst, ob es schon im `PATH` liegt.
3. Wenn nicht: Download der für dein OS + CPU passenden Binary von der im Manifest hinterlegten URL, SHA-256-Verifikation, Entpacken, Ablage im OS-typischen Cache-Verzeichnis:
   - macOS  `~/Library/Application Support/RayTagger/tools/<tool>/<version>/<rid>/`
   - Linux  `~/.local/share/RayTagger/tools/<tool>/<version>/<rid>/`
   - Windows  `%LOCALAPPDATA%\RayTagger\tools\<tool>\<version>\<rid>\`
4. Beim nächsten `raytagger scan` ist alles offline verfügbar.

Bei einem regulären `raytagger scan` läuft derselbe Resolver implizit: PATH → Cache → Download. Du musst `setup` also nicht explizit aufrufen — es ist nur die saubere Vorbereitung für Offline-Maschinen oder CI-Pipelines, wo der erste Scan keine Internet-Verzögerung haben darf.

### `--force` und sicheres Update

```bash
raytagger setup --force
```

Löscht die gecachten Binaries vor dem Download neu. Sinnvoll nach Manifest-Updates oder bei Verdacht auf einen korrupten Cache.

### Wenn der Auto-Bootstrap nicht passt

Tagger weigert sich, ein Binary zu installieren, dessen SHA-256 nicht zum Manifest passt. Wenn Upstream den Build erneuert, muss das Manifest gepflegt werden — anderenfalls bricht Tagger mit einem klaren Fehler ab. In dem Fall (oder wenn du grundsätzlich keinen Auto-Download willst) gibt es zwei Wege:

- `auto_bootstrap: false` in `tagger.yaml` setzen — Tagger nutzt dann **ausschließlich** den `PATH`.
- Manifest auf einen internen Mirror umbiegen (`manifest_file: /pfad/zu/eigenem.yaml`) — z. B. wenn dein Unternehmen die Binaries selber spiegelt.

In beiden Fällen ist die manuelle Installation unten der Backup-Pfad.

---

## Manuelle Installation (Fallback)

## macOS

Stand Mai 2026: Die offizielle Homebrew-Formel `MTG/essentia/essentia` ist **kaputt** (siehe „Bekannte Probleme" unten). Empfohlen ist der manuelle Quell-Build — dauert ~5 Minuten und liefert ein sauberes Binary unter `/opt/homebrew/bin/`.

### Voraussetzungen

```bash
xcode-select --install                    # Apple Command Line Tools
brew install eigen libyaml fftw ffmpeg libsamplerate libtag chromaprint pkg-config
brew install python                       # waf braucht python3
```

Geprüft mit:
- `ffmpeg` 8.x  (NICHT `ffmpeg@2.8` — siehe unten)
- `eigen` 5.x
- `libsamplerate` 0.2.x
- `libtag` 2.x
- `chromaprint` 1.6.x

### Build aus Quellcode

```bash
git clone https://github.com/MTG/essentia.git
cd essentia

# Wichtig: modernes FFmpeg + C++17 erzwingen
export PKG_CONFIG_PATH=/opt/homebrew/opt/ffmpeg/lib/pkgconfig:$PKG_CONFIG_PATH

python3 waf configure \
    --mode=release \
    --with-examples \
    --std=c++17 \
    --prefix=/opt/homebrew

python3 waf
python3 waf install
```

### Verifikation

```bash
which essentia_streaming_extractor_music
# → /opt/homebrew/bin/essentia_streaming_extractor_music

essentia_streaming_extractor_music 2>&1 | head -5
# → "built with Essentia version 7e90d20" (oder neuer)
```

Kurzer Smoke-Test mit einer eigenen Datei:

```bash
essentia_streaming_extractor_music meinetrack.mp3 /tmp/out.json
grep -E '"bpm"|"key_key"|"key_scale"' /tmp/out.json | head
```

### Bekannte Probleme (macOS)

| Symptom beim Build / `brew install`                                            | Ursache                                                                                                                                   | Lösung                                                                                  |
|--------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------|
| `brew install MTG/essentia/essentia` → `Error: No available formula bottle`    | Die Formel ist HEAD-only, kein vorgebautes Bottle.                                                                                        | Manueller Source-Build (oben) oder `--HEAD` (siehe Folge-Issue).                        |
| `brew install MTG/essentia/essentia --HEAD` → Compile-Errors in `audiocontext.cpp` (`ch_layout`, `codecpar` undefiniert) | Formel zieht das veraltete `ffmpeg@2.8`, aktueller Essentia-Source benutzt aber die FFmpeg-7-API.                                          | Manueller Build mit `PKG_CONFIG_PATH=/opt/homebrew/opt/ffmpeg/lib/pkgconfig` (oben).    |
| Build-Error: `Eigen ... requires C++14 or higher`                              | Eigen 5.x erzwingt mind. C++14, Default-Toolchain pickt aber C++11.                                                                       | `--std=c++17` an `waf configure` übergeben (oben).                                      |
| `python3 waf configure` → `Could not find pkg-config`                          | `pkg-config` fehlt.                                                                                                                       | `brew install pkg-config`.                                                              |
| `waf` → `No module named 'waflib'`                                             | Beschädigter Checkout oder falsche Python-Version.                                                                                        | Frischer `git clone`, `python3 --version` ≥ 3.9 prüfen.                                 |

### Aufräumen, falls ein gescheiterter Brew-Versuch herumliegt

```bash
brew uninstall --force essentia       # entfernt unvollständige Installation
brew untap MTG/essentia               # optional, falls die Formel nie mehr gebraucht wird
```

---

## Linux

### Debian / Ubuntu (22.04, 24.04, 26.04)

System-Pakete für die Abhängigkeiten:

```bash
sudo apt-get update
sudo apt-get install -y \
    build-essential pkg-config \
    libeigen3-dev libyaml-dev libfftw3-dev \
    libavcodec-dev libavformat-dev libavutil-dev libswresample-dev \
    libsamplerate0-dev libtag1-dev libchromaprint-dev \
    python3 python3-dev
```

Build:

```bash
git clone https://github.com/MTG/essentia.git
cd essentia
python3 waf configure --mode=release --with-examples
python3 waf
sudo python3 waf install
sudo ldconfig                         # damit der dynamische Loader das neue libessentia.so findet
```

Das Binary landet in `/usr/local/bin/essentia_streaming_extractor_music`.

### Arch Linux

```bash
sudo pacman -S --needed eigen libyaml fftw ffmpeg libsamplerate taglib chromaprint pkgconf base-devel python
```

Danach derselbe Build-Block wie für Debian/Ubuntu. Es gibt zusätzlich ein inoffizielles AUR-Paket `essentia-git`, das den Quell-Build automatisiert:

```bash
yay -S essentia-git
```

### Fedora / RHEL

```bash
sudo dnf install -y \
    eigen3-devel libyaml-devel fftw-devel \
    ffmpeg-devel libsamplerate-devel taglib-devel chromaprint-devel \
    pkgconf-pkg-config gcc-c++ python3 python3-devel
```

Hinweis: `ffmpeg-devel` benötigt unter Fedora die RPMFusion-Repos. Falls nicht eingerichtet:

```bash
sudo dnf install -y https://download1.rpmfusion.org/free/fedora/rpmfusion-free-release-$(rpm -E %fedora).noarch.rpm
```

Anschließend Build wie bei Debian/Ubuntu.

### Verifikation (alle Distributionen)

```bash
essentia_streaming_extractor_music --help 2>&1 | head -5
ldd $(which essentia_streaming_extractor_music) | head
```

### Bekannte Probleme (Linux)

| Symptom                                                                        | Ursache                                                                       | Lösung                                                                                     |
|--------------------------------------------------------------------------------|-------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------|
| `ImportError: libessentia.so: cannot open shared object file`                  | `/usr/local/lib` nicht im Loader-Pfad.                                        | `sudo ldconfig` ausführen oder `LD_LIBRARY_PATH=/usr/local/lib` exportieren.               |
| Ältere Ubuntu-Versionen (≤ 20.04): `libav*`-Pakete zu alt für aktuellen Essentia-Source | Repo-FFmpeg < 5.x.                                                            | Entweder `master`-Release verwenden, das den älteren API-Stand unterstützt, oder FFmpeg aus Source bauen. |
| `pip install essentia` schlägt mit Wheel-Fehler fehl                           | Auf glibc-Distributionen abseits Ubuntu/Debian sind die manylinux-Wheels mismatched. | Aus Quellcode bauen (oben). Das Python-Binding wird für Tagger ohnehin nicht gebraucht.    |

---

## Windows

Tagger braucht auf Windows **nur das `essentia_streaming_extractor_music.exe`-Binary** — es ist nicht nötig, die ganze Essentia-Library zu kompilieren. Es gibt zwei realistische Pfade:

### Option A — Vorgebautes statisches Binary (empfohlen)

Das MTG-Team veröffentlicht statische Extractor-Binaries unter
<http://essentia.upf.edu/documentation/extractors/>.

1. Den Win64-Build von `essentia_streaming_extractor_music` herunterladen.
2. Die `.exe` in ein Verzeichnis kopieren, das im `PATH` liegt — z. B.:
   ```powershell
   $dst = "$env:LOCALAPPDATA\Programs\Essentia"
   New-Item -ItemType Directory -Force $dst | Out-Null
   Move-Item .\essentia_streaming_extractor_music.exe $dst\
   [Environment]::SetEnvironmentVariable("Path", $env:Path + ";$dst", "User")
   ```
3. Neue PowerShell-Sitzung öffnen (damit `PATH` neu eingelesen wird) und verifizieren:
   ```powershell
   essentia_streaming_extractor_music.exe 2>&1 | Select-Object -First 5
   ```

### Option B — WSL2 + Linux-Anleitung

Für Power-User, die ohnehin schon WSL benutzen:

```powershell
wsl --install -d Ubuntu-24.04
```

Danach in der WSL-Shell der **Debian/Ubuntu**-Anleitung folgen. Tagger selbst läuft als .NET-Anwendung unter Windows; das WSL-Binary wird durch eine kleine Wrapper-`.bat` aufrufbar, oder du startest Tagger gleich aus WSL heraus.

> Aktuelle Erfahrung (Mai 2026): Beim Aufruf aus Windows-Pfaden über `wsl essentia_streaming_extractor_music ...` müssen Eingabe- und Ausgabepfade in WSL-Notation (`/mnt/c/...`) übersetzt werden. Tagger erledigt das nicht automatisch — Option A ist deshalb pragmatischer.

### Option C — Cross-Compile / MSVC-Build

Theoretisch möglich (Essentia liefert Skripte unter `packaging/win32/`), aber der NSIS-Installer dort wurde zuletzt mit NSIS 2.44 gegen MSVC 2005 SP1 getestet. Praktisch nicht mehr brauchbar — Option A.

### Bekannte Probleme (Windows)

| Symptom                                                                        | Ursache                                                                       | Lösung                                                                                     |
|--------------------------------------------------------------------------------|-------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------|
| `'essentia_streaming_extractor_music' is not recognized`                       | `PATH`-Eintrag noch nicht in der aktuellen Sitzung sichtbar.                  | Neue PowerShell/CMD-Sitzung öffnen oder Rechner neu starten.                               |
| `VCRUNTIME140.dll wurde nicht gefunden`                                        | Visual-C++-Redistributable fehlt.                                             | [Microsoft VC++ Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe) installieren. |
| Antivirus blockiert das Binary                                                 | Statisch gelinkter Extractor wird gelegentlich falsch positiv erkannt.        | Datei freigeben oder Tagger-Verzeichnis whitelisten.                                       |

---

## Andere native Tools

### `fpcalc` (Chromaprint)

Wird für den AcoustID-Lookup gebraucht.

| OS              | Befehl                                                                                  |
|-----------------|-----------------------------------------------------------------------------------------|
| macOS           | `brew install chromaprint`                                                              |
| Debian/Ubuntu   | `sudo apt-get install -y libchromaprint-tools`                                          |
| Arch            | `sudo pacman -S chromaprint`                                                            |
| Fedora          | `sudo dnf install -y chromaprint-tools`                                                 |
| Windows         | Statisch gelinktes `fpcalc.exe` von <https://acoustid.org/chromaprint> herunterladen und in `PATH` kopieren. |

Verifikation:

```bash
fpcalc -version
# → fpcalc version 1.6.x
```

---

## Optional: TensorFlow-Genre-Klassifikatoren

Tagger bringt drei optionale, audio-basierte Genre-Klassifikatoren mit
(`genre_electronic`, `mtg_jamendo`, `discogs_effnet`). Sie sind in
`tagger.yaml` per Default **alle aus** — du musst sie explizit aktivieren
und dafür die Python-Laufzeit von Essentia + TensorFlow installieren.

Wenn du nur die DSP-Heuristik (`analysis.genre_classifier.heuristic`) nutzt,
brauchst du **nichts** hier auf der Seite — die Heuristik läuft im selben
Essentia-Subprozess wie BPM/Key/Energy.

### Voraussetzungen

```bash
# Python 3.9 – 3.12 (TensorFlow-Wheels gibt es nur für diese Range)
python3 --version

# essentia-tensorflow zieht TensorFlow als transitive Dependency
pip install essentia-tensorflow
```

> Apple Silicon: `essentia-tensorflow` liefert kein arm64-Wheel direkt,
> sondern verlangt das `tensorflow-macos` + `tensorflow-metal` Setup. Bei
> Problemen einen frischen venv aufsetzen:
> `python3.11 -m venv .venv && source .venv/bin/activate && pip install tensorflow-macos tensorflow-metal essentia-tensorflow`.

### Helper-Script

Der eigentliche Klassifikator läuft als Subprozess über ein mitgeliefertes
Python-Script:

```
tools/raytagger-genre-classifier/
├── classify.py          # Einstiegspunkt — wird von Tagger aufgerufen
├── remap/               # Per-Model-Label-Remap (z.B. "Drum n Bass" → "Drum and Bass")
└── dev/
    └── analyze_remap_coverage.py   # Coverage-Tool für Taxonomy-Pflege
```

Tagger findet das Script relativ zur ausführbaren Datei oder via
`analysis.genre_classifier.tensorflow.python_helper_path` in `tagger.yaml`.

### Modell-Bootstrap

Die `.pb`-Modelldateien werden beim ersten Aktivieren via
`raytagger setup` (bzw. automatisch beim ersten `scan`) aus dem
Manifest `native-tools.yaml` (Sektion `models:`) gezogen — SHA-256-pinned
und atomar in den User-Cache promotet:

- macOS  `~/Library/Application Support/RayTagger/models/<model-key>/`
- Linux  `~/.local/share/RayTagger/models/<model-key>/`
- Windows  `%LOCALAPPDATA%\RayTagger\models\<model-key>\`

### Aktivierung in `tagger.yaml`

```yaml
analysis:
  genre_classifier:
    heuristic:
      enabled: true                  # läuft ohne Python — kein extra Setup
      min_confidence: 0.55
    tensorflow:
      genre_electronic:
        enabled: true                # nur einschalten wenn pip-Setup oben sitzt
        min_confidence: 0.65
      mtg_jamendo:
        enabled: false               # 87-class Jamendo
        min_confidence: 0.50
      discogs_effnet:
        enabled: true                # 400-class, mit Per-Parent-Aggregation
        min_confidence: 0.50
        aggregate_top_k: true        # default an — sonst sehr viele Long-Tail-Kandidaten
```

### Verifikation

Beim Start zeigt Tagger pro aktivem Klassifikator eine Probe-Zeile:

```
INFO  Heuristic genre classifier verfügbar
INFO  TensorFlow genre_electronic verfügbar    (python3.11, tensorflow 2.15)
INFO  TensorFlow discogs_effnet verfügbar      (model SHA matched, 400 classes)
```

Fehlt Python oder ein Modul, wird der betroffene Klassifikator stillschweigend
deaktiviert. `raytagger scan --verbose` zeigt den Grund.

### Subprozess-Kosten (relevant für große Bibliotheken)

Auf Apple Silicon kostet ein TF-Modell ca. **1.5 s pro Track**. Bei 1000 Tracks
und allen drei Modellen aktiv: ~75 Minuten allein für die Klassifikatoren —
zusätzlich zu BPM/Key/Energy + Online-Lookup. Der CLI-Startup-Banner surfacet
die kumulative Schätzung beim Scan-Start. Optimierungen (Daemon-Mode,
Batch-Mode, File-Dedup) sind in `docs/PLAN_GENRE_CLASSIFICATION.md §5.7` als
deferred work dokumentiert.

---

## Nach der Installation

Tagger probt beim Start, ob die Binaries gefunden werden, und gibt eine Status-Zeile pro Tool aus. Schnellcheck:

```bash
dotnet run --project src/RayTagger.Cli -- scan --dry-run ./music
```

Erwartete Log-Zeilen:

```
INFO  Essentia-Extractor verfügbar    → /opt/homebrew/bin/essentia_streaming_extractor_music
INFO  Chromaprint (fpcalc) verfügbar  → /opt/homebrew/bin/fpcalc
```

Fehlt eines davon, wird die zugehörige Analyse stillschweigend übersprungen — `raytagger scan --verbose` zeigt warum.
