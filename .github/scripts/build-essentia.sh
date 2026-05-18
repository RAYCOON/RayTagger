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
  osx-arm64|osx-x64|linux-x64|win-x64) ;;
  *)
    echo "ERROR: unsupported TARGET_RID=$TARGET_RID" >&2
    exit 2
    ;;
esac

# On Windows we run inside MSYS2's bash; the GH Actions env hands us paths
# in mixed Windows form (e.g. D:\a\raytagger\raytagger/essentia). Convert to
# proper MSYS2/Unix form so quoting and globbing behave as expected.
if [[ "$TARGET_RID" == win-* ]]; then
  command -v cygpath >/dev/null 2>&1 || { echo "ERROR: cygpath not found — Windows build must run inside MSYS2"; exit 6; }
  ESSENTIA_REPO=$(cygpath -u "$ESSENTIA_REPO")
  STAGE_DIR=$(cygpath -u "$STAGE_DIR")
fi

# Portable SHA-256: macOS BSD has shasum, GNU/MSYS2 have sha256sum. Either works.
if command -v sha256sum >/dev/null 2>&1; then
  SHA_CMD=(sha256sum)
elif command -v shasum >/dev/null 2>&1; then
  SHA_CMD=(shasum -a 256)
else
  echo "ERROR: neither sha256sum nor shasum is available" >&2
  exit 7
fi

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
  win-x64)
    # MSYS2 / MinGW64 toolchain. pkg-config files for all our deps live under
    # /mingw64/lib/pkgconfig — pacman puts them there. Same C++17 reason as macOS.
    export PKG_CONFIG_PATH="/mingw64/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
    WAF_ARGS+=(--std=c++17)
    ;;
esac

python3 ./waf configure "${WAF_ARGS[@]}"
python3 ./waf

if [[ "$TARGET_RID" == win-* ]]; then
  BUILT_BIN="build/src/examples/essentia_streaming_extractor_music.exe"
else
  BUILT_BIN="build/src/examples/essentia_streaming_extractor_music"
fi
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

if [[ "$TARGET_RID" == win-* ]]; then
  BIN_NAME="essentia_streaming_extractor_music.exe"
else
  BIN_NAME="essentia_streaming_extractor_music"
fi
cp "$ESSENTIA_REPO/$BUILT_BIN" "$PKG_DIR/$BIN_NAME"
chmod 0755 "$PKG_DIR/$BIN_NAME"

# --- Bundle dynamic dependencies ----------------------------------------------
case "$TARGET_RID" in
  osx-*)
    # macOS: binary references Homebrew dylib paths via LC_LOAD_DYLIB. Pull every
    # non-system dependency into a sibling lib/ directory and rewrite the install
    # names so the package is self-contained.
    if ! command -v dylibbundler >/dev/null 2>&1; then
      echo "ERROR: dylibbundler not on PATH. Install with: brew install dylibbundler" >&2
      exit 5
    fi
    mkdir -p "$PKG_DIR/lib"
    dylibbundler -of -b -x "$PKG_DIR/$BIN_NAME" \
                 -d "$PKG_DIR/lib/" -p '@executable_path/lib/' >/dev/null
    ;;
  win-x64)
    # MinGW64 builds link dynamically against DLLs in /mingw64/bin. Copy every
    # such DLL referenced by the executable (recursively) next to the .exe so the
    # package is self-contained — Windows resolves DLLs from the .exe's directory
    # before any other location, no rpath rewriting needed.
    ldd "$PKG_DIR/$BIN_NAME" \
      | awk '/=> \/mingw64\// { print $3 }' \
      | while read -r dll; do
          cp -n "$dll" "$PKG_DIR/"
        done
    # Resolve transitive deps until quiescent (libavcodec pulls libavutil etc.)
    for _ in 1 2 3 4; do
      added=0
      for dll in "$PKG_DIR"/*.dll; do
        [ -f "$dll" ] || continue
        ldd "$dll" \
          | awk '/=> \/mingw64\// { print $3 }' \
          | while read -r dep; do
              [ -f "$PKG_DIR/$(basename "$dep")" ] || { cp "$dep" "$PKG_DIR/"; }
            done
      done
    done
    ;;
  linux-x64)
    # --build-static plus glibc/libstdc++ is good enough; no extra bundling needed.
    :
    ;;
esac

# Tarball + SHA-256 (use detected SHA_CMD so it works on macOS/Linux/MSYS2 alike)
ARCHIVE="$STAGE_DIR/${PKG_NAME}.tar.gz"
tar -C "$STAGE_DIR" -czf "$ARCHIVE" "$PKG_NAME"
SHA256="$("${SHA_CMD[@]}" "$ARCHIVE" | awk '{print $1}')"
echo "$SHA256  $(basename "$ARCHIVE")" > "${ARCHIVE}.sha256"

# Commit info for downstream consumers (includes binary path inside the archive,
# which differs between Windows .exe and POSIX builds — the manifest updater
# uses this to emit the correct binary_path: in native-tools.yaml).
cat > "$STAGE_DIR/${PKG_NAME}.commit-info.txt" <<EOF
commit_sha=$COMMIT_SHA
commit_short=$COMMIT_SHORT
commit_date=$COMMIT_DATE
commit_message=$COMMIT_MSG
target_rid=$TARGET_RID
archive=$(basename "$ARCHIVE")
sha256=$SHA256
binary_in_archive=$BIN_NAME
EOF

# Clean up the intermediate directory; consumers just want the tarball.
rm -rf "$PKG_DIR"

echo
echo "== Done =="
echo "Archive : $ARCHIVE"
echo "SHA-256 : $SHA256"
echo "Commit  : $COMMIT_SHORT ($COMMIT_DATE)"
