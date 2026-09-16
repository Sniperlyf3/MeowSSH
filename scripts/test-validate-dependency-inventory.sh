#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
VALIDATOR="$ROOT_DIR/scripts/validate-dependency-inventory.sh"
EXPECTED_COMMIT=0123456789abcdef0123456789abcdef01234567
TEMP_DIR=$(mktemp -d)
trap 'rm -rf "$TEMP_DIR"' EXIT

make_fixture() {
  local dir=$1
  mkdir -p "$dir"
  printf '{"projects":[{"path":"MeowSSH.slnx"}]}\n' > "$dir/solution-nuget-dependencies.json"
  printf '{"projects":[]}\n' > "$dir/solution-nuget-vulnerabilities.json"
  printf '{"projects":[{"path":"src/MeowSSH.App/MeowSSH.App.csproj"}]}\n' > "$dir/android-app-nuget-dependencies.json"
  printf '{"projects":[]}\n' > "$dir/android-app-nuget-vulnerabilities.json"
  printf 'test dotnet info\n' > "$dir/dotnet-info.txt"
  printf '%s\n' "$EXPECTED_COMMIT" > "$dir/source-commit.txt"
  (
    cd "$dir"
    sha256sum \
      solution-nuget-dependencies.json \
      solution-nuget-vulnerabilities.json \
      android-app-nuget-dependencies.json \
      android-app-nuget-vulnerabilities.json \
      dotnet-info.txt \
      source-commit.txt \
      > SHA256SUMS
  )
}

GOOD="$TEMP_DIR/good"
make_fixture "$GOOD"
bash "$VALIDATOR" "$GOOD" "$EXPECTED_COMMIT"

BAD_COMMIT="$TEMP_DIR/bad-commit"
cp -a "$GOOD" "$BAD_COMMIT"
if bash "$VALIDATOR" "$BAD_COMMIT" ffffffffffffffffffffffffffffffffffffffff >/dev/null 2>&1; then
  echo "Validator accepted evidence from the wrong source commit." >&2
  exit 1
fi

TAMPERED="$TEMP_DIR/tampered"
cp -a "$GOOD" "$TAMPERED"
printf 'tampered\n' >> "$TAMPERED/android-app-nuget-dependencies.json"
if bash "$VALIDATOR" "$TAMPERED" "$EXPECTED_COMMIT" >/dev/null 2>&1; then
  echo "Validator accepted evidence with a broken checksum." >&2
  exit 1
fi

MISSING_APP="$TEMP_DIR/missing-app"
make_fixture "$MISSING_APP"
printf '{"projects":[]}\n' > "$MISSING_APP/android-app-nuget-dependencies.json"
(
  cd "$MISSING_APP"
  sha256sum \
    solution-nuget-dependencies.json \
    solution-nuget-vulnerabilities.json \
    android-app-nuget-dependencies.json \
    android-app-nuget-vulnerabilities.json \
    dotnet-info.txt \
    source-commit.txt \
    > SHA256SUMS
)
if bash "$VALIDATOR" "$MISSING_APP" "$EXPECTED_COMMIT" >/dev/null 2>&1; then
  echo "Validator accepted evidence that omitted the shipping Android app project." >&2
  exit 1
fi

printf 'Dependency evidence validator self-tests passed.\n'
