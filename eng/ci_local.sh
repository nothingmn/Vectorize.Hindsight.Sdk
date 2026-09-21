#!/usr/bin/env bash
# Run every gate the build workflow runs, in the same order, from committed state.
#
# GitHub Actions cannot be executed locally, so this is the closest honest substitute:
# the same commands, the same sequence, the same environment variables the workflow sets.
# Anything this script cannot reproduce — the runner image, the action versions, the
# artifact upload — is stated as unverified rather than implied.
#
# Usage:
#   bash eng/ci_local.sh            # full run
#   bash eng/ci_local.sh --quick    # skip the test-and-coverage stages
#
# Exit codes: 0 all gates passed, 1 a gate failed, 2 misconfiguration.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
ROOT="$(pwd)"

QUICK=0
[[ "${1:-}" == "--quick" ]] && QUICK=1

# The workflow's own environment, so a local run matches CI rather than approximating it.
export CI=true
export GITHUB_ACTIONS=true
export DOTNET_NOLOGO=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true

PASSED=0
FAILED=()
STEP=0

run() {
  local label="$1"; shift
  STEP=$((STEP + 1))
  printf '\n\033[1m[%d] %s\033[0m\n' "$STEP" "$label"
  local start
  start=$(date +%s)

  if "$@"; then
    printf '    ok (%ds)\n' "$(( $(date +%s) - start ))"
    PASSED=$((PASSED + 1))
  else
    printf '    FAILED (%ds)\n' "$(( $(date +%s) - start ))"
    FAILED+=("$label")
  fi
}

# --- Static gates -------------------------------------------------------------

run "Validate the workflow definitions" \
  python3 eng/validate_workflows.py

run "Verify the generated models match the spec" \
  python3 eng/generate_models.py --check

run "Verify the generated operations match the spec" \
  python3 eng/generate_operations.py --check

run "Verify the committed inventory matches the spec" \
  python3 eng/generate_inventory.py --check

run "Report the version the build will produce" \
  bash -c "echo \"  prefix: \$(python3 eng/package_version.py --prefix)\"; \
           echo \"  sha   : \$(python3 eng/package_version.py --sha)\"; \
           echo \"  build : \$(python3 eng/package_version.py)\""

# The version the workflow derives, captured once so every later step builds the
# same thing the workflow would. Sourcing it here rather than re-deriving means a
# drift between the two is impossible to miss: the pack and the verification both
# use this value.
BUILD_VERSION="$(python3 eng/package_version.py)"

# --- Build and test -----------------------------------------------------------

run "Restore" \
  dotnet restore Vectorize.Hindsight.Sdk.slnx

run "Build (Release)" \
  dotnet build Vectorize.Hindsight.Sdk.slnx --configuration Release --no-restore \
    -p:Version="$BUILD_VERSION"

if [[ "$QUICK" -eq 0 ]]; then
  run "Test with coverage" \
    # Purge first: dotnet test appends a new run directory on every invocation, and a
    # stale one would have the coverage gate merge a history of runs rather than the
    # current one - inflating the test count and keeping a no-longer-covered package
    # in the report. CI gets this for free from a fresh checkout; locally it must be
    # explicit, or the numbers stop describing the code.
    rm -rf ./artifacts/test-results
    mkdir -p ./artifacts/test-results

    dotnet test Vectorize.Hindsight.Sdk.slnx --configuration Release --no-build \
      --collect:"XPlat Code Coverage" \
      --results-directory ./artifacts/test-results \
      --logger "trx"

  run "Check coverage floors" \
    python3 eng/coverage_gate.py './artifacts/test-results/**/coverage.cobertura.xml'
else
  printf '\n\033[1m[skipped] Test and coverage (--quick)\033[0m\n'
fi

# --- Package ------------------------------------------------------------------

run "Pack" \
  bash -c "
    # Purge first, for the same reason the test results are purged: dotnet pack writes
    # into the output directory rather than replacing it, so a package from an earlier
    # version survives alongside the current one. Verification then reads a mixture of
    # generations and reports the old ones as wrong - or, worse, passes because a stale
    # package happens to satisfy the check. In CI a fresh checkout hides this; locally
    # it must be explicit.
    rm -rf ./artifacts/packages
    mkdir -p ./artifacts/packages
    dotnet pack Vectorize.Hindsight.Sdk.slnx --configuration Release --no-build \
      --output ./artifacts/packages \
      -p:Version=\"$BUILD_VERSION\""

run "Verify the produced packages" \
  bash -c "python3 eng/verify_packages.py ./artifacts/packages --expect-version \"$BUILD_VERSION\""

# The version must be reproducible: what the workflow used must equal what the source
# derives for this commit. A disagreement means an artifact exists that does not
# identify the code inside it.
run "Verify the derived version is reproducible" \
  bash -c "RECOMPUTED=\"\$(python3 eng/package_version.py)\"; \
           if [[ \"$BUILD_VERSION\" != \"\$RECOMPUTED\" ]]; then \
             echo \"::error::build used $BUILD_VERSION but the source now derives \$RECOMPUTED.\"; \
             exit 1; \
           fi; \
           echo \"  verified: $BUILD_VERSION\""

# A packed archive is not a usable one. This consumes the package the way a real
# consumer would — restore it from a local feed and call into it — which is the only
# check that proves the lib/ payload actually loads.
run "Consume the packed package from a local feed" \
  bash eng/consume_package.sh

# --- Verdict ------------------------------------------------------------------

printf '\n\033[1m────────────────────────────────────────────────────────\033[0m\n'
printf 'gates passed: %d\n' "$PASSED"

if [[ ${#FAILED[@]} -gt 0 ]]; then
  printf 'gates failed: %d\n' "${#FAILED[@]}"
  for failure in "${FAILED[@]}"; do
    printf '  - %s\n' "$failure"
  done
  printf '\n\033[1mCI simulation FAILED\033[0m\n'
  exit 1
fi

printf '\033[1mCI simulation passed\033[0m\n'
printf '\nNot reproduced by this script, and therefore unverified:\n'
printf '  - the ubuntu-latest runner image and its toolchain versions\n'
printf '  - the actions themselves (checkout, setup-dotnet, upload-artifact)\n'
printf '  - SourceLink, which only produces a source link in a clone with a remote\n'
printf '    and is therefore referenced only when GITHUB_ACTIONS is set\n'
printf '  - the artifact upload and the release job in release.yml\n'
printf '    (validate it with: gh workflow run release.yml)\n'
