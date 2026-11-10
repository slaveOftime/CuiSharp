#!/usr/bin/env bash
# Builds CUI into native/ for Linux and macOS:
#   Linux  — libcui.so (JIT) and libcui.a (NativeAOT), X11 backend
#   macOS  — libcui.dylib (JIT) and libcui.a (NativeAOT), Cocoa backend
#
#   ./native/build-native.sh [path-to-CUI-checkout]
#
# Defaults to the CUI submodule next to this script. Requires cmake, a C compiler and,
# on Linux, the X11 development headers (apt-get install cmake build-essential libx11-dev).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src="${1:-$here/CUI}"

if [[ ! -f "$src/CMakeLists.txt" ]]; then
  echo "No CUI checkout at '$src'. Run: git submodule update --init --recursive" >&2
  exit 1
fi

# CUI_PLATFORM=auto picks the backend from the host: x11 on Linux, cocoa on macOS.
# Spelling it out on Linux keeps the build honest about its one hard dependency.
platform_args=(-DCUI_PLATFORM=auto)
if [[ "$(uname)" != "Darwin" ]]; then
  if ! pkg-config --exists x11; then
    echo "Missing X11 headers. Try: sudo apt-get install libx11-dev" >&2
    exit 1
  fi
  platform_args=(-DCUI_PLATFORM=x11)
else
  # .NET's macOS baseline is 12.0. Compiling the archive for whatever the build
  # machine runs (26.0 on a runner) makes every object newer than the executable
  # that links it, and ld says so once per object file.
  platform_args+=(-DCMAKE_OSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-12.0}")
fi

# The pinned submodule stays untouched, so the build runs against a patched copy.
# tools/bindgen.cs reads the same staged headers. See docs/PLAN.md section 4.3.
repo="$(cd "$here/.." && pwd)"
cui_rev="$(git -C "$src" rev-parse --short HEAD 2>/dev/null || echo unknown)"
# Outside the repository on purpose: the container build runs as root, and a
# root-owned artifacts/ later rejects the runner user's pack output with
# "access denied". Scratch in TMPDIR keeps the tree free of root-owned dirs.
stage="$(mktemp -d "${TMPDIR:-/tmp}/cui-native-src.XXXXXX")"
tar -C "$src" --exclude=build-shared --exclude=build-static --exclude=.git -cf - . | tar -C "$stage" -xf -
for patch in "$here"/patches/*.patch; do
  [[ -e "$patch" ]] || continue
  echo "==> patch $(basename "$patch")"
  ( cd "$stage" && git apply "$patch" ) || { echo "native patch failed to apply: $patch" >&2; exit 1; }
done
src="$stage"

common=(-DCMAKE_BUILD_TYPE=Release -DCUI_BUILD_TESTS=OFF -DCUI_BUILD_EXAMPLES=OFF
        -DCUI_WITH_APP=ON "${platform_args[@]}")

# nproc is GNU coreutils; getconf works on both Linux and macOS.
jobs="$(getconf _NPROCESSORS_ONLN)"

pushd "$src" >/dev/null
rm -rf build-shared build-static
if [[ "$(uname)" == "Darwin" ]]; then
  echo "==> libcui.dylib (shared)"
  cmake -B build-shared -G "Unix Makefiles" "${common[@]}" -DCUI_BUILD_SHARED=ON
  cmake --build build-shared -j"$jobs"
  echo "==> libcui.a (static, for NativeAOT)"
  cmake -B build-static -G "Unix Makefiles" "${common[@]}"
  cmake --build build-static -j"$jobs"
  cp build-shared/libcui.dylib build-static/libcui.a "$here/"
  produced=(libcui.dylib libcui.a)
else
  echo "==> libcui.so (shared)"
  cmake -B build-shared -G "Unix Makefiles" "${common[@]}" -DCUI_BUILD_SHARED=ON
  cmake --build build-shared -j"$jobs"
  echo "==> libcui.a (static, for NativeAOT)"
  cmake -B build-static -G "Unix Makefiles" "${common[@]}"
  cmake --build build-static -j"$jobs"
  cp build-shared/libcui.so build-static/libcui.a "$here/"
  produced=(libcui.so libcui.a)
fi
popd >/dev/null

# Name the files this platform produced; `ls` on the ones it does not build would
# fail the pipeline and, under set -e, the script with it.
for f in "${produced[@]}"; do
  ls -l "$here/$f" | awk '{printf "    %-12s %8.0f KB\n", $9, $5/1024}'
done

# The ABI a Linux package promises, checked rather than inherited from whichever image
# happened to build it: a .so linked against a newer glibc loads on nothing older, and
# that failure surfaces in a consumer's environment, not here.
baseline="${CUI_GLIBC_BASELINE:-2.31}"
provenance="$here/native-provenance.txt"
{
  echo "cui-rev=$cui_rev"
  echo "os=$(uname -s)-$(uname -m)"
  echo "built-on=$( . /etc/os-release 2>/dev/null && echo "${PRETTY_NAME:-unknown}" || echo unknown)"
  echo "cc=$("${CC:-cc}" --version 2>/dev/null | head -1)"
} > "$provenance"

if [[ "$(uname)" != "Darwin" ]] && command -v objdump >/dev/null; then
  required="$(objdump -T "$here/libcui.so" | grep -o 'GLIBC_[0-9.]*' | sed 's/GLIBC_//' | sort -Vu | tail -1)"
  echo "requires-glibc=$required" >> "$provenance"
  echo "baseline-glibc=$baseline" >> "$provenance"
  if [[ -n "$required" && "$(printf '%s\n%s\n' "$baseline" "$required" | sort -V | tail -1)" != "$baseline" ]]; then
    echo "ERROR: libcui.so needs glibc $required but the package promises $baseline." >&2
    echo "       Build on an image at or below the baseline (CUI_GLIBC_BASELINE=$baseline)," >&2
    echo "       or raise the promise deliberately and record it in docs/NATIVE-ABI.md." >&2
    exit 1
  fi
fi

# Recorded with the artifact, not reconstructed later: a package that cannot say which
# CUI revision and compiler produced its library is a package nobody can audit.
for f in "${produced[@]}"; do
  if command -v sha256sum >/dev/null; then
    echo "file=$f sha256=$(sha256sum "$here/$f" | cut -d' ' -f1)" >> "$provenance"
  else
    echo "file=$f sha256=$(shasum -a 256 "$here/$f" | cut -d' ' -f1)" >> "$provenance"
  fi
done

echo "Provenance written to $provenance."
echo "Native artifacts ready."
