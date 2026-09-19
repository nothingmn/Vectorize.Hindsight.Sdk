#!/usr/bin/env bash
# Resolve the LLM API key for the Hindsight test rig.
#
# Prints the key on stdout, or nothing if none is found. Never echoes it to a
# log, never writes it to disk.
#
# Resolution order:
#   1. HINDSIGHT_API_LLM_API_KEY  — explicit override, wins always
#   2. Hermes' active provider config (providers.ollama.com.api_key) — this is
#      the account actually in use on this host
#   3. OLLAMA_API_KEY in the Hermes .env — a separate account, kept as fallback
#
# Order matters: the two keys belong to different Ollama accounts with separate
# quota. The .env one is commonly exhausted while the active one still has
# headroom, so the active one is preferred.

set -euo pipefail

if [[ -n "${HINDSIGHT_API_LLM_API_KEY:-}" ]]; then
  printf '%s' "$HINDSIGHT_API_LLM_API_KEY"
  exit 0
fi

CONFIG="${HOME}/.hermes/config.yaml"
if [[ -f "$CONFIG" ]]; then
  key="$(python3 - "$CONFIG" <<'PY' 2>/dev/null || true
import re, sys, pathlib
text = pathlib.Path(sys.argv[1]).read_text()
# providers:
#   ollama.com:
#     api: https://ollama.com/v1
#     name: ...
#     api_key: <key>
m = re.search(
    r"^\s{2}ollama\.com:\s*\n"
    r"(?:\s{4}\S+:.*\n)*?"
    r"\s{4}api_key:\s*(\S+)",
    text, re.M)
print(m.group(1) if m else "")
PY
)"
  if [[ -n "$key" ]]; then
    printf '%s' "$key"
    exit 0
  fi
fi

ENV_FILE="${HOME}/.hermes/.env"
if [[ -f "$ENV_FILE" ]]; then
  key="$(grep -m1 '^OLLAMA_API_KEY=' "$ENV_FILE" | cut -d= -f2- || true)"
  if [[ -n "$key" ]]; then
    printf '%s' "$key"
    exit 0
  fi
fi

exit 1
