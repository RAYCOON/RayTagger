#!/usr/bin/env python3
"""
raytagger-genre-classifier — bridge between RayTagger (.NET) and Essentia's
pre-trained TensorFlow genre classification models.

Invoked as a subprocess. Outputs exactly one line of JSON to stdout on success.
Diagnostics go to stderr (empty on the happy path). See `docs/PLAN_GENRE_CLASSIFICATION.md`
§4 for the integration design.

CLI contract
------------

    python raytagger_genre_classifier.py \\
        --model <electronic|jamendo|discogs-effnet> \\
        --audio <abs-path-to-audio> \\
        --models-dir <abs-path-to-models-directory> \\
        --top-k 5

stdout (one line, parsed by the C# wrapper):

    {"model": "electronic", "predictions": [
        {"label": "house",  "probability": 0.78},
        {"label": "techno", "probability": 0.12},
        ...
    ]}

Exit codes (consumed by `TensorflowGenreClassifier` in .NET):
  0  Success — predictions on stdout.
  1  Generic error — stderr explains.
  2  Model file missing — tells the C# wrapper to attempt a download via the
     native-tools bootstrapper, then retry.
  3  Audio file unreadable.

Label normalisation
-------------------

Two stages, in order:

1. Per-model remap from `remap/<model>.json` (ships in the repo next to this script).
   Resolves model-specific abbreviations and symbol-substitutions that the §5.1a
   normaliser can't handle (`drum_n_bass` → `Drum and Bass`, `r&b` → `R&B`).
2. The §5.1a normaliser runs in .NET on the C# side — we ship raw remapped labels.

The remap is hand-curated for each model based on its training-label vocabulary.
Labels not in the remap pass through unchanged.
"""

import argparse
import json
import os
import sys
import traceback
from pathlib import Path


EXIT_OK = 0
EXIT_GENERIC = 1
EXIT_MODEL_MISSING = 2
EXIT_AUDIO_UNREADABLE = 3


# Quiet TF + Essentia info-level logging on stderr by default. Set RAYTAGGER_DEBUG=1
# to restore verbose output (useful when diagnosing graph-loading or audio-decoding
# issues). Must happen BEFORE importing tensorflow/essentia — the TF env var is
# consumed at C++ library init time.
_DEBUG = os.environ.get("RAYTAGGER_DEBUG") == "1"
if not _DEBUG:
    os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "3")
    # Suppress Apple-Silicon ABI fork warning (TensorFlow on macOS forks for thread
    # pool init; we never re-fork from inside a worker).
    os.environ.setdefault("TF_DISABLE_MKL", "0")

SCRIPT_DIR = Path(__file__).resolve().parent

# Essentia's published genre models. Each entry has:
#   labels_filename   — Essentia's rich metadata.json renamed by the bootstrapper. Contains
#                       both the label list AND the model's input/output node schema; we read
#                       node names from the schema rather than hardcoding Essentia conventions.
#   sample_rate       — Essentia's pre-trained models all expect 16 kHz mono.
#   pipeline          — "two-stage" (embedding model → classification head) or
#                       "single-stage" (the .pb file is itself the classifier).
#   embedding         — for two-stage: the embedding extractor's .pb name and the graph node
#                       that returns the embedding layer (not the class layer). The discogs-effnet
#                       embedding output is `PartitionedCall:1` per Essentia's published schema —
#                       hardcoded because the embedding model is downstream of the head and we
#                       don't ship its metadata.json separately.
#   head              — the classification model's .pb name. For single-stage this IS the
#                       classifier; for two-stage it consumes embeddings. Input/output node
#                       names are auto-discovered from labels.json (the renamed metadata.json).
#   default_top_k     — discogs-effnet has 400 classes so we widen the net there.
MODEL_REGISTRY = {
    "electronic": {
        "description": "Essentia electronic-music classes (ambient / dnb / house / techno / trance).",
        "pipeline": "two-stage",
        "embedding": {
            "filename": "discogs-effnet-bs64-1.pb",
            "output_node": "PartitionedCall:1",
        },
        "head": {
            "filename": "genre_electronic-discogs-effnet-1.pb",
        },
        "sample_rate": 16000,
        "labels_filename": "labels.json",
        "default_top_k": 5,
    },
    "jamendo": {
        "description": "MTG Jamendo 87-class genre tagger.",
        "pipeline": "two-stage",
        "embedding": {
            "filename": "discogs-effnet-bs64-1.pb",
            "output_node": "PartitionedCall:1",
        },
        "head": {
            "filename": "mtg_jamendo_genre-discogs-effnet-1.pb",
        },
        "sample_rate": 16000,
        "labels_filename": "labels.json",
        "default_top_k": 5,
    },
    "discogs-effnet": {
        "description": "Discogs Effnet 400-class fine-grained style classifier.",
        "pipeline": "single-stage",
        "embedding": None,
        "head": {
            "filename": "discogs-effnet-bs64-1.pb",
        },
        "sample_rate": 16000,
        "labels_filename": "labels.json",
        "default_top_k": 10,
    },
}


def fail(exit_code, message):
    """Print to stderr and exit with the requested code. Never returns."""
    print(message, file=sys.stderr)
    sys.exit(exit_code)


def import_essentia():
    """
    Lazy import — keep `--help` snappy when essentia isn't installed (CI scripts
    that just want to read the CLI contract). Returns the `essentia.standard`
    module and raises sys.exit(1) with a clear remediation if unavailable.

    Essentia's `[ INFO ]` lines come from its own C++ logger (separate from TF's
    sink). Mute them here unless RAYTAGGER_DEBUG=1.
    """
    try:
        import essentia
        if not _DEBUG:
            essentia.log.infoActive = False
            essentia.log.warningActive = False  # graph-loading "[WARNING]" lines too
        import essentia.standard as es  # noqa: F401 — needed after the log toggle
        return es
    except ImportError as exc:
        fail(
            EXIT_GENERIC,
            f"essentia-tensorflow is not installed: {exc}\n"
            f"Install via: pip install essentia-tensorflow\n"
            f"Supported on Python 3.9–3.12 (cp39–cp312 wheels on PyPI).",
        )


def load_head_metadata(model_dir, labels_filename):
    """
    Load the head model's published metadata. The bootstrapper renames Essentia's
    `<model-name>.json` to `labels.json` per the manifest; we treat that file as
    the canonical source for BOTH the class vocabulary AND the model's TensorFlow
    input/output node names. Returns a dict with keys `labels`, `input_node`,
    `output_node`. Falls back to None for nodes when the metadata is the bare-list
    legacy shape — caller can use Essentia's defaults in that case.
    """
    labels_path = model_dir / labels_filename
    if not labels_path.is_file():
        fail(
            EXIT_MODEL_MISSING,
            f"Labels file missing: {labels_path}\n"
            f"The model archive should contain both model.pb and labels.json.",
        )
    try:
        with labels_path.open("r", encoding="utf-8") as fp:
            data = json.load(fp)
    except (OSError, json.JSONDecodeError) as exc:
        fail(EXIT_GENERIC, f"Failed to parse labels file {labels_path}: {exc}")

    # Legacy bare list — no schema, caller falls back to Essentia defaults.
    if isinstance(data, list):
        return {"labels": data, "input_node": None, "output_node": None}

    if not isinstance(data, dict):
        fail(EXIT_GENERIC, f"Unrecognised labels file shape in {labels_path}.")

    labels = None
    for key in ("classes", "labels"):
        if key in data and isinstance(data[key], list):
            labels = data[key]
            break
    if labels is None:
        fail(EXIT_GENERIC, f"No 'classes' or 'labels' key in {labels_path}.")

    schema = data.get("schema") or {}
    input_node = _pick_first_node(schema.get("inputs"))
    output_node = _pick_predictions_node(schema.get("outputs"))

    return {
        "labels": labels,
        "input_node": input_node,
        "output_node": output_node,
    }


def _pick_first_node(nodes):
    """Return the first node's `name`, or None when the input list is missing/empty."""
    if not isinstance(nodes, list) or not nodes:
        return None
    first = nodes[0]
    return first.get("name") if isinstance(first, dict) else None


def _pick_predictions_node(nodes):
    """
    Return the node marked `output_purpose == "predictions"`. Many Essentia models
    expose multiple outputs (predictions + penultimate layer for transfer learning);
    we want the prediction softmax/sigmoid output. Falls back to the first node
    when no node carries the marker.
    """
    if not isinstance(nodes, list) or not nodes:
        return None
    for n in nodes:
        if isinstance(n, dict) and n.get("output_purpose") == "predictions":
            return n.get("name")
    return _pick_first_node(nodes)


def load_remap(model_key):
    """
    Load the per-model remap from `remap/<model_key>.json` (bundled with this script).
    Missing remap file is not an error — labels pass through unchanged.
    """
    remap_path = SCRIPT_DIR / "remap" / f"{model_key}.json"
    if not remap_path.is_file():
        return {}
    try:
        with remap_path.open("r", encoding="utf-8") as fp:
            data = json.load(fp)
    except (OSError, json.JSONDecodeError) as exc:
        print(
            f"Warning: failed to read remap file {remap_path}: {exc} — proceeding without remap.",
            file=sys.stderr,
        )
        return {}
    if not isinstance(data, dict):
        print(
            f"Warning: remap file {remap_path} is not a JSON object — ignoring.",
            file=sys.stderr,
        )
        return {}
    # Case-insensitive lookup. Model labels are usually lowercase already but we
    # don't want to depend on that.
    return {str(k).lower(): str(v) for k, v in data.items()}


def apply_remap(label, remap):
    """Apply the remap, fall through to the raw label when no entry exists."""
    return remap.get(label.lower(), label)


def verify_model_files(model_dir, config):
    """
    Pre-flight check: every .pb file the pipeline needs must exist. Returns the
    list of resolved absolute paths the pipeline will use. Missing → exit code 2
    so the C# wrapper triggers a download retry.
    """
    required = []
    if config["pipeline"] == "two-stage":
        required.append(model_dir / config["embedding"]["filename"])
    required.append(model_dir / config["head"]["filename"])

    for path in required:
        if not path.is_file():
            fail(EXIT_MODEL_MISSING, f"Model file missing: {path}")
    return required


def load_audio(es, audio_path, sample_rate):
    """Load mono audio at the model's required sample rate."""
    try:
        return es.MonoLoader(
            filename=str(audio_path),
            sampleRate=sample_rate,
            resampleQuality=4,
        )()
    except RuntimeError as exc:
        fail(EXIT_AUDIO_UNREADABLE, f"Audio unreadable ({audio_path}): {exc}")


def run_two_stage(es, audio, model_dir, config, metadata):
    """Embedding model → classification head. Returns a (T, num_classes) array."""
    embedding_extractor = es.TensorflowPredictEffnetDiscogs(
        graphFilename=str(model_dir / config["embedding"]["filename"]),
        output=config["embedding"]["output_node"],
    )
    embeddings = embedding_extractor(audio)

    # Auto-discovered input/output nodes from the head's metadata — varies per model
    # (e.g. genre_electronic uses model/Softmax, jamendo uses model/Sigmoid). Falls back
    # to Essentia's defaults when metadata has no schema (legacy bare-list labels file).
    head_kwargs = {"graphFilename": str(model_dir / config["head"]["filename"])}
    if metadata["input_node"]:
        head_kwargs["input"] = metadata["input_node"]
    if metadata["output_node"]:
        head_kwargs["output"] = metadata["output_node"]
    head = es.TensorflowPredict2D(**head_kwargs)
    return head(embeddings)


def run_single_stage(es, audio, model_dir, config, metadata):
    """The .pb file IS the classifier — feed audio directly. Returns (T, num_classes)."""
    classifier_kwargs = {"graphFilename": str(model_dir / config["head"]["filename"])}
    # The single-stage path uses TensorflowPredictEffnetDiscogs which preprocesses audio
    # itself (mel-spectrogram inside the wrapper) — `input` is fixed, only `output` is
    # overridable. Pick the predictions output from metadata so we don't accidentally
    # tap the embedding layer.
    if metadata["output_node"]:
        classifier_kwargs["output"] = metadata["output_node"]
    classifier = es.TensorflowPredictEffnetDiscogs(**classifier_kwargs)
    return classifier(audio)


def compute_top_k(predictions, labels, top_k, remap):
    """Average per-class probabilities over time, then return the top-k labels."""
    # numpy is a transitive dep of essentia-tensorflow.
    import numpy as np

    if predictions.ndim != 2:
        fail(EXIT_GENERIC, f"Unexpected prediction shape {predictions.shape}; expected (segments, classes).")
    if predictions.shape[1] != len(labels):
        fail(
            EXIT_GENERIC,
            f"Prediction class count {predictions.shape[1]} does not match "
            f"labels file size {len(labels)} — model/labels mismatch.",
        )

    mean = np.mean(predictions, axis=0)
    top_k = max(1, min(top_k, len(labels)))
    top_indices = np.argsort(mean)[::-1][:top_k]

    return [
        {
            "label": apply_remap(labels[i], remap),
            "probability": float(mean[i]),
        }
        for i in top_indices
    ]


def classify(args):
    """Main classification dispatch. Returns an exit code; never raises."""
    if args.model not in MODEL_REGISTRY:
        fail(EXIT_GENERIC, f"Unknown model '{args.model}'. Choices: {sorted(MODEL_REGISTRY.keys())}")
    config = MODEL_REGISTRY[args.model]

    models_dir = Path(args.models_dir).resolve()
    if not models_dir.is_dir():
        fail(EXIT_MODEL_MISSING, f"Models directory does not exist: {models_dir}")

    model_dir = models_dir / args.model
    if not model_dir.is_dir():
        fail(EXIT_MODEL_MISSING, f"Model subdirectory does not exist: {model_dir}")

    audio_path = Path(args.audio).resolve()
    if not audio_path.is_file():
        fail(EXIT_AUDIO_UNREADABLE, f"Audio file does not exist: {audio_path}")

    verify_model_files(model_dir, config)

    # Metadata + label list are read up-front so node-name failures surface before we
    # pay for audio loading and TF graph initialisation.
    metadata = load_head_metadata(model_dir, config["labels_filename"])

    es = import_essentia()
    audio = load_audio(es, audio_path, config["sample_rate"])

    if config["pipeline"] == "two-stage":
        predictions = run_two_stage(es, audio, model_dir, config, metadata)
    else:
        predictions = run_single_stage(es, audio, model_dir, config, metadata)

    remap = load_remap(args.model)
    top_k = args.top_k if args.top_k is not None else config["default_top_k"]
    top_predictions = compute_top_k(predictions, metadata["labels"], top_k, remap)

    print(json.dumps({"model": args.model, "predictions": top_predictions}))
    return EXIT_OK


def parse_args(argv):
    parser = argparse.ArgumentParser(
        description="Run an Essentia-published TensorFlow genre model on one audio file.",
    )
    parser.add_argument(
        "--model",
        required=True,
        choices=sorted(MODEL_REGISTRY.keys()),
        help="Which model to use.",
    )
    parser.add_argument(
        "--audio",
        required=True,
        help="Absolute path to the audio file (any format MonoLoader supports).",
    )
    parser.add_argument(
        "--models-dir",
        required=True,
        help="Directory containing per-model subdirectories with .pb + labels.json files.",
    )
    parser.add_argument(
        "--top-k",
        type=int,
        default=None,
        help="Number of top predictions to emit (default: model-specific, 5 for genre / 10 for discogs-effnet).",
    )
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv if argv is not None else sys.argv[1:])
    try:
        return classify(args)
    except SystemExit:
        raise
    except Exception as exc:  # pragma: no cover — defensive last line
        print(f"Unhandled error: {exc}", file=sys.stderr)
        traceback.print_exc(file=sys.stderr)
        return EXIT_GENERIC


if __name__ == "__main__":
    sys.exit(main())
