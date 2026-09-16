#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
EVIDENCE_DIR=${1:-"$ROOT_DIR/artifacts/dependency-inventory"}
EXPECTED_COMMIT=${2:-"$(git -C "$ROOT_DIR" rev-parse HEAD)"}

required_reports=(
  solution-nuget-dependencies.json
  solution-nuget-vulnerabilities.json
  android-app-nuget-dependencies.json
  android-app-nuget-vulnerabilities.json
)

for report in "${required_reports[@]}"; do
  if [[ ! -f "$EVIDENCE_DIR/$report" ]]; then
    echo "Missing dependency evidence report: $report" >&2
    exit 1
  fi
  python3 -m json.tool "$EVIDENCE_DIR/$report" >/dev/null
 done

if ! grep -Fq 'src/MeowSSH.App/MeowSSH.App.csproj' "$EVIDENCE_DIR/android-app-nuget-dependencies.json"; then
  echo "Android application dependency evidence does not include the shipping app project." >&2
  exit 1
fi

if [[ ! -f "$EVIDENCE_DIR/source-commit.txt" ]]; then
  echo "Dependency evidence is missing source-commit.txt." >&2
  exit 1
fi
ACTUAL_COMMIT=$(tr -d '\r\n' < "$EVIDENCE_DIR/source-commit.txt")
if [[ "$ACTUAL_COMMIT" != "$EXPECTED_COMMIT" ]]; then
  echo "Dependency evidence source commit $ACTUAL_COMMIT does not match expected release commit $EXPECTED_COMMIT." >&2
  exit 1
fi

if [[ ! -f "$EVIDENCE_DIR/SHA256SUMS" ]]; then
  echo "Dependency evidence is missing SHA256SUMS." >&2
  exit 1
fi
(
  cd "$EVIDENCE_DIR"
  sha256sum -c SHA256SUMS
)

printf 'Dependency evidence validated for source commit %s\n' "$EXPECTED_COMMIT"
