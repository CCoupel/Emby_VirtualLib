#!/usr/bin/env bash
# CI_CMD — script unique de la CI (commands.ci, agents/infra.md §3bis C11).
# Appele par .github/workflows/ci-branch.yml (pre-validation), .github/workflows/release.yml,
# et en local par qa / deployer. Aucune etape ne doit exister uniquement en CI ou en local.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "::group::Restore"
dotnet restore src/VirtualLib
dotnet restore tests/VirtualLib.Tests
echo "::endgroup::"

echo "::group::Build (Release)"
dotnet build src/VirtualLib --configuration Release --no-restore
echo "::endgroup::"

echo "::group::Tests"
dotnet test tests/VirtualLib.Tests/ --configuration Release --no-restore
echo "::endgroup::"

echo "::group::Audit des dependances"
AUDIT=$(dotnet list src/VirtualLib package --vulnerable --include-transitive 2>&1)
echo "$AUDIT"
if echo "$AUDIT" | grep -qE '\b(High|Critical)\b'; then
  echo "Dependances vulnerables (High/Critical) detectees" >&2
  exit 1
fi
echo "::endgroup::"

echo "CI OK"
