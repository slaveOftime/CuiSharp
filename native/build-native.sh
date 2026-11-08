#!/usr/bin/env bash
# Builds CUI into native/ for Linux: libcui.so (JIT) and libcui.a (NativeAOT).
#
#   ./native/build-native.sh [path-to-CUI-checkout]
#
# Defaults to the CUI submodule next to this script. Requires cmake, a C compiler and the
# X11 development headers (apt-get install cmake build-essential libx11-dev).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src="${1:-$here/CUI}"

if [[ ! -f "$src/CMakeLists.txt" ]]; then
  echo "No CUI checkout at '$src'. Run: git submodule update --init --recursive" >&2
  exit 1
fi

if ! pkg-config --exists x11; then
  echo "Missing X11 headers. Try: sudo apt-get install libx11-dev" >&2
  exit 1
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
        -DCUI_WITH_APP=ON -DCUI_PLATFORM=x11)

pushd "$src" >/dev/null
rm -rf build-shared build-static
echo "==> libcui.so (shared)"
cmake -B build-shared -G "Unix Makefiles" "${common[@]}" -DCUI_BUILD_SHARED=ON
cmake --build build-shared -j"$(nproc)"
echo "==> libcui.a (static, for NativeAOT)"
cmake -B build-static -G "Unix Makefiles" "${common[@]}"
cmake --build build-static -j"$(nproc)"
cp build-shared/libcui.so build-static/libcui.a "$here/"
popd >/dev/null

ls -l "$here/libcui.so" "$here/libcui.a" | awk '{printf "    %-12s %8.0f KB\n", $9, $5/1024}'
echo "Native artifacts ready."
