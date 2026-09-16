#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
OUTPUT_DIR=${1:-"$ROOT_DIR/artifacts/dependency-inventory"}
SOLUTION="$ROOT_DIR/MeowSSH.slnx"
APP_PROJECT="$ROOT_DIR/src/MeowSSH.App/MeowSSH.App.csproj"

mkdir -p "$OUTPUT_DIR"

# MeowSSH.slnx intentionally contains the cross-platform/test projects but not
# the Android app head. Restore and inventory both scopes so release evidence
# covers the shipping application's Maui/Billing/Integrity package graph too.
dotnet restore "$SOLUTION"
dotnet restore "$APP_PROJECT"

dotnet package list \
  --project "$SOLUTION" \
  --include-transitive \
  --no-restore \
  --format json \
  --output-version 1 \
  > "$OUTPUT_DIR/solution-nuget-dependencies.json"

dotnet package list \
  --project "$SOLUTION" \
  --include-transitive \
  --vulnerable \
  --no-restore \
  --format json \
  --output-version 1 \
  > "$OUTPUT_DIR/solution-nuget-vulnerabilities.json"

dotnet package list \
  --project "$APP_PROJECT" \
  --include-transitive \
  --no-restore \
  --format json \
  --output-version 1 \
  > "$OUTPUT_DIR/android-app-nuget-dependencies.json"

dotnet package list \
  --project "$APP_PROJECT" \
  --include-transitive \
  --vulnerable \
  --no-restore \
  --format json \
  --output-version 1 \
  > "$OUTPUT_DIR/android-app-nuget-vulnerabilities.json"

dotnet --info > "$OUTPUT_DIR/dotnet-info.txt"

git -C "$ROOT_DIR" rev-parse HEAD > "$OUTPUT_DIR/source-commit.txt"

(
  cd "$OUTPUT_DIR"
  sha256sum \
    solution-nuget-dependencies.json \
    solution-nuget-vulnerabilities.json \
    android-app-nuget-dependencies.json \
    android-app-nuget-vulnerabilities.json \
    dotnet-info.txt \
    source-commit.txt \
    > SHA256SUMS
)

printf 'Dependency evidence written to %s\n' "$OUTPUT_DIR"
