# Hindsight HTTP API — verified surface area

Verified against the machine-readable specification, not copied from prose.

Sources:

- `https://hindsight.vectorize.io/openapi.json` — HTTP 200, 570,730 bytes, `openapi: 3.1.0`,
  `info.title` = `Hindsight HTTP API`, `info.version` = `0.10.0`.
- `https://github.com/vectorize-io/hindsight` — `hindsight-docs/static/openapi.json`,
  byte-identical to the published file (paths and components compared structurally: equal).
  The docs site's `/api-reference` page renders version **0.9.2** while the published spec
  says **0.10.0**; the spec is authoritative and is the one mirrored here.
- Vendor clients, cloned at `vendor/hindsight/hindsight-clients/`: Python (generated + hand-written),
  TypeScript (generated + hand-written), Go (generated), Rust (progenitor, build-time).
- Vendor generation pipeline: `scripts/generate-clients.sh`; the OpenAPI file is the single source of truth.

Raw artifacts kept alongside this note:

- `spec/openapi.json` — the specification as downloaded
- `spec/openapi.yaml` — the Go client's copy, kept for cross-checking
- `inventory/operations_full.json` — every operation with parameters, body and response schemas
- `inventory/operations.csv` — the same, tabular
- `inventory/schemas.md` — all 181 component schemas rendered property by property

---

## 1. Headline numbers

| Measure | Value |
| --- | --- |
| Paths | 74 |
| Operations (method x path) | 94 |
| Component schemas | 181 |
| Operations reachable from a component schema | 181 of 181 (no orphans) |
| Operations with a request body | 26 |
| Declared deprecated | 7 |
| Tags (functional areas) | 16 |
| Auth schemes declared in `components.securitySchemes` | 0 |

### Response codes, counted across all 94 operations

| Code | Occurrences | Schema |
| --- | --- | --- |
| 200 | 86 | per-operation success schema; two of them are binary, not JSON |
| 201 | 3 | `CreateKnowledgePageResponse`, `KnowledgeNode`, `WebhookResponse` |
| 202 | 5 | `BankTransferSubmitResponse` (x3), `DocumentExportSubmitResponse`, `DocumentImportSubmitResponse` |
| 404 | 24 | no body schema declared |
| 422 | 88 | `HTTPValidationError` -> `ValidationError[]` |

That is the whole declared error surface. **There is no declared 401, 403, 409, 429 or 5xx
anywhere in the specification** — even though the prose documents a `409` for a reused
`operation_id` and the vendor's own TypeScript client retries `429`/`503`. Any typed error
mapping beyond 404 and 422 is inferred from documentation and vendor code, not from the spec.
This is the same class of gap the TypeSafe notes recorded, and it is worth stating plainly
rather than discovering it during implementation.

---

## 2. Authentication

There are no `components.securitySchemes` and no top-level or per-operation `security` block.
Instead, **88 of the 94 operations declare an optional `authorization` header parameter**:

```
{ "name": "authorization", "in": "header", "required": false,
  "schema": { "anyOf": [ { "type": "string" }, { "type": "null" } ] } }
```

Consequences for a client:

- The key is optional at the transport level, so the client must not require one to construct.
- The header is not marked `required`, so a request without it is well-formed — a deployment
  with the default `TenantExtension` accepts it.
- Prose states the header is `Bearer <key>` and *required if a non-default TenantExtension is
  active*; the server decides, and it answers `401` when it is not satisfied. A client cannot
  pre-validate this.
- Two documented deployment shapes exist: a single-tenant server on `http://localhost:8888`
  with no key, and a hosted multi-tenant server with a key. Both must work.

The vendor's TypeScript client sends `Bearer` in `Authorization`; its `apiKey` option is optional.

---

## 3. Tenancy and the `/v1/default/` prefix

Every resource path is shaped:

```
/v1/{tenant}/banks/{bank_id}/{resource}
```

and the literal in the specification is `default` — 85 of 94 operations carry `{bank_id}`, and
every one of them sits under `/v1/default/`. The tenant segment is fixed in the published spec,
not a parameter. A client should treat it as a configurable base path rather than hardcoding
the literal in 85 places, because a non-default `TenantExtension` deployment changes it.

**`bank_id` is the primary axis of the entire API.** Everything except five health/version/
metrics operations and three file operations is scoped to a bank.

---

## 4. Operations by functional area

### Audit (2)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/audit-logs` | `list_audit_logs` | List audit logs |
| `GET` | `/v1/default/banks/{bank_id}/audit-logs/stats` | `audit_log_stats` | Audit log statistics |

### Bank Templates (3)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/bank-template-schema` | `get_bank_template_schema` | Get bank template JSON Schema |
| `GET` | `/v1/default/banks/{bank_id}/export` | `export_bank_template` | Export bank template |
| `POST` | `/v1/default/banks/{bank_id}/import` | `import_bank_template` | body `BankTemplateManifest`; Import bank template |

### Bank Transfer (3)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `POST` | `/v1/default/banks/{bank_id}/clone` | `clone_bank` | Clone a bank (async) |
| `POST` | `/v1/default/banks/{bank_id}/transfer/export` | `export_bank_transfer` | Export a bank (async) |
| `POST` | `/v1/default/banks/{bank_id}/transfer/import` | `import_bank_transfer` | body `Body_import_bank_transfer`; Import a bank (async) |

### Banks (17)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks` | `list_banks` | List memory banks |
| `PUT` | `/v1/default/banks/{bank_id}` | `create_or_update_bank` | body `CreateBankRequest`; Create or update memory bank |
| `PATCH` | `/v1/default/banks/{bank_id}` | `update_bank` | body `CreateBankRequest`; Partial update memory bank |
| `DELETE` | `/v1/default/banks/{bank_id}` | `delete_bank` | Delete memory bank |
| `POST` | `/v1/default/banks/{bank_id}/background` | `add_bank_background` | **deprecated**; body `AddBackgroundRequest`; Add/merge memory bank background (removed — use PATCH .../config) |
| `GET` | `/v1/default/banks/{bank_id}/config` | `get_bank_config` | Get bank configuration |
| `PATCH` | `/v1/default/banks/{bank_id}/config` | `update_bank_config` | body `BankConfigUpdate`; Update bank configuration |
| `DELETE` | `/v1/default/banks/{bank_id}/config` | `reset_bank_config` | Reset bank configuration |
| `POST` | `/v1/default/banks/{bank_id}/consolidate` | `trigger_consolidation` | body `['ConsolidationRequest', 'null']`; Trigger consolidation |
| `POST` | `/v1/default/banks/{bank_id}/consolidation/recover` | `recover_consolidation` | Recover failed consolidation |
| `POST` | `/v1/default/banks/{bank_id}/health/llm` | `test_bank_llm` | Test the bank's LLM connectivity |
| `DELETE` | `/v1/default/banks/{bank_id}/observations` | `clear_observations` | Clear all observations |
| `GET` | `/v1/default/banks/{bank_id}/profile` | `get_bank_profile` | **deprecated**; Get memory bank profile (removed — use GET .../config) |
| `PUT` | `/v1/default/banks/{bank_id}/profile` | `update_bank_disposition` | **deprecated**; body `UpdateDispositionRequest`; Update memory bank disposition (removed — use PATCH .../config) |
| `POST` | `/v1/default/banks/{bank_id}/prompts/preview` | `preview_prompt` | body `PromptPreviewRequest`; Preview an operation's prompts (no LLM call) |
| `GET` | `/v1/default/banks/{bank_id}/stats` | `get_agent_stats` | Get statistics for memory bank |
| `GET` | `/v1/default/banks/{bank_id}/stats/memories-timeseries` | `get_memories_timeseries` | Memory ingestion time-series |

### Directives (5)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/directives` | `list_directives` | List directives |
| `POST` | `/v1/default/banks/{bank_id}/directives` | `create_directive` | body `CreateDirectiveRequest`; Create directive |
| `GET` | `/v1/default/banks/{bank_id}/directives/{directive_id}` | `get_directive` | Get directive |
| `PATCH` | `/v1/default/banks/{bank_id}/directives/{directive_id}` | `update_directive` | body `UpdateDirectiveRequest`; Update directive |
| `DELETE` | `/v1/default/banks/{bank_id}/directives/{directive_id}` | `delete_directive` | Delete directive |

### Document Transfer (4)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/document-transfer` | `export_documents_sync_removed` | **deprecated**; Export documents (removed — use POST .../document-transfer/export) |
| `POST` | `/v1/default/banks/{bank_id}/document-transfer` | `import_documents` | **deprecated**; body `Body_import_documents`; Import documents (async) |
| `POST` | `/v1/default/banks/{bank_id}/document-transfer/export` | `export_documents` | **deprecated**; Export documents (async) |
| `GET` | `/v1/default/files/download/{key}` | `download_file` | Download a stored file (async export archive) |

### Documents (7)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/documents` | `list_documents` | List documents |
| `GET` | `/v1/default/banks/{bank_id}/documents/{document_id}` | `get_document` | Get document details |
| `PATCH` | `/v1/default/banks/{bank_id}/documents/{document_id}` | `update_document` | body `UpdateDocumentRequest`; Update document |
| `DELETE` | `/v1/default/banks/{bank_id}/documents/{document_id}` | `delete_document` | Delete a document |
| `GET` | `/v1/default/banks/{bank_id}/documents/{document_id}/chunks` | `list_document_chunks` | List document chunks |
| `POST` | `/v1/default/banks/{bank_id}/documents/{document_id}/reprocess` | `reprocess_document` | Reprocess document |
| `GET` | `/v1/default/chunks/{chunk_id}` | `get_chunk` | Get chunk details |

### Entities (4)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/entities` | `list_entities` | List entities |
| `GET` | `/v1/default/banks/{bank_id}/entities/graph` | `get_entity_graph` | Get entity co-occurrence graph |
| `GET` | `/v1/default/banks/{bank_id}/entities/{entity_id}` | `get_entity` | Get entity details |
| `POST` | `/v1/default/banks/{bank_id}/entities/{entity_id}/regenerate` | `regenerate_entity_observations` | **deprecated**; Regenerate entity observations (deprecated) |

### Files (1)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `POST` | `/v1/default/banks/{bank_id}/files/retain` | `file_retain` | body `Body_file_retain`; Convert files to memories |

### Knowledge Base (8)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/knowledge-base/export` | `export_knowledge_base` | Export the knowledge base as a markdown bundle |
| `POST` | `/v1/default/banks/{bank_id}/knowledge-base/folders` | `create_knowledge_folder` | body `CreateFolderRequest`; Create a knowledge-base folder |
| `PATCH` | `/v1/default/banks/{bank_id}/knowledge-base/nodes/{node_id}` | `update_knowledge_node` | body `UpdateNodeRequest`; Rename/move a knowledge-base node or update a page's options |
| `DELETE` | `/v1/default/banks/{bank_id}/knowledge-base/nodes/{node_id}` | `delete_knowledge_node` | Delete a knowledge-base node |
| `POST` | `/v1/default/banks/{bank_id}/knowledge-base/pages` | `create_knowledge_page` | body `CreatePageRequest`; Create a knowledge-base page |
| `GET` | `/v1/default/banks/{bank_id}/knowledge-base/pages/{page_id}` | `get_knowledge_page` | Get a knowledge-base page |
| `GET` | `/v1/default/banks/{bank_id}/knowledge-base/search` | `search_knowledge_base` | Hybrid search over knowledge pages (BM25 + vector) |
| `GET` | `/v1/default/banks/{bank_id}/knowledge-base/tree` | `get_knowledge_base_tree` | Get the knowledge-base tree |

### LLM Traces (2)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/llm-requests` | `list_llm_requests` | List LLM request traces |
| `GET` | `/v1/default/banks/{bank_id}/llm-requests/stats` | `llm_request_stats` | LLM request statistics |

### Memory (14)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/attachments/{attachment_id}` | `get_bank_attachment` | Fetch an attachment retained inline with a document |
| `GET` | `/v1/default/banks/{bank_id}/graph` | `get_graph` | Get memory graph data |
| `POST` | `/v1/default/banks/{bank_id}/memories` | `retain_memories` | body `RetainRequest`; Retain memories |
| `DELETE` | `/v1/default/banks/{bank_id}/memories` | `clear_bank_memories` | Clear memory bank memories |
| `POST` | `/v1/default/banks/{bank_id}/memories/dry-run-extract` | `dry_run_extract_memories` | body `DryRunExtractRequest`; Dry-run fact extraction (preview, no persistence) |
| `GET` | `/v1/default/banks/{bank_id}/memories/list` | `list_memories` | List memory units |
| `POST` | `/v1/default/banks/{bank_id}/memories/recall` | `recall_memories` | body `RecallRequest`; Recall memory |
| `GET` | `/v1/default/banks/{bank_id}/memories/{memory_id}` | `get_memory` | Get memory unit |
| `PATCH` | `/v1/default/banks/{bank_id}/memories/{memory_id}` | `update_memory` | body `UpdateMemoryRequest`; Curate memory unit |
| `GET` | `/v1/default/banks/{bank_id}/memories/{memory_id}/history` | `get_observation_history` | Get observation history |
| `DELETE` | `/v1/default/banks/{bank_id}/memories/{memory_id}/observations` | `clear_memory_observations` | Clear observations for a memory |
| `GET` | `/v1/default/banks/{bank_id}/observations/scopes` | `list_observation_scopes` | List observation scopes |
| `POST` | `/v1/default/banks/{bank_id}/reflect` | `reflect` | body `ReflectRequest`; Reflect and generate answer |
| `GET` | `/v1/default/banks/{bank_id}/tags` | `list_tags` | List tags |

### Mental Models (9)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/mental-models` | `list_mental_models` | List mental models |
| `POST` | `/v1/default/banks/{bank_id}/mental-models` | `create_mental_model` | body `CreateMentalModelRequest`; Create mental model |
| `GET` | `/v1/default/banks/{bank_id}/mental-models/{mental_model_id}` | `get_mental_model` | Get mental model |
| `PATCH` | `/v1/default/banks/{bank_id}/mental-models/{mental_model_id}` | `update_mental_model` | body `UpdateMentalModelRequest`; Update mental model |
| `DELETE` | `/v1/default/banks/{bank_id}/mental-models/{mental_model_id}` | `delete_mental_model` | Delete mental model |
| `POST` | `/v1/default/banks/{bank_id}/mental-models/{mental_model_id}/clear` | `clear_mental_model` | Clear mental model content |
| `POST` | `/v1/default/banks/{bank_id}/mental-models/{mental_model_id}/dry-run-refresh` | `dry_run_refresh_mental_model` | Dry-run mental model refresh (preview, no persistence) |
| `GET` | `/v1/default/banks/{bank_id}/mental-models/{mental_model_id}/history` | `get_mental_model_history` | Get mental model history |
| `POST` | `/v1/default/banks/{bank_id}/mental-models/{mental_model_id}/refresh` | `refresh_mental_model` | Refresh mental model |

### Monitoring (5)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/health` | `health_endpoint_health_get` | Health check endpoint |
| `GET` | `/health/live` | `get_liveness` | Liveness probe |
| `GET` | `/health/ready` | `get_readiness` | Readiness probe |
| `GET` | `/metrics` | `metrics_endpoint_metrics_get` | Prometheus metrics endpoint |
| `GET` | `/version` | `get_version` | Get API version and feature flags |

### Operations (5)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/operations` | `list_operations` | List async operations |
| `GET` | `/v1/default/banks/{bank_id}/operations/{operation_id}` | `get_operation_status` | Get operation status |
| `DELETE` | `/v1/default/banks/{bank_id}/operations/{operation_id}` | `cancel_operation` | Cancel a pending or in-flight async operation |
| `DELETE` | `/v1/default/banks/{bank_id}/operations/{operation_id}/delete` | `delete_operation` | Delete a terminal async operation |
| `POST` | `/v1/default/banks/{bank_id}/operations/{operation_id}/retry` | `retry_operation` | Retry a failed async operation |

### Webhooks (5)

| Method | Path | operationId | Notes |
| --- | --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/webhooks` | `list_webhooks` | List webhooks |
| `POST` | `/v1/default/banks/{bank_id}/webhooks` | `create_webhook` | body `CreateWebhookRequest`; Register webhook |
| `PATCH` | `/v1/default/banks/{bank_id}/webhooks/{webhook_id}` | `update_webhook` | body `UpdateWebhookRequest`; Update webhook |
| `DELETE` | `/v1/default/banks/{bank_id}/webhooks/{webhook_id}` | `delete_webhook` | Delete webhook |
| `GET` | `/v1/default/banks/{bank_id}/webhooks/{webhook_id}/deliveries` | `list_webhook_deliveries` | List webhook deliveries |

---

## 5. Request body shapes

26 operations take a body. 23 are `application/json`; three are `multipart/form-data`:

| Operation | Content type | Fields |
| --- | --- | --- |
| `POST /v1/default/banks/{bank_id}/document-transfer` | multipart | `file` (string) |
| `POST /v1/default/banks/{bank_id}/transfer/import` | multipart | `file` (string) |
| `POST /v1/default/banks/{bank_id}/files/retain` | multipart | `files` (array), `request` (string) |

Two operations return non-JSON: `GET .../attachments/{attachment_id}` returns
`application/octet-stream` and `GET /v1/default/files/download/{key}` returns `application/zip`.
A client must therefore expose a streaming/binary download path, not just JSON.

---

## 6. Enums and closed sets the client must model

Only **one** schema in the entire specification is a bare enum — `Budget` (`low|mid|high`).
Every other closed set is an inline `enum` on a string property. These are the ones that
matter, and a client that types them as open strings loses the ability to validate:

| Field | Values | Where |
| --- | --- | --- |
| `budget` | `low`, `mid`, `high` | recall, reflect |
| `tags_match` | `any`, `all`, `exact` | recall/reflect/directives/tag-group lists |
| `TagGroupLeaf.match` | `any`, `all`, `any_strict`, `all_strict`, `exact` | tag-group leaves |
| `TagGroupLeaf.resolve` | `exact`, `fuzzy` | tag-group leaves |
| `detail` | `metadata`, `content`, `full` | mental models, knowledge pages |
| `fact type` | `world`, `experience`, `observation` | recall `types`, memory units |
| `retain_extraction_mode` | `concise`, `verbose`, `custom`, `verbatim`, `chunks` | bank config |
| label group `type` | `value`, `multi-values`, `text`, `multi-text`, `map` | bank config `entity_labels` |
| content block `type` | `text`, `image`, `file` | retain `MemoryItem.content` (discriminated) |
| entity label `match` | `any`, `all`, `exact` | `LabelGroup` query filters |

`MemoryItem.content` is a **discriminated union** (`discriminator.propertyName = "type"`) over
`TextContentBlock` | `ImageContentBlock` | `FileContentBlock` — a multimodal retain path that a
client has to model explicitly, not flatten to `string`.

---

## 7. Where the specification is loose, and what that costs a typed client

These are the places a strict C#/source-generated client has to make a decision, recorded
here because they are the ones that produce defects later.

1. **OpenAPI 3.1 null unions.** Nullability is expressed as `anyOf: [{type: X}, {type: null}]`
   throughout rather than 3.0's `nullable: true`. A generator must understand 3.1 `anyOf` or it
   will emit non-nullable types for genuinely nullable fields.
2. **`type: object` with no properties.** At least 12 responses declare `application/json` with
   an entirely empty schema. There is nothing to bind to; the honest client shape is a raw
   `JsonElement` or a documented empty type, not an invented empty class.
3. **Undeclared error statuses.** 401/403/409/429/5xx appear only in prose and vendor code.
4. **Mixed path types.** `ValidationError.loc` is `array<string|integer>` and `input` is
   untyped — the same shape TypeSafe's notes called out, and the same converter problem.
5. **Two response media types** (`octet-stream`, `zip`) sit alongside JSON on the same client.
6. **Deprecated-but-live operations.** Seven are marked deprecated in their summary while the
   replacements are named inline (`use PATCH .../config`). Two pairs are duplicates of live
   routes (`/profile` vs `/config`, `/background` vs `PATCH /config`).
7. **No `servers` block.** The spec declares no base URL, so the default must come from
   documentation (`http://localhost:8888` per the quick start) and be overridable.

---

## 8. Vendor clients already in existence

| Client | Location | Generation | Notes |
| --- | --- | --- | --- |
| Python | `hindsight-clients/python` | `openapi-generator-cli` v7.10.0 in Docker -> `hindsight_client_api`, wrapped by a hand-written `Hindsight` class (3,119 lines) | full low-level surface + curated high-level facade |
| TypeScript | `hindsight-clients/typescript` | `@hey-api/openapi-ts` 0.97.3 -> `generated/`, wrapped by hand-written `src/index.ts` (2,084 lines) | same two-layer shape |
| Go | `hindsight-clients/go` | `openapi-generator-cli` v7.10.0 | 219 `.go` files, generated models + API classes |
| Rust | `hindsight-clients/rust` | `progenitor` at build time via `build.rs` | generated from the same spec |

Both hand-written wrappers converge on the same operational set — `retain` / `retainBatch` /
`retainFiles`, `recall`, `reflect`, `listMemories`, bank lifecycle, directives, mental models,
knowledge base, documents, and bank export/import/clone. That curated set is a defensible
starting point for the high-level .NET facade, while the full 94-operation surface stays
reachable underneath.

Notable behaviours **not** in the spec, taken from the vendor's TypeScript client, which a
parity-minded .NET client should match:

- Retry only on `429` and `503`, only for **idempotent** operations (`recall`, `reflect`) —
  synchronous `retain` is deliberately excluded because its `operation_id` is ignored there
  and a retry could duplicate a write.
- `Retry-After` is honoured, with a 500 ms doubling fallback, and the wait is **full-jitter**
  (`random() * wait`) so a synchronised burst does not replay the spike.
- Abort/cancellation is observable during the wait, not only around the request.
- `HindsightError` carries `statusCode` and `details`.
- `User-Agent` is `hindsight-client-typescript/<version>` and is overridable by integrations.

---

## 9. What 100% surface coverage actually requires

Stated as a checklist, because "100% of the API surface" is otherwise ambiguous.
**Superseded by section 12**, which folds in the decisions taken; kept here so the reasoning is
visible rather than overwritten.

- **94 operations**, each with a typed request and a typed response.
- **181 component schemas**, all reachable, all modelled.
- **The two binary operations** returning `octet-stream` and `zip`.
- **The three multipart operations** including the multi-file `files/retain`.
- **All 42 distinct query parameters** and 13 path parameters (`bank_id`, plus `attachment_id`,
  `chunk_id`, `directive_id`, `document_id`, `entity_id`, `key`, `memory_id`, `mental_model_id`,
  `node_id`, `operation_id`, `page_id`, `webhook_id`).
- **The discriminated multimodal content union**.
- **The tag-group expression tree** (`TagGroupAnd|Or|Not|Leaf`, input and output variants).
- **The 422 `HTTPValidationError` shape** with its mixed-type `loc`.
- **7 deprecated operations**, either implemented or explicitly and deliberately omitted with
  a recorded reason — silently skipping them is what makes a "100%" claim false.

---

## 10. Live-server reconciliation (version 0.9.2 vs 0.10.0)

The published specification is **0.10.0**, but the Hindsight instance this host actually talks to
serves **0.9.2**. That difference is small and, importantly, one-directional:

| | 0.9.2 (served by the reference server) | 0.10.0 (published spec) |
| --- | --- | --- |
| Paths | 69 | 74 |
| Operations | 89 | 94 |
| Component schemas | 157 | 181 |

`0.9.2` is a **strict subset** of `0.10.0`. Nothing was removed between the two — no operation and no
schema exists in 0.9.2 that is absent from 0.10.0. The five operations added in 0.10.0 are:

```
GET  /v1/default/banks/{bank_id}/attachments/{attachment_id}
POST /v1/default/banks/{bank_id}/clone
POST /v1/default/banks/{bank_id}/prompts/preview
POST /v1/default/banks/{bank_id}/transfer/export
POST /v1/default/banks/{bank_id}/transfer/import
```

plus 24 schemas (`BankTransferSubmitResponse`, the multimodal content blocks, the graph node/edge
types, the prompt-preview types, and others).

**Consequence for the client:** a client generated against 0.10.0 is a superset of 0.9.2's surface,
so it can talk to either — but the five new operations will `404` against a 0.9.2 server. The client
should therefore not treat those five as universally available. The clean way to handle this is the
server's own `/version` endpoint, which reports `features` flags the client can read before deciding
what is safe to call:

```json
{"api_version":"0.9.2",
 "features":{"observations":true,"mcp":true,"worker":true,"bank_config_api":true,
             "bank_llm_health":false,"file_upload_api":true,"document_export_api":true,
             "document_import_api":true,"audit_log":false,"llm_trace":true,
             "store_document_text":true}}
```

Two things worth noting from that real response, because they contradict the documented "everything
on" assumption: the reference server runs with the **worker enabled** (`worker: true` — so async
retain and background consolidation do execute) but with **`bank_llm_health` and `audit_log`
disabled**, so those endpoints are present in the spec and unavailable in practice. Feature-flag
awareness is not optional for an honest integration suite.

---

## 11. Decisions taken

| # | Decision |
| --- | --- |
| D1 | Root namespace and package id: **`Vectorize.Hindsight.Sdk`**. Chosen over `Hindsight.Sdk` because `Hindsight` collides with the Hermes host's own memory integration. |
| D2 | **Deprecated operations are out of scope.** Seven operations are excluded deliberately and by name, not skipped. Live surface for coverage purposes: **87 operations**. |
| D3 | **Fully generated surface.** All 87 live operations are exposed with typed requests and responses. No curated subset, no facade-only layer. |
| D4 | **Repeatable test infrastructure**, including deep integration tests against a live server. See [`../test-infra/README.md`](../test-infra/README.md). |

### The seven excluded operations

Recorded explicitly so the coverage claim is auditable rather than approximate:

| Method | Path | Replacement, per its own summary |
| --- | --- | --- |
| `GET` | `/v1/default/banks/{bank_id}/profile` | `GET .../config` |
| `PUT` | `/v1/default/banks/{bank_id}/profile` | `PATCH .../config` |
| `POST` | `/v1/default/banks/{bank_id}/background` | `PATCH .../config` |
| `GET` | `/v1/default/banks/{bank_id}/document-transfer` | `POST .../document-transfer/export` |
| `POST` | `/v1/default/banks/{bank_id}/document-transfer` | (import path superseded by `transfer/import`) |
| `POST` | `/v1/default/banks/{bank_id}/document-transfer/export` | `POST .../transfer/export` |
| `POST` | `/v1/default/banks/{bank_id}/entities/{entity_id}/regenerate` | none declared; marked deprecated |

The replacements for all but the last are themselves live operations in the 87, so no capability is
lost by excluding these.

---

## 12. Coverage definition, final

"100% of the API surface" now means, concretely:

- **87 live operations** — 94 minus the 7 deprecated.
- **181 component schemas** — all modelled, including the 24 that 0.9.2 does not serve.
- Both **binary** responses and all three **multipart** requests.
- **42 query parameters** and **13 path parameters**.
- The discriminated **multimodal content union** and the **tag-group expression tree**.
- The **422 `HTTPValidationError`** shape with its mixed-type `loc`.
- **Feature-flag gating** via `/version` for the five 0.10.0-only operations.
- **Live verification** of the statuses the spec does not declare (401/403/409/429/5xx).
