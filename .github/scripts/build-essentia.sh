#!/usr/bin/env bash
# Build essentia_streaming_extractor_music from MTG/essentia@master.
#
# Invoked by .github/workflows/essentia-build.yml on the CI matrix runners
# AND locally for testing. Designed to fail loud rather than to be tolerant.
#
# Required env vars:
#   ESSENTIA_REPO        absolute path of a checked-out MTG/essentia repo
#   STAGE_DIR            absolute path Tagger writes packaging output to
#   TARGET_RID           one of: osx-arm64 | osx-x64 | linux-x64
#
# Optional env vars:
#   ESSENTIA_REF         git ref to checkout (default: master)
#
# Outputs (in STAGE_DIR):
#   essentia-<commit-date>-<short-sha>-<rid>.tar.gz
#   essentia-<commit-date>-<short-sha>-<rid>.tar.gz.sha256
#   essentia-<commit-date>-<short-sha>-<rid>.commit-info.txt
#
set -euo pipefail

: "${ESSENTIA_REPO:?ESSENTIA_REPO is required}"
: "${STAGE_DIR:?STAGE_DIR is required}"
: "${TARGET_RID:?TARGET_RID is required}"
ESSENTIA_REF="${ESSENTIA_REF:-master}"

case "$TARGET_RID" in
  osx-arm64|osx-x64|linux-x64) ;;
  *)
    echo "ERROR: unsupported TARGET_RID=$TARGET_RID" >&2
    exit 2
    ;;
esac

mkdir -p "$STAGE_DIR"

# --- Resolve commit info -----------------------------------------------------
pushd "$ESSENTIA_REPO" >/dev/null

git fetch --quiet origin "$ESSENTIA_REF" || true
git checkout --quiet "$ESSENTIA_REF"

COMMIT_SHA="$(git rev-parse HEAD)"
COMMIT_SHORT="$(git rev-parse --short=8 HEAD)"
COMMIT_DATE="$(git log -1 --format=%cd --date=format:%Y%m%d HEAD)"
COMMIT_MSG="$(git log -1 --format=%s HEAD)"

echo "== Building Essentia $COMMIT_SHORT ($COMMIT_DATE) — '$COMMIT_MSG' =="

# --- Configure + build -------------------------------------------------------
WAF_ARGS=(--mode=release --with-examples --build-static)

case "$TARGET_RID" in
  osx-arm64|osx-x64)
    # Modern Essentia source uses FFmpeg 7.x API (ch_layout, codecpar). The MTG
    # Homebrew formula still pulls ffmpeg@2.8 which breaks — point pkg-config at
    # current Homebrew FFmpeg before configure. Eigen 5.x requires ≥ C++14, so
    # force C++17 to be safe.
    if [ -d /opt/homebrew/opt/ffmpeg/lib/pkgconfig ]; then
      export PKG_CONFIG_PATH="/opt/homebrew/opt/ffmpeg/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
    fi
    WAF_ARGS+=(--std=c++17)
    ;;
  linux-x64)
    # Ubuntu's stock libs work; just stay on the default toolchain.
    :
    ;;
esac

python3 ./waf configure "${WAF_ARGS[@]}"
python3 ./waf

BUILT_BIN="build/src/examples/essentia_streaming_extractor_music"
if [ ! -f "$BUILT_BIN" ]; then
  echo "ERROR: expected binary not found at $BUILT_BIN" >&2
  ls -la build/src/examples/ >&2 || true
  exit 3
fi

# Sanity probe — running the binary with no args should exit non-zero with a
# usage banner. If it segfaults, the build is broken.
set +e
"./$BUILT_BIN" >/tmp/essentia-probe.out 2>&1
rc=$?
set -e
if [ $rc -gt 2 ]; then
  echo "ERROR: built binary exited with $rc, expected 1 (usage). Output:" >&2
  cat /tmp/essentia-probe.out >&2
  exit 4
fi

popd >/dev/null

# --- Package -----------------------------------------------------------------
PKG_NAME="essentia-${COMMIT_DATE}-${COMMIT_SHORT}-${TARGET_RID}"
PKG_DIR="$STAGE_DIR/$PKG_NAME"
mkdir -p "$PKG_DIR"
cp "$ESSENTIA_REPO/$BUILT_BIN" "$PKG_DIR/essentia_streaming_extractor_music"
chmod 0755 "$PKG_DIR/essentia_streaming_extractor_music"

# On macOS, the binary references Homebrew dylib paths via LC_LOAD_DYLIB. Pull
# every non-system dependency into a sibling lib/ directory and rewrite the
# install names so the package is self-contained. `dylibbundler` is the standard
# tool for this.
if [[ "$TARGET_RID" == osx-* ]]; then
  if ! command -v dylibbundler >/dev/null 2>&1; then
    echo "ERROR: dylibbundler not on PATH. Install with: brew install dylibbundler" >&2
    exit 5
  fi
  mkdir -p "$PKG_DIR/lib"
  # -of: overwrite existing; -b: bundle; -d: dependencies dir; -p: rpath token
  dylibbundler -of -b -x "$PKG_DIR/essentia_streaming_extractor_music" \
               -d "$PKG_DIR/lib/" -p '@executable_path/lib/' >/dev/null
fi

# Tarball + SHA-256
ARCHIVE="$STAGE_DIR/${PKG_NAME}.tar.gz"
tar -C "$STAGE_DIR" -czf "$ARCHIVE" "$PKG_NAME"
SHA256="$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')"
echo "$SHA256  $(basename "$ARCHIVE")" > "${ARCHIVE}.sha256"

# Commit info for downstream consumers
cat > "$STAGE_DIR/${PKG_NAME}.commit-info.txt" <<EOF
commit_sha=$COMMIT_SHA
commit_short=$COMMIT_SHORT
commit_date=$COMMIT_DATE
commit_message=$COMMIT_MSG
target_rid=$TARGET_RID
archive=$(basename "$ARCHIVE")
sha256=$SHA256
EOF

# Clean up the intermediate directory; consumers just want the tarball.
rm -rf "$PKG_DIR"

echo
echo "== Done =="
echo "Archive : $ARCHIVE"
echo "SHA-256 : $SHA256"
echo "Commit  : $COMMIT_SHORT ($COMMIT_DATE)"
