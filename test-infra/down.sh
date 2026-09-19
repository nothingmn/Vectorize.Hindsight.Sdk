#!/usr/bin/env bash
# Stop the local Hindsight test instance.
#
#   ./down.sh            # stop, keep the database volume
#   ./down.sh --purge    # stop and delete the database volume (clean slate)
#
# --purge only ever targets this rig's own volume. It is not a general-purpose
# teardown and must never be pointed at a production Hindsight.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

log() { printf '[down] %s\n' "$*"; }

if [[ "${1:-}" == "--purge" ]]; then
  log "stopping container and deleting the test volume..."
  docker compose down --volumes --remove-orphans
  log "purged. Next ./up.sh starts from an empty database."
else
  log "stopping container (volume retained)..."
  docker compose down --remove-orphans
  log "stopped."
fi
