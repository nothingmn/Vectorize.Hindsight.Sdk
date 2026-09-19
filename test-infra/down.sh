#!/usr/bin/env bash
# Stop the local Hindsight test instance.
#
#   ./down.sh            # stop, keep the database volume
#   ./down.sh --purge    # stop and delete the database volume (clean slate)
#
# --purge only ever targets this rig's own volume. It is not a general-purpose
# teardown and must never be pointed at a production Hindsight.
#
# Note: `docker compose` interpolates the whole file for *every* subcommand, so
# `down` needs the same variables defined as `up` even though it makes no LLM
# call. They are given inert placeholders here rather than resolved, so stopping
# the rig never requires a credential and never reads one.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

log() { printf '[down] %s\n' "$*"; }

# Inert values: interpolation only. No request is made with them.
export HINDSIGHT_API_LLM_PROVIDER="${HINDSIGHT_API_LLM_PROVIDER:-openai}"
export HINDSIGHT_API_LLM_API_KEY="${HINDSIGHT_API_LLM_API_KEY:-unused-for-down}"
export HINDSIGHT_API_LLM_MODEL="${HINDSIGHT_API_LLM_MODEL:-glm-5.3-flash}"
export HINDSIGHT_API_LLM_BASE_URL="${HINDSIGHT_API_LLM_BASE_URL:-}"
export HINDSIGHT_TEST_API_PORT="${HINDSIGHT_TEST_API_PORT:-8888}"

# The container may already be gone; that is not a failure.
if ! docker compose ps --quiet 2>/dev/null | grep -q .; then
  if [[ "${1:-}" == "--purge" ]]; then
    log "no container running — purging volume anyway"
    docker compose down --volumes --remove-orphans
    log "purged. Next ./up.sh starts from an empty database."
  else
    log "already stopped."
  fi
  exit 0
fi

if [[ "${1:-}" == "--purge" ]]; then
  log "stopping container and deleting the test volume..."
  docker compose down --volumes --remove-orphans
  log "purged. Next ./up.sh starts from an empty database."
else
  log "stopping container (volume retained)..."
  docker compose down --remove-orphans
  log "stopped."
fi
