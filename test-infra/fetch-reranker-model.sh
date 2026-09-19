#!/usr/bin/env bash
#
# Fetches the ONNX reranker assets used by the reranking integration tests.
#
# Repeatable and verifiable: every file is checked against the size the repository reports, so a
# truncated or partially-written download fails here rather than much later as a mysterious parity
# failure. The assets land under artifacts/ (gitignored) because they are hundreds of megabytes and
# are not source.
#
# Nothing here is needed to build or to run the unit suite. Only the reranking integration tests
# read these, and they skip cleanly when the model is absent.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="${HINDSIGHT_RERANKER_DIR:-$REPO_ROOT/artifacts/reranker}"

# Which precision to fetch. int8 is the default: measured against fp32 on the same candidates it
# produced the same ordering with the same near-identical scores, at a fifth of the size, which
# makes it the right thing to have on disk by default.
PRECISION="${1:-int8}"

case "$PRECISION" in
  int8)
    MODEL_URL="https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/onnx/model_int8.onnx"
    MODEL_NAME="model_int8.onnx"
    MODEL_BYTES=570727094
    ;;
  fp32)
    # The fp32 export is split: a small graph file plus a large external-weights file.
    MODEL_URL="https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/onnx/model.onnx"
    MODEL_NAME="model.onnx"
    MODEL_BYTES=656891
    ;;
  *)
    echo "[reranker] unknown precision '$PRECISION'; use int8 or fp32" >&2
    exit 2
    ;;
esac

TOKENIZER_URL="https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/tokenizer.json"
TOKENIZER_NAME="tokenizer.json"
TOKENIZER_BYTES=17098273

SP_URL="https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/main/sentencepiece.bpe.model"
SP_NAME="sentencepiece.bpe.model"
SP_BYTES=5069051

mkdir -p "$DEST"

fetch() {
  local url="$1" name="$2" expected="$3"

  if [[ -f "$DEST/$name" ]] && [[ "$(stat -c%s "$DEST/$name")" == "$expected" ]]; then
    echo "[reranker] $name already present and the right size"
    return
  fi

  echo "[reranker] fetching $name ($(( expected / 1000000 )) MB)..."
  # Partial downloads resume, which matters for the multi-gigabyte fp32 weights.
  curl -sSL -C - -o "$DEST/$name" "$url"
}

fetch "$MODEL_URL" "$MODEL_NAME" "$MODEL_BYTES"
fetch "$TOKENIZER_URL" "$TOKENIZER_NAME" "$TOKENIZER_BYTES"
fetch "$SP_URL" "$SP_NAME" "$SP_BYTES"

if [[ "$PRECISION" == "fp32" ]]; then
  DATA_URL="https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/main/onnx/model.onnx_data"
  DATA_NAME="model.onnx_data"
  DATA_BYTES=2271088656
  fetch "$DATA_URL" "$DATA_NAME" "$DATA_BYTES"
fi

# Verify, rather than trusting that curl succeeded. A truncated file is the failure that would
# otherwise appear much later as an unexplained reranking difference.
failed=0
for pair in "$MODEL_NAME:$MODEL_BYTES" "$TOKENIZER_NAME:$TOKENIZER_BYTES" "$SP_NAME:$SP_BYTES"; do
  name="${pair%%:*}"
  expected="${pair##*:}"
  actual="$(stat -c%s "$DEST/$name" 2>/dev/null || echo 0)"
  if [[ "$actual" != "$expected" ]]; then
    echo "[reranker] FAILED: $name is $actual bytes, expected $expected" >&2
    failed=1
  fi
done

if [[ "$failed" != 0 ]]; then
  echo "[reranker] one or more assets are wrong; delete $DEST and retry" >&2
  exit 1
fi

echo "[reranker] ready in $DEST (precision: $PRECISION)"
echo "[reranker] export HINDSIGHT_RERANKER_DIR=$DEST to point the integration tests at it"
