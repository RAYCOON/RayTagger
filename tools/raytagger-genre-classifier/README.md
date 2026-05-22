# raytagger-genre-classifier

Subprocess bridge between RayTagger (.NET) and Essentia's pre-trained TensorFlow
genre classification models. Invoked by `TensorflowGenreClassifier` (see
`src/RayTagger.Analysis/Genre/TensorflowGenreClassifier.cs`) — not intended for
direct human use, but documented here so the install path is reproducible.

See `docs/PLAN_GENRE_CLASSIFICATION.md` §4 for the design rationale.

## Install

```bash
python3 -m pip install -r requirements.txt
```

`essentia-tensorflow` ships pre-built wheels for cp39–cp312 on macOS and Linux
(no source build needed). Pulls in NumPy and TensorFlow as transitive deps —
roughly 280 MB total. On Apple Silicon the wheel runs on the system TF; on Linux
it's CPU-only unless you install the GPU TF variant separately.

## CLI contract (consumed by .NET)

```bash
python raytagger_genre_classifier.py \
    --model <electronic|jamendo|discogs-effnet> \
    --audio /abs/path/track.mp3 \
    --models-dir /abs/path/RayTagger/models \
    --top-k 5
```

stdout (one line of JSON):

```json
{"model": "electronic", "predictions": [
    {"label": "house",  "probability": 0.78},
    {"label": "techno", "probability": 0.12},
    {"label": "trance", "probability": 0.05}
]}
```

stderr is for diagnostics only — empty on success.

### Exit codes

| Code | Meaning | .NET side reaction |
| ---: | --- | --- |
| 0 | Success | parse stdout |
| 1 | Generic failure | log warning, disable this classifier for the scan |
| 2 | Model file missing | attempt download via the native-tools bootstrapper, then retry |
| 3 | Audio file unreadable | log warning, skip this track |

## Models

| Model key | Pipeline | Output | Use case |
| --- | --- | --- | --- |
| `electronic` | discogs-effnet embedding → genre-electronic head | ~6 classes | sanity-check / second opinion on the Phase A heuristic |
| `jamendo` | discogs-effnet embedding → mtg-jamendo head | 87 classes | Rock/Pop/R&B/Soul/Jazz/Funk/Classical — genres the heuristic skips |
| `discogs-effnet` | direct (single-stage classifier) | 400 classes | fine-grained sub-genres (Tech House, Liquid DnB, Melodic Techno) → `SubGenreCandidates` |

Each model expects its files in `<models-dir>/<model-key>/`:

```
models/
  electronic/
    model.pb                 (or genre_electronic-discogs-effnet-1.pb — see registry)
    labels.json
  jamendo/
    ...
```

Filenames are pinned in `MODEL_REGISTRY` inside the script. The .NET bootstrap
downloads them from `essentia.upf.edu/models/...`.

## Label remap (`remap/<model>.json`)

The taxonomy resolver in .NET matches whole words via case-insensitive regex.
Model labels like `drum_n_bass` would emit `drum n bass` after the .NET
normaliser — which doesn't match the taxonomy entry `Drum and Bass` because
the word `and` is missing.

Each remap file is a JSON dict mapping `<model-label>` → `<canonical taxonomy phrase>`.
Hand-curated, ships in this directory, applied **before** the predictions reach
.NET. Labels not in the remap pass through unchanged.

## Manual smoke test

```bash
python raytagger_genre_classifier.py \
    --model electronic \
    --audio /path/to/test.mp3 \
    --models-dir ~/Library/Application\ Support/RayTagger/models \
    --top-k 5
```

If the models aren't downloaded yet, you'll get exit code 2 and a stderr message
naming the missing file. The bootstrap path will populate them on first scan.

## Dev tooling

`dev/analyze_remap_coverage.py` simulates the full Python-remap → .NET-normaliser
→ .NET-resolver pipeline against every label of every model and prints a
per-model coverage report. Run it when:

- adding a new TF model (verify its labels against the existing taxonomy + remap),
- editing `./music/taxonomy.yaml` (some labels that resolved before may now miss),
- editing any `remap/*.json` (confirm no regressions).

```bash
# One-time: fetch model metadata.json files
mkdir -p /tmp/raytagger-remap-analysis/{electronic,jamendo,discogs-effnet}
# (download URLs from docs/PLAN_GENRE_CLASSIFICATION.md §4.3)

python3 tools/raytagger-genre-classifier/dev/analyze_remap_coverage.py
```

The script prints three buckets per model: `MATCH`, `NEAR-MISS` (candidate for
remap), `OOV` (out of vocab, correctly dropped).

## Troubleshooting

- **`essentia-tensorflow is not installed`** — `pip install -r requirements.txt`. Confirm with `python -c "import essentia.standard"`.
- **`Model file missing: …`** — let .NET handle the download. To do it manually, fetch the .pb + labels.json from `essentia.upf.edu/models/` and place them in `<models-dir>/<model-key>/`.
- **`Audio unreadable`** — Essentia's `MonoLoader` supports MP3/FLAC/AIFF/OGG/WAV. Symlinks resolve fine; permission errors or codec-less files don't.
- **Unexpected prediction shape** — model file and labels file are out of sync (different model versions). Re-download both.
