#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
OUTPUT_DIR=${1:-"$ROOT_DIR/artifacts/dependency-inventory"}
SOLUTION="$ROOT_DIR/MeowSSH.slnx"

mkdir -p "$OUTPUT_DIR"

# Restore once, then keep both reports tied to the same resolved dependency graph.
dotnet restore "$SOLUTION"

dotnet package list \
  --project "$SOLUTION" \
  --include-transitive \
  --no-restore \
  --format json \
  --output-version 1 \
  > "$OUTPUT_DIR/nuget-dependencies.json"

dotnet package list \
  --project "$SOLUTION" \
  --include-transitive \
  --vulnerable \
  --no-restore \
  --format json \
  --output-version 1 \
  > "$OUTPUT_DIR/nuget-vulnerabilities.json"

dotnet --info > "$OUTPUT_DIR/dotnet-info.txt"

git -C "$ROOT_DIR" rev-parse HEAD > "$OUTPUT_DIR/source-commit.txt"

(
  cd "$OUTPUT_DIR"
  sha256sum \
    nuget-dependencies.json \
    nuget-vulnerabilities.json \
    dotnet-info.txt \
    source-commit.txt \
    > SHA256SUMS
)

printf 'Dependency evidence written to %s\n' "$OUTPUT_DIR"
