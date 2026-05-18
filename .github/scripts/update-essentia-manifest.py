#!/usr/bin/env python3
"""
Update `samples/native-tools.example.yaml` after a fresh Essentia build run.

Reads one or more `*.commit-info.txt` files produced by build-essentia.sh and
rewrites the corresponding `tools.essentia_streaming_extractor_music.sources.*`
entries in-place, preserving every line/comment we don't touch.

Why a line-aware rewriter and not yaml.dump:
- The manifest carries hand-curated comments documenting platform gaps and
  upstream provenance. A round-trip through PyYAML would discard them.
- We touch ≤ 4 fields per RID block; surgical edits keep the diff reviewable.

Invoked from .github/workflows/essentia-build.yml and runnable locally.

Usage:
    python3 update-essentia-manifest.py \\
        --manifest samples/native-tools.example.yaml \\
        --version "master-20260518-7e90d20b" \\
        --release-tag "essentia-tools-2026-05" \\
        --repo "RAYCOON/raytagger" \\
        artifacts/*.commit-info.txt
"""
from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path

ARCHIVE_NAME_RE = re.compile(
    r"^essentia-(?P<date>\d{8})-(?P<short>[0-9a-f]{8})-(?P<rid>[a-z0-9-]+)\.tar\.gz$"
)


@dataclass
class BuildResult:
    rid: str
    commit_short: str
    commit_date: str
    archive: str
    sha256: str

    @classmethod
    def from_info_file(cls, path: Path) -> "BuildResult":
        fields: dict[str, str] = {}
        for line in path.read_text().splitlines():
            if "=" not in line:
                continue
            k, _, v = line.partition("=")
            fields[k.strip()] = v.strip()
        for required in ("target_rid", "commit_short", "commit_date", "archive", "sha256"):
            if required not in fields:
                raise ValueError(f"{path}: missing field '{required}'")
        return cls(
            rid=fields["target_rid"],
            commit_short=fields["commit_short"],
            commit_date=fields["commit_date"],
            archive=fields["archive"],
            sha256=fields["sha256"],
        )


def update_manifest(
    manifest_path: Path,
    builds: list[BuildResult],
    version: str,
    release_tag: str,
    repo: str,
) -> str:
    text = manifest_path.read_text()
    lines = text.splitlines(keepends=True)

    # Locate the essentia tool block start.
    tool_start = None
    for i, line in enumerate(lines):
        if re.match(r"^\s*essentia_streaming_extractor_music\s*:\s*$", line):
            tool_start = i
            break
    if tool_start is None:
        raise SystemExit(
            "samples/native-tools.example.yaml is missing the "
            "essentia_streaming_extractor_music key — refusing to invent a new tool block."
        )

    # The tool block ends at the first non-blank, non-comment line whose indent
    # matches the tool key. Comments and blank lines don't bound the block —
    # they're documentation that belongs to whichever tool follows.
    tool_indent = len(lines[tool_start]) - len(lines[tool_start].lstrip(" "))
    tool_end = len(lines)
    for j in range(tool_start + 1, len(lines)):
        stripped = lines[j].lstrip(" ")
        if not stripped or stripped.startswith("#"):
            continue
        current_indent = len(lines[j]) - len(stripped)
        if current_indent <= tool_indent:
            tool_end = j
            break

    block = lines[tool_start:tool_end]

    # Update the version line (creates one if absent, just inside the block).
    block = _replace_or_insert_after(
        block,
        match_re=re.compile(r"^(\s*)version\s*:.*$"),
        replacement_fmt=lambda indent: f'{indent}version: "{version}"\n',
        insert_after_index=0,
        default_indent="    ",
    )

    # For each build result, rewrite the RID entry (url + sha256 + version-derived comment).
    for build in builds:
        block = _rewrite_rid_entry(block, build, release_tag, repo)

    lines[tool_start:tool_end] = block
    new_text = "".join(lines)
    return new_text


def _replace_or_insert_after(
    block: list[str],
    match_re: re.Pattern[str],
    replacement_fmt,
    insert_after_index: int,
    default_indent: str,
) -> list[str]:
    for i, line in enumerate(block):
        m = match_re.match(line)
        if m:
            block[i] = replacement_fmt(m.group(1))
            return block
    block.insert(insert_after_index + 1, replacement_fmt(default_indent))
    return block


def _rewrite_rid_entry(
    block: list[str],
    build: BuildResult,
    release_tag: str,
    repo: str,
) -> list[str]:
    # Locate `<rid>:` line within the sources: section. If absent, log and
    # leave the block untouched — adding a new RID with no surrounding context
    # would lose the carefully-written comments above each existing entry.
    rid_re = re.compile(rf"^(\s*){re.escape(build.rid)}\s*:\s*$")
    for i, line in enumerate(block):
        m = rid_re.match(line)
        if m:
            entry_indent = m.group(1)
            _replace_entry_fields(block, start=i, entry_indent=entry_indent, build=build,
                                  release_tag=release_tag, repo=repo)
            return block
    print(
        f"WARN: RID '{build.rid}' is not in the manifest; skipping (add an entry manually if you want it bootstrapped).",
        file=sys.stderr,
    )
    return block


def _replace_entry_fields(
    block: list[str],
    start: int,
    entry_indent: str,
    build: BuildResult,
    release_tag: str,
    repo: str,
) -> None:
    inner_indent = entry_indent + "  "
    new_url = f"https://github.com/{repo}/releases/download/{release_tag}/{build.archive}"

    # The entry ends at the first non-blank/non-comment line whose indent ≤ entry_indent.
    entry_end = len(block)
    for j in range(start + 1, len(block)):
        stripped = block[j].lstrip(" ")
        if not stripped or stripped.startswith("#"):
            continue
        current_indent = len(block[j]) - len(stripped)
        if current_indent <= len(entry_indent):
            entry_end = j
            break

    fields = {
        "url": f'"{new_url}"',
        "sha256": f'"{build.sha256}"',
        "archive_format": "tar_gz",
        "binary_path": '"essentia_streaming_extractor_music"',
    }

    replaced: set[str] = set()
    for k in range(start + 1, entry_end):
        line = block[k]
        for field, value in fields.items():
            m = re.match(rf"^({re.escape(inner_indent)}){re.escape(field)}\s*:.*$", line)
            if m:
                block[k] = f"{inner_indent}{field}: {value}\n"
                replaced.add(field)
                break

    missing = [f for f in fields if f not in replaced]
    if missing:
        insertion = [f"{inner_indent}{f}: {fields[f]}\n" for f in missing]
        block[entry_end:entry_end] = insertion


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--manifest", required=True, type=Path)
    ap.add_argument("--version", required=True, help="Version label for the essentia block, e.g. 'master-20260518-7e90d20b'.")
    ap.add_argument("--release-tag", required=True, help="GitHub release tag the binaries are attached to.")
    ap.add_argument("--repo", required=True, help="GitHub repo in 'owner/name' form.")
    ap.add_argument("info_files", nargs="+", type=Path, help="*.commit-info.txt files produced by build-essentia.sh.")
    args = ap.parse_args()

    builds = [BuildResult.from_info_file(p) for p in args.info_files]
    if not builds:
        print("No build info files supplied.", file=sys.stderr)
        return 2

    new_text = update_manifest(
        manifest_path=args.manifest,
        builds=builds,
        version=args.version,
        release_tag=args.release_tag,
        repo=args.repo,
    )
    args.manifest.write_text(new_text)
    print(f"Updated {args.manifest} for RIDs: {', '.join(b.rid for b in builds)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
