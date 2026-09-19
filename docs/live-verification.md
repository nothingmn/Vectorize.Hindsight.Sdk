# Live verification — what the specification does not tell you

Everything in this file was **observed against a running server**, not read from documentation. The
instance is the local rig described in [`../test-infra/README.md`](../test-infra/README.md), serving
`api_version` **0.10.0**.

Where a claim here contradicts the specification, the observation wins and the contradiction is the
point of the note.

---

## 1. The served spec is smaller than the published spec, at the same version string

| | Served by the rig | Published |
| --- | --- | --- |
| `info.version` | 0.10.0 | 0.10.0 |
| Paths | 71 | 74 |
| Operations | 91 | 94 |
| Component schemas | 179 | 181 |

Same version, different surface. Three operations are absent from the served document **and** return
nothing useful if called, because their backing feature is off:

```
POST /v1/default/banks/{bank_id}/clone
POST /v1/default/banks/{bank_id}/transfer/export
POST /v1/default/banks/{bank_id}/transfer/import
```

Two schemas go with them: `BankTransferSubmitResponse`, `Body_import_bank_transfer`.

**Consequence:** generating a client from the published spec produces code for three operations a
default deployment does not serve. That is fine provided availability is decided at run time from
`/version` — see below. It is not fine to assume the published spec describes what is deployed.

---

## 2. Feature flags gate route *registration*, not just behaviour — and inconsistently

`/version` reports a `features` map. Its flags do not behave uniformly.

**A flag that removes the route's usefulness while leaving it registered as `200` in the spec:**

```json
{"api_version":"0.10.0","features":{"...":"...","bank_llm_health":false}}
```

```
POST /v1/default/banks/{bank_id}/health/llm
-> 404 {"detail":"Bank LLM health check is disabled. Set HINDSIGHT_API_ENABLE_BANK_LLM_HEALTH=true to enable."}
```

The published spec declares this operation as **`200`** with a `BankLlmHealthResponse`. It answers
`404`. A client that trusts the spec will map that to "bank not found", which is wrong and
misleading to a caller.

**A flag that does not gate its route at all:**

```json
"audit_log": false
```

```
GET /v1/default/banks/{bank_id}/audit-logs -> 200
```

The route answers normally despite the flag being `false`, so it gates something narrower than the
route itself.

**Consequence:** `/version` cannot be used as a simple "is this operation available" oracle. The
client needs per-operation knowledge, and the integration suite must assert real status codes rather
than infer them from flags.

The rig's flags, observed:

```json
{"observations":true,"mcp":true,"worker":true,"bank_config_api":true,"bank_llm_health":false,
 "file_upload_api":true,"document_export_api":true,"document_import_api":true,"audit_log":false,
 "llm_trace":true,"store_document_text":true}
```

---

## 3. The undeclared status codes, as actually returned

The published spec declares only `404` and `422`. Here is what the server really sends.

| Status | Provoked by | Body | In spec? |
| --- | --- | --- | --- |
| `404` | `GET .../banks/{missing}/stats` | `{"detail":"Bank 'x' not found"}` | status yes, **schema no** |
| `422` | `POST .../memories/recall` with `{}` | `{"detail":[{"type":"missing","loc":["body","query"],"msg":"Field required","input":{}}]}` | status yes, schema yes |
| `405` | `POST .../banks/{id}/stats` (wrong method) | `{"detail":"Method Not Allowed"}` | **no** |
| `404` | calling a feature-disabled operation | `{"detail":"... is disabled. Set ...=true to enable."}` | **no** (declared `200`) |

Two details worth having, because they are exactly the shapes a typed client must bind:

- The `404` for a missing bank carries a `detail` **string**, while the `422` carries `detail` as an
  **array of objects**. The same key is two different types depending on status. A client that
  binds `detail` to one type will fail on the other.
- The real `422` confirms the mixed-type `loc` and untyped `input` from the spec:
  `"loc":["body","query"]` (string segments) and `"input":{}` (an object, not a string).

---

## 4. Authentication is genuinely not enforced by default — and 401 cannot be provoked

```
GET /v1/default/banks
  Authorization: Bearer totally-invalid-key-xyz
-> 200 {"banks":[],"total":0,"limit":100,"offset":0}
```

A deliberately invalid key was **accepted**. In the default deployment there is no auth layer at
all: the `authorization` header is declared optional, and the server does not read it.

**Consequence:** `401` and `403` cannot be verified against the default rig. They require a
deployment with a non-default `TenantExtension` active. Any client-side handling of 401/403 is
therefore *unverified* against this instance and must be labelled as such — the integration suite
should skip those assertions with an explicit reason rather than pretend to cover them.

This also means the local rig is safe by construction: there is no credential to leak from it.

---

## 5. There is no request-id, trace-id, or rate-limit header

Every response carries exactly:

```
HTTP/1.1 200 OK
date: ...
server: uvicorn
content-length: ...
content-type: application/json
```

No `x-request-id`. No `trace-id`. No `retry-after`. No rate-limit headers.

**Consequence, and it is a significant divergence from the TypeSafe client's design:** there is no
server-supplied correlation handle to attach to a log line or a span. The TypeSafe SDK could put
`x-typesafe-request-id` on every exception and every span because the server sent one. Here there is
nothing, so client-side correlation has to be generated locally and cannot be reconciled with server
logs. Any "trace id" the client emits is its own invention and should be named so it is not mistaken
for server-side.

`Retry-After` never appearing also means the client's retry logic cannot depend on it being present;
it must have a computed fallback that is exercised in practice, not just in a branch.

---

## 6. The `409` is real, but it is not the one the documentation describes

This section is a correction. An earlier pass concluded that `409` "did not reproduce" for a reused
`operation_id`. That conclusion was wrong, and the way it was wrong is worth recording: the probe
tested one path, found no `409`, and generalised to "the collision case was not observed". A later,
wider probe found the `409` on a different route.

**What was tested first (and still returns `200`):**

```
POST .../memories  {"items":[{"content":"probe alpha"}],"async":true,
                    "operation_id":"1111...5555"}          -> 200, operation created
POST .../memories  {"items":[{"content":"probe beta totally different"}],"async":true,
                    "operation_id":"1111...5555"}          -> 200, same operation_id echoed
```

Resubmitting the same `operation_id` with different content did **not** produce a `409`. The
documented collision rule is therefore not enforced at submit time — or at least not by that state
transition.

**What does produce a `409`, deterministically:**

```
POST .../operations/{operation_id}/retry      (on a batch_retain parent whose
                                               children have all completed)
-> 409
   {"detail":"Operation <id> is a batch_retain parent with no incomplete sub-batches
              to retry (its children have all completed, or it has none). Resubmit the
              source documents to re-ingest them, then delete this record via
              DELETE /operations/<id>/delete."}
```

Repeating the call returns `409` again, so it is a state-based rejection rather than a race.

Consequences, and they are not small:

- **`409` is a real status on this API and the specification declares it nowhere.** The published
  spec lists only `404` and `422`. A client that mapped only the declared statuses would report a
  retry rejection as a generic 400 — which is exactly the defect this library had and fixed (see
  `HindsightUnexpectedStatusException` in the notes below).
- The body is a useful, actionable message: it names the alternative (`DELETE .../delete`) rather
  than only refusing. That is worth surfacing to the caller, and it is preserved in
  `ResponseBody`.
- **A single negative probe is not evidence of absence.** The first conclusion generalised from one
  path to a whole status. Recording "not observed on this path" is honest; recording "does not
  happen" was not, and the difference cost a correction later.

This is now covered by the integration suite, and `HindsightConflictException` maps it.

---

## 7. Async operation lifecycle, verified

Retain with `async: true` returns immediately and creates real, inspectable state.

```
POST .../memories {async: true}  -> 200
  {"success":true,"bank_id":"...","items_count":1,"async":true,
   "operation_id":"<uuid>","operation_ids":null,"usage":null}
```

Two things to note at once:

- `operation_ids` is `null` while `operation_id` is populated. Both exist; they are not synonyms.
- `usage` is `null` for an async submit — token usage is not known until the work completes.

`GET .../operations` returns a paginated envelope:
`{"bank_id","total","limit","offset","operations":[...]}` — note the array is `operations`, and the
envelope is `total/limit/offset`, not the `cursor`-style pagination used elsewhere.

`GET .../operations/{id}` on a parent returns substantially more than the list view:

```json
{"operation_id","status","operation_type","created_at","updated_at","completed_at",
 "error_message","retry_count","next_retry_at","progress","details",
 "result_metadata":{"is_parent":true,"items_count":1,"total_tokens":2,"num_sub_batches":1},
 "child_operations":[{"operation_id","status","sub_batch_index","items_count","error_message"}]}
```

The parent/child split is real: `async: true` on a batch produces one parent plus one child per
sub-batch. A client that only models the flat list view will miss `child_operations` and
`result_metadata` entirely.

---

## 8. An upstream LLM failure does not surface as an HTTP error

The single most important operational finding. With the provider refusing work, the async retain
still returned **`200`**, and the failure appeared buried in the child operation:

```
child_operations[0].error_message:
  "RuntimeError: Fact extraction failed: 1/1 chunks failed. First failures:
   chunk 0: RateLimitError: Error code: 429 - {'error': {...'you have reached your weekly
   usage limit'...}}"
```

The parent `status` remained `"pending"` at the time of observation, with the child carrying the
error string.

**Consequence:** for async retain, a provider outage or quota exhaustion is **not** a request
failure. The client sees `200` and must poll operations to discover that extraction never happened.
A client — and equally a test suite — that treats `200` as "the memory was stored" will silently
record nothing. This is a real trap, and it is precisely the kind of thing a stubbed transport can
never reveal.

---

## 9. Banks are auto-created by writes, and `404` on reads

Confirmed as documented: posting to a bank that does not exist creates it. `probe-bank` appeared in
`GET /v1/default/banks` with default disposition:

```json
{"bank_id":"probe-bank","name":"probe-bank",
 "disposition":{"skepticism":3,"literalism":3,"empathy":3},
 "mission":"","fact_count":0,"last_document_at":null,"last_write_at":null}
```

Note the bank list entry carries `disposition` inline, while `mission` and the disposition fields are
also settable through `PATCH .../config`. The old `/profile` route exists but is deprecated.

Reads of a missing bank answer `404` rather than an empty result, which is the documented and
deliberate behaviour — it is what makes a typo'd `bank_id` visible instead of looking like a healthy
empty bank.

`DELETE .../banks/{id}` returns
`{"success":true,"message":"Bank 'x' and all associated data deleted successfully","deleted_count":0}`.
Note `deleted_count` can be `0` on a fully successful delete — it counts something other than "did
the delete work", so a client must not treat `0` as failure.

---

## 10. Operational constraint on this machine

**The Ollama API key available to this host has reached its weekly usage limit.** The provider's own
message, surfaced through the retry path:

> `you (robchartier) have reached your weekly usage limit, upgrade for higher limits`

Consequences for the integration suite:

- Operations that call the LLM — **retain, reflect, consolidate, mental-model refresh, knowledge-page
  content generation, dry-run extract** — cannot be exercised until the quota resets or credits are
  added.
- Everything else runs freely: bank CRUD and config, documents listing, entities, directives, mental
  model CRUD (without refresh), knowledge-base structure, operations, webhooks, tags, stats, health
  and version, and all read paths. That is well over half of the 87 live operations and needs no
  provider at all.
- The rig is otherwise proven working: it starts, serves `0.10.0`, auto-creates banks, records
  operations, and returns correct `404`/`405`/`422`. Only the LLM-backed work is blocked, and it is
  blocked by quota, not by the rig.

The suite must therefore be split so the free tests run always and the LLM-backed tests are
explicitly tagged and skipped when quota is unavailable — not silently failing.

---

## 11. The bank-config write surface is smaller than the published spec's

Found while verifying the typed bank-configuration surface against the rig, and the same class of
discrepancy as §1 — a field the published specification declares that the deployed build will not
accept.

Two fields are present on `BankTemplateConfig` in the published 0.10.0 spec and are **rejected by
the running server as unknown**:

```
knowledge_page_default_trigger
reflect_default_options
```

The rejection is a `400` whose body lists the fields the server does accept, so it is self-describing
rather than a silent drop:

> `Unknown configuration fields: ['knowledge_page_default_trigger', 'reflect_default_options'].`
> `Valid configurable fields: ['consolidation_llm_batch_size', ... ]...`

The server's authoritative list — `HindsightConfig.get_configurable_fields()` — holds **50** fields,
and the published spec's `BankTemplateConfig` holds the same **50**, with **no difference in either
direction**. So the spec and the current source agree; it is the *served build* that is behind both,
by exactly those two fields. This is not a spec error and not a client error.

**Consequence for the typed surface:** the typed patch models the whole declared surface, including
the two fields this build has not yet grown. That is the correct direction — a client that modelled
only what one particular build accepts would be wrong the moment the server is upgraded, and the
field list is regenerated from the spec rather than hand-maintained. A caller who sets one of the two
against this build gets the server's own `400`, naming the field, rather than a silent no-op or a
client-side guess.

The remaining 48 fields were verified end to end against the rig: written through the typed patch,
read back through the typed read model, with nested values (`observation_scope_limits`,
`llm_gemini_safety_settings`, `memory_defense`, `retain_strategies`) round-tripping intact, a single
override cleared without disturbing its siblings, and the map overload still reaching a raw key.

**A read/write asymmetry this surfaced.** The first version of the read model typed
`retain_strategies` from the *write* model, so reading a strategy handed back
`BankConfigValue<int>` where a caller expects `int?` — the ordinary read
`strategies["documents"].RetainChunkSize == 800` did not compile. The two positions are now
generated from separate type maps, and a regression test asserts that no read member exposes the
write-side tri-state.
