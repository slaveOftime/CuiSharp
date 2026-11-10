#!/usr/bin/env bash
# Builds the Linux native library inside an image whose glibc is the one the package
# promises, and puts the result where build-native.sh would:
#
#   ./native/build-native-container.sh [image]     # debian:11 by default, glibc 2.31
#
# glibc merges and moves symbols between releases — libpthread's are in libc from 2.34 —
# so a .so linked on a newer image refuses to load on an older one, and the failure shows
# up in a consumer's container, not here. Building on the baseline image is what makes the
# promise true rather than inherited from whichever runner happened to compile it.
#
# Needs docker; the image is pulled on first use. Runs as root inside the container, so the
# files it writes are root-owned: this is for CI, not for a working tree you keep editing.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
image="${1:-debian:11}"
baseline="${CUI_GLIBC_BASELINE:-2.31}"

if ! command -v docker >/dev/null; then
  echo "docker is needed to build on the $image baseline image" >&2
  exit 1
fi

echo "==> building in $image (glibc baseline $baseline)"
docker run --rm \
  -v "$repo:/work" \
  -w /work \
  -e CUI_GLIBC_BASELINE="$baseline" \
  "$image" \
  bash -c '
    set -e
    export DEBIAN_FRONTEND=noninteractive
    # Bullseye left LTS: deb.debian.org still serves its security index but has
    # dropped the .debs it names, so installing anything with a security update
    # 404s. archive.debian.org keeps the last point release of every package, and
    # it is never refreshed again — hence Check-Valid-Until, or apt would call
    # the Release file expired and only warn on its way to the same failure.
    sed -i "s|deb.debian.org/debian-security|archive.debian.org/debian-security|" /etc/apt/sources.list
    echo "Acquire::Check-Valid-Until \"false\";" > /etc/apt/apt.conf.d/99-archive
    apt_options=(-o Acquire::Retries=2 -o Acquire::http::Timeout=15 -o Acquire::https::Timeout=15
                 -o APT::Update::Error-Mode=any)
    timeout --kill-after=10s 120s apt-get "${apt_options[@]}" update -qq
    # X11 headers because CUI'"'"'s only hard Linux dependency is Xlib.
    timeout --kill-after=10s 180s apt-get "${apt_options[@]}" install -y -qq --no-install-recommends \
      build-essential cmake pkg-config libx11-dev binutils git ca-certificates python3-pip >/dev/null
    # Bullseye ships cmake 3.18, and the CUI 1.3 CMakeLists uses $<CONFIG:Rel,Min>
    # with several configurations — an expression that needs 3.19. The submodule
    # is pinned and not ours to patch, so build with a current cmake instead; the
    # compiler and glibc stay the image version, which is what the baseline
    # promise rests on.
    timeout --kill-after=10s 120s pip3 install -q --timeout 20 --retries 2 "cmake>=3.28"
    echo "    cmake:  $(cmake --version | head -1 | awk "{print \$3}")"
    . /etc/os-release
    echo "    image:  $PRETTY_NAME"
    echo "    glibc:  $(ldd --version | head -1 | awk "{print \$NF}")"
    bash native/build-native.sh
  '

# The archive is what a packaged consumer links, so the file it actually got is worth saying.
grep -E 'requires-glibc|baseline-glibc|built-on' "$here/native-provenance.txt" || true
