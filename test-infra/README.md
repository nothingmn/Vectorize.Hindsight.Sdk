# Test infrastructure

A repeatable, isolated Hindsight instance for developing `Vectorize.Hindsight.Sdk` and running deep
integration tests against a real HTTP server.

Everything here is opt-in and disposable. Nothing in this directory touches a production instance.

---

## Why it exists

Unit tests with a stubbed transport prove the client speaks the shape it *thinks* the API has. They
cannot prove that shape is right. Three things in the OpenAPI specification are only checkable against
a live server:

- Undeclared status codes. The spec declares only `404` and `422`; `401`, `403`, `409`, `429` and
  `5xx` appear in prose and in the vendor's own client, never in the spec.
  ([`../docs/api-surface.md`](../docs/api-surface.md) section 1.)
- Undeclared headers, and which of the optional ones the server actually honours.
- Round-trips. Whether a payload the client serializes is one the server accepts, and whether what
  comes back deserializes into the model the spec implied.

A local instance turns all three from inference into evidence.

---

## Isolation guarantees

The rig is deliberately walled off from every other Hindsight on this machine and on the network:

| Concern | How it is isolated |
| --- | --- |
| Container | own name `hindsight-sdk-test` |
| Database | own named volume `hindsight_test_pg0` |
| Network | ports bound to `127.0.0.1` only — nothing on the LAN can reach it |
| Teardown | `down.sh --purge` deletes only this rig's named volume |
| Credentials | read at run time from the environment, never written to a tracked file |

`up.sh` refuses to start if the port is held by anything other than this container, and it prints a
notice if Hermes' own Hindsight is configured elsewhere, so the remote reference server is never
confused with the test target.

**The remote reference server is read-only ground truth.** It runs the version Hermes itself depends
on. The suite never writes to it. See [`../docs/api-surface.md`](../docs/api-surface.md) for the
version relationship.

---

## Usage

```bash
./up.sh              # start, wait for /health/ready, print the served spec version
./up.sh --pull       # refresh the image first
./down.sh            # stop, keep the database
./down.sh --purge    # stop and delete the database — clean slate
```

`up.sh` exits non-zero with the container logs if startup fails, rather than leaving a half-started
instance behind. `down.sh` needs no credentials — it supplies inert placeholders so that
`docker compose` interpolation succeeds — and is idempotent: stopping an already-stopped rig is a
no-op, not an error.

Both scripts are safe to re-run, and the rig is verified to come back up from the pinned digest
serving the same `api_version`, which is what makes a test result reproducible.

### Credentials

`up.sh` resolves the LLM key in this order:

1. `HINDSIGHT_API_LLM_API_KEY` in the environment
2. `OLLAMA_API_KEY` from `${HOME}/.hermes/.env`

The key is passed directly into the container. It is never written into a tracked file; `.env` and
`secrets/` are gitignored.

### Defaults

| Setting | Default | Why |
| --- | --- | --- |
| LLM provider | `openai` | Ollama's endpoint is OpenAI-compatible |
| LLM model | `glm-5.3-flash` | same model the production Hindsight instances use |
| LLM base URL | `https://ollama.com/v1` | |
| Embeddings | local (in image) | keeps the rig self-contained, no embedding spend |
| Reranker | local (in image) | as above |
| `HINDSIGHT_API_WORKER_ID` | `hindsight-sdk-test` | a stable value, so restarts do not orphan claimed work |
| API port | `8888` | override with `HINDSIGHT_TEST_API_PORT` |

Use the LLM sparingly. Retain and reflect both call it; recall and every CRUD operation do not, so
the bulk of the 89 live operations can be exercised without spending provider quota at all.

---

## Image variant

The compose file pins the image **by digest**, not by tag:

```
ghcr.io/vectorize-io/hindsight@sha256:3edcb6165cefdeaa6721dd0fce43cfd13b7a9c346ce0d2c5f4b4bf7bc3c8ac0b
```

built `2026-09-14`, serving `api_version` **0.10.0**. A tag like `latest` moves, which would make
"the tests passed" unrepeatable — the same suite could run against a different server next week and
nobody would notice. Override with `HINDSIGHT_TEST_IMAGE` only when deliberately testing a different
release.

The pinned image is the **full** variant (~9 GB) because it bundles the local embedding and reranker
models, which keeps the rig self-contained.

To verify the running container matches the pin:

```bash
docker inspect hindsight-sdk-test --format '{{.Image}}'
# -> sha256:e7815cac45c526ebc09d44b94574f9a93a8646c464aaf7a9bf63d8f6d49b9fcb
```

The **slim** variant is ~500 MB but requires external embedding and reranker providers. Switching is
a one-line change — set `HINDSIGHT_TEST_IMAGE` to the slim digest, or:

```yaml
image: ghcr.io/vectorize-io/hindsight:latest-slim
```

It is not the default because it would add a second external dependency to every test run.

---

## What the integration suite should cover

Ordered by what a stub cannot prove. This is the specification for the integration project.

1. **Server-version reconciliation.** Fetch `/version` and `/openapi.json`, assert the running
   version, and skip operations it does not serve. Five operations exist in 0.10.0 but not 0.9.2
   (`attachments/{id}`, `clone`, `prompts/preview`, `transfer/export`, `transfer/import`).
2. **Bank lifecycle end to end.** create → config → retain → recall → reflect → stats → export →
   delete, asserting the state transitions rather than only the status codes.
3. **Retain idempotency.** Same `document_id` twice replaces rather than accumulates. This is
   documented behaviour with real consequences for duplicate memories.
4. **`operation_id` retry semantics.** Re-submitting the same `operation_id` returns the original
   operation and creates no new work. Reusing one that belongs to a different operation is
   documented as `409` — and `409` is *not in the spec*, so this is exactly the kind of claim that
   must be verified rather than assumed.
5. **Async operation lifecycle.** submit → poll `operations/{id}` → terminal state → delete, plus
   cancel on an in-flight operation.
6. **The real error surface.** Provoke `404` on a missing bank (documented: reads of a missing bank
   are `404`, not empty results), `422` on a schema violation, and `401` by presenting a bad
   `authorization` header and seeing what the server actually returns.
7. **Binary downloads.** `attachments/{id}` (`octet-stream`) and `files/download/{key}` (`zip`).
8. **Multipart uploads.** All three, including the multi-file `files/retain`.
9. **Tag-group expression tree.** `And`/`Or`/`Not`/`Leaf` with strict and fuzzy resolution.
10. **Multimodal retain.** The discriminated `text`/`image`/`file` content-block union.
11. **Round-trip fidelity on the wide payloads** — `BankTemplateConfig` (50 properties),
    `RecallResponse`, `ReflectResponse` — asserting that a deserialize/serialize cycle is stable.

Items 1–8 need no LLM calls beyond retain and reflect. Item 10 needs a small image fixture.

---

## Cost discipline

Retain and reflect are the only operations that spend LLM credits. The suite should:

- batch many assertions into as few retain calls as possible,
- use one bank per test class and delete it in teardown,
- keep reflect calls to the few cases where the agentic loop is the thing under test,
- tag the LLM-touching tests so they can be run separately from the free ones.

---

## The reranker model assets

`fetch-reranker-model.sh` downloads the ONNX cross-encoder used by the reranking suite and by the
cross-bank sample. It is separate from the server rig: the reranker runs **in the test process**, not
in the container.

```bash
bash test-infra/fetch-reranker-model.sh int8   # 570 MB model + 22 MB of tokenizer assets
bash test-infra/fetch-reranker-model.sh fp32   # 2.3 GB (graph + external weights)
export HINDSIGHT_RERANKER_DIR=artifacts/reranker
```

- **Nothing needs this to build or to run the unit suites.** The reranking suite skips cleanly when
  the assets are absent, naming this script and the variable in the skip message.
- **Every file is verified against the size the model repository reports**, so a truncated download
  fails here rather than later as an unexplained reranking difference. Partial downloads resume.
- Assets land in `artifacts/`, which is gitignored — they are hundreds of megabytes and are not
  source.
- The coverage gate treats the reranking package's floor as conditional on
  `HINDSIGHT_RERANKER_DIR` being set. With the assets absent it reports the package as **unmeasured**
  rather than passing it, because "the suite could not run" and "the suite passed" are different
  statements.
