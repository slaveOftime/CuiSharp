#!/usr/bin/env bash
# Hosted runners already carry most build dependencies. Do not refresh every apt repository just
# to reinstall X11 headers: an unrelated repository outage used to stall this step for hours.
set -euo pipefail

if (( $# == 0 )); then
  echo "usage: bash tools/ci-linux-deps.sh <package>..." >&2
  exit 2
fi

installed() {
  [[ "$(dpkg-query -W -f='${Status}' "$1" 2>/dev/null || true)" == 'install ok installed' ]]
}

missing=()
for package in "$@"; do
  if ! installed "$package"; then missing+=("$package"); fi
done
if (( ${#missing[@]} == 0 )); then
  echo "==> Linux dependencies already installed: $* (no apt update needed)"
  exit 0
fi

echo "==> Installing missing Linux dependencies: ${missing[*]}"
options=(-o Acquire::Retries=2 -o Acquire::http::Timeout=15 -o Acquire::https::Timeout=15
         -o APT::Update::Error-Mode=any -o DPkg::Lock::Timeout=30)
# These dependencies all come from Ubuntu, not the runner's Microsoft or other third-party repos.
# Ubuntu 24.04 uses deb822; 22.04 uses sources.list. Leave the system's files unchanged.
for sources in /etc/apt/sources.list.d/ubuntu.sources /etc/apt/sources.list; do
  if [[ -s "$sources" ]]; then
    options+=(-o "Dir::Etc::sourcelist=$sources" -o Dir::Etc::sourceparts=-)
    break
  fi
done
sudo timeout --kill-after=10s 120s apt-get "${options[@]}" update
sudo timeout --kill-after=10s 180s env DEBIAN_FRONTEND=noninteractive apt-get "${options[@]}" install -y --no-install-recommends "${missing[@]}"
for package in "$@"; do
  if ! installed "$package"; then
    echo "apt did not install required package: $package" >&2
    exit 1
  fi
done
