#!/usr/bin/env bash
# Exercise dependency provisioning in isolation; never contact apt or need root.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir "$work/bin"
export CI_DEPS_TEST_STATE="$work"
export PATH="$work/bin:$PATH"

printf '#!/usr/bin/env bash\nexec "$@"\n' > "$work/bin/sudo"
cat > "$work/bin/dpkg-query" <<'FAKE'
#!/usr/bin/env bash
if grep -Fxq "${@: -1}" "$CI_DEPS_TEST_STATE/installed"; then
  printf 'install ok installed'
else
  exit 1
fi
FAKE
cat > "$work/bin/apt-get" <<'FAKE'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CI_DEPS_TEST_STATE/calls"
for argument in "$@"; do
  if [[ "$argument" == "${CI_DEPS_TEST_FAIL:-never}" ]]; then exit 42; fi
  if [[ "$argument" == 'install' && "${CI_DEPS_TEST_INCOMPLETE:-0}" == 1 ]]; then exit 0; fi
  case "$argument" in
    clang|zlib1g-dev|libx11-dev) printf '%s\n' "$argument" >> "$CI_DEPS_TEST_STATE/installed" ;;
  esac
done
FAKE
chmod +x "$work/bin/"*

printf '%s\n' clang zlib1g-dev libx11-dev > "$work/installed"
bash "$repo/tools/ci-linux-deps.sh" clang zlib1g-dev libx11-dev
[[ ! -f "$work/calls" ]] || { echo 'already-installed packages caused a network request'; exit 1; }

printf '%s\n' libx11-dev > "$work/installed"
bash "$repo/tools/ci-linux-deps.sh" clang zlib1g-dev libx11-dev
[[ $(wc -l < "$work/calls") == 2 ]]
grep -q ' install -y --no-install-recommends clang zlib1g-dev$' "$work/calls"
! grep -q 'libx11-dev' "$work/calls"
grep -q 'Acquire::https::Timeout=15' "$work/calls"

: > "$work/installed"
set +e
CI_DEPS_TEST_FAIL=update bash "$repo/tools/ci-linux-deps.sh" clang
exit_code=$?
set -e
[[ $exit_code == 42 ]] || { echo 'apt failure was ignored'; exit 1; }

: > "$work/installed"
set +e
CI_DEPS_TEST_INCOMPLETE=1 bash "$repo/tools/ci-linux-deps.sh" clang
exit_code=$?
set -e
[[ $exit_code == 1 ]] || { echo 'successful apt exit without installed packages was accepted'; exit 1; }
echo 'Linux dependency provisioning checks passed'
