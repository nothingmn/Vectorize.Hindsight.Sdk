#!/usr/bin/env bash
# Start the local Hindsight test instance, deterministically.
#
# Usage:
#   ./up.sh                 # start and wait until /health/ready is green
#   ./up.sh --pull          # refresh the image first
#
# The LLM API key is read from the Hermes environment at run time and passed
# straight into Docker. It is never written to a file in this repository.
#
# Exit codes: 0 ready, 1 failed, 2 configuration problem.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

REPO_ROOT="$(cd .. && pwd)"
API_PORT="${HINDSIGHT_TEST_API_PORT:-8888}"
BASE_URL="http://127.0.0.1:${API_PORT}"

log() { printf '[up] %s\n' "$*"; }
die() { printf '[up] ERROR: %s\n' "$*" >&2; exit "${2:-1}"; }

# --- Resolve the LLM credentials ---------------------------------------------
# Order: explicit environment, then the Hermes .env, which is where this machine
# already keeps a working Ollama key.
if [[ -z "${HINDSIGHT_API_LLM_API_KEY:-}" ]]; then
  HERMES_ENV="${HOME}/.hermes/.env"
  if [[ -f "$HERMES_ENV" ]]; then
    HINDSIGHT_API_LLM_API_KEY="$(grep -m1 '^OLLAMA_API_KEY=' "$HERMES_ENV" | cut -d= -f2- || true)"
  fi
fi

[[ -n "${HINDSIGHT_API_LLM_API_KEY:-}" ]] \
  || die "no LLM API key. Set HINDSIGHT_API_LLM_API_KEY, or make sure OLLAMA_API_KEY is present in ${HOME}/.hermes/.env" 2

export HINDSIGHT_API_LLM_API_KEY
export HINDSIGHT_API_LLM_PROVIDER="${HINDSIGHT_API_LLM_PROVIDER:-openai}"
export HINDSIGHT_API_LLM_MODEL="${HINDSIGHT_API_LLM_MODEL:-glm-5.3-flash}"
export HINDSIGHT_API_LLM_BASE_URL="${HINDSIGHT_API_LLM_BASE_URL:-https://ollama.com/v1}"
export HINDSIGHT_TEST_API_PORT="$API_PORT"

# --- Refuse to collide with anything else on the port -------------------------
if ss -ltn 2>/dev/null | grep -q ":${API_PORT}\b"; then
  if docker ps --format '{{.Names}}' | grep -qx 'hindsight-sdk-test'; then
    log "already listening on ${API_PORT} (our container) — reusing"
  else
    die "port ${API_PORT} is already in use by something that is not this test instance. Stop it, or set HINDSIGHT_TEST_API_PORT." 1
  fi
fi

# --- Warn loudly if a *different* Hindsight is configured as the SDK target ---
# The remote reference server is read-only ground truth and must not be written to.
if [[ -f "${HOME}/.hermes/hindsight/config.json" ]]; then
  REMOTE="$(python3 -c "import json,pathlib;print(json.loads((pathlib.Path.home()/'.hermes/hindsight/config.json').read_text()).get('api_url',''))" 2>/dev/null || true)"
  [[ -n "$REMOTE" ]] && log "note: Hermes' own Hindsight is ${REMOTE} (not this instance)"
fi

if [[ "${1:-}" == "--pull" ]]; then
  log "pulling image..."
  docker compose pull
fi

log "starting container..."
docker compose up -d

# --- Wait for readiness, not for a fixed sleep --------------------------------
log "waiting for ${BASE_URL}/health/ready ..."
for _ in $(seq 1 120); do
  if curl -fsS -m 3 "${BASE_URL}/health/ready" >/dev/null 2>&1; then
    log "ready."
    break
  fi
  if ! docker ps --format '{{.Names}}' | grep -qx 'hindsight-sdk-test'; then
    die "container exited during startup. Logs:\n$(docker logs --tail 60 hindsight-sdk-test 2>&1)"
  fi
  sleep 2
done

version="$(curl -fsS -m 5 "${BASE_URL}/version" || true)"
[[ -n "$version" ]] || die "health passed but /version did not answer"

log "api_version: $(printf '%s' "$version" | python3 -c 'import sys,json;print(json.load(sys.stdin)["api_version"])' 2>/dev/null || printf '%s' "$version")"
log "live spec : $(curl -fsS -m 10 "${BASE_URL}/openapi.json" | python3 -c 'import sys,json;print(json.load(sys.stdin)["info"]["version"])' 2>/dev/null || echo '?')"
log "done. SDK client should target ${BASE_URL}"
