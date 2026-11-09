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
fi

# The pinned submodule stays untouched, so the build runs against a patched copy.
# tools/bindgen.cs reads the same staged headers. See docs/PLAN.md section 4.3.
repo="$(cd "$here/.." && pwd)"
stage="$repo/artifacts/native-src"
rm -rf "$stage"
mkdir -p "$stage"
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
echo "Native artifacts ready."
