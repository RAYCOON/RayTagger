#!/usr/bin/env python3
"""
Remap coverage analyzer — developer tool, not part of the production pipeline.

Simulates the full Python-remap → .NET-normaliser → .NET-resolver pipeline for
every label of every model, then reports:

  MATCH     — label resolves to a taxonomy genre (and possibly subgenre).
  NEAR-MISS — label contains a partial-match against a taxonomy entry but the
              normaliser+resolver can't get there. Candidate for a new remap entry.
  OOV       — label is genuinely outside the taxonomy (Blues, Classical, etc.).
              Correct outcome: no match, dropped silently.

Use when:
- Adding a new TF model (verify its label vocabulary against the remap).
- Editing taxonomy.yaml (some labels that resolved before may now miss).
- Editing remap/*.json (confirm no regressions).

Requires the model metadata.json files in a labels directory. Defaults to
`/tmp/raytagger-remap-analysis/<model-key>/labels.json`. Download URLs match
the .NET bootstrap manifest (see docs/PLAN_GENRE_CLASSIFICATION.md §4.3):

    mkdir -p /tmp/raytagger-remap-analysis/{electronic,jamendo,discogs-effnet}
    curl -sSfL -o /tmp/raytagger-remap-analysis/electronic/labels.json \\
        https://essentia.upf.edu/models/classification-heads/genre_electronic/genre_electronic-discogs-effnet-1.json
    curl -sSfL -o /tmp/raytagger-remap-analysis/jamendo/labels.json \\
        https://essentia.upf.edu/models/classification-heads/mtg_jamendo_genre/mtg_jamendo_genre-discogs-effnet-1.json
    curl -sSfL -o /tmp/raytagger-remap-analysis/discogs-effnet/labels.json \\
        https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.json

Then:
    python3 tools/raytagger-genre-classifier/dev/analyze_remap_coverage.py

Override the labels directory via the RAYTAGGER_REMAP_ANALYSIS_DIR env var.
"""

import json
import os
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
TOOLS_DIR = REPO_ROOT / "tools" / "raytagger-genre-classifier"
ANALYSIS_DIR = Path(os.environ.get("RAYTAGGER_REMAP_ANALYSIS_DIR", "/tmp/raytagger-remap-analysis"))

# Taxonomy (mirrors ./music/taxonomy.yaml)

GENRES = [
    "House", "Indie Dance", "Techno", "Trance", "Drum and Bass", "Dubstep",
    "Hip Hop", "R&B", "Rap", "Pop", "Rock", "Ambient", "Trip Hop", "Downtempo",
    "Jazz", "Funk", "Soul",
]

SUBGENRES = {
    "House":         ["Deep", "Tech", "Soul", "Soulful", "Funk", "Jazz", "Progressive", "Afro", "Latin", "Disco", "Bass", "Minimal", "Vocal"],
    "Techno":        ["Detroit", "Berlin", "Acid", "Melodic", "Industrial", "Minimal", "Hard", "Dub"],
    "Trance":        ["Progressive", "Uplifting", "Psy", "Vocal", "Tech", "Hard", "Goa"],
    "Drum and Bass": ["Liquid", "Neurofunk", "Jump-up", "Halftime", "Minimal", "Jungle", "Drumfunk", "Rollers"],
    "Hip Hop":       ["Boom Bap", "Trap", "Lo-fi", "Conscious", "Old School", "Drill"],
}


# .NET ClassifierLabelNormaliser mirror

def normalise(s):
    if not s or not s.strip():
        return ""
    s = s.lower()
    s = re.sub(r"[_\-/]", " ", s)
    s = re.sub(r"\s+", " ", s).strip()
    if not s:
        return ""
    if len(s) > 4 and s.endswith("s"):
        trimmed = s[:-1]
        if all((c.isascii() and c.isalpha()) or c == " " for c in trimmed) and not trimmed.endswith("s"):
            s = trimmed
    return s


# .NET TaxonomyGenreResolver mirror

def find_genre_matches(normalised_label):
    hits = []
    for g in GENRES:
        if re.search(rf"\b{re.escape(g)}\b", normalised_label, re.IGNORECASE):
            hits.append(g)
    hits.sort(key=lambda x: -len(x))
    return hits


def find_subgenre_matches(rest_text, parent_genre):
    if parent_genre not in SUBGENRES:
        return []
    hits = []
    for sub in SUBGENRES[parent_genre]:
        if re.search(rf"\b{re.escape(sub)}\b", rest_text, re.IGNORECASE):
            hits.append(sub)
    hits.sort(key=lambda x: -len(x))
    return hits


def remove_genre_word(text, genre):
    pattern = rf"\b{re.escape(genre)}\b"
    stripped = re.sub(pattern, " ", text, flags=re.IGNORECASE)
    return re.sub(r"\s+", " ", stripped).strip()


def resolve(label, remap):
    remapped = remap.get(label.lower(), label)
    norm = normalise(remapped)
    if not norm:
        return ("EMPTY", norm, None, None)
    genre_hits = find_genre_matches(norm)
    if not genre_hits:
        return ("NO_MATCH", norm, None, None)
    parent = genre_hits[0]
    rest = remove_genre_word(norm, parent)
    sub_hits = find_subgenre_matches(rest, parent)
    sub = sub_hits[0] if sub_hits else None
    return ("MATCH", norm, parent, sub)


# Near-miss heuristic

TAXONOMY_WORDS = set()
for g in GENRES:
    for w in re.split(r"\s+", g.lower()):
        if len(w) >= 3 and w not in {"and"}:
            TAXONOMY_WORDS.add(w)
for parent, subs in SUBGENRES.items():
    for s in subs:
        for w in re.split(r"\s+", s.lower()):
            if len(w) >= 3:
                TAXONOMY_WORDS.add(w)


def is_near_miss(label):
    norm = label.lower()
    tokens = re.split(r"[\s_\-/]+", norm)
    for token in tokens:
        for tw in TAXONOMY_WORDS:
            if tw in token and len(tw) >= 3:
                return True
    return False


def load_labels(path):
    data = json.loads(Path(path).read_text())
    if isinstance(data, list):
        return data
    for key in ("classes", "labels"):
        if key in data and isinstance(data[key], list):
            return data[key]
    raise ValueError(f"Bad labels file: {path}")


def load_remap(model_key):
    path = TOOLS_DIR / "remap" / f"{model_key}.json"
    if not path.exists():
        return {}
    data = json.loads(path.read_text())
    return {k.lower(): v for k, v in data.items() if not k.startswith("_")}


def analyze(model_key, labels_file):
    labels = load_labels(labels_file)
    remap = load_remap(model_key)
    matched, near_miss, oov = [], [], []
    for label in labels:
        status, norm, parent, sub = resolve(label, remap)
        if status == "MATCH":
            matched.append((label, norm, parent, sub))
        else:
            if is_near_miss(label):
                near_miss.append((label, norm))
            else:
                oov.append(label)
    return matched, near_miss, oov


def main():
    models = [
        ("electronic", ANALYSIS_DIR / "electronic" / "labels.json"),
        ("jamendo", ANALYSIS_DIR / "jamendo" / "labels.json"),
        ("discogs-effnet", ANALYSIS_DIR / "discogs-effnet" / "labels.json"),
    ]

    for model_key, labels_file in models:
        if not labels_file.exists():
            print(f"\n=== {model_key}: SKIP (no labels at {labels_file}) ===\n")
            continue

        print(f"\n=== {model_key} ===")
        matched, near_miss, oov = analyze(model_key, labels_file)
        print(f"  matched:    {len(matched):4d}")
        print(f"  near-miss:  {len(near_miss):4d}  (candidates for remap)")
        print(f"  out-of-vocab: {len(oov):4d}  (correctly dropped)")

        if matched:
            print(f"\n  --- MATCHED (sample) ---")
            for label, norm, parent, sub in matched[:15]:
                sub_str = f" + {sub}" if sub else ""
                print(f"    {label!r:50}  ->  {parent}{sub_str}")
            if len(matched) > 15:
                print(f"    ... and {len(matched) - 15} more")

        if near_miss:
            print(f"\n  --- NEAR-MISS (needs remap) ---")
            for label, norm in near_miss:
                print(f"    {label!r:50}  ->  normalised: {norm!r}")

        if oov:
            print(f"\n  --- OUT-OF-VOCAB (all {len(oov)} dropped, review for missed remaps) ---")
            for label in oov:
                print(f"    {label!r}")


if __name__ == "__main__":
    main()
