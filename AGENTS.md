# AGENTS.md — using Vectorize.Hindsight.Sdk

Instructions for an LLM agent (or a human) writing code against this library. Everything here is
verified against the real public surface of this repository, not invented.

**Audience:** an agent that has been asked to add long-term memory to a .NET application, and needs
to know exactly which calls to make, in what order, and which mistakes are silent.

**Contents:** [1 What this is](#1-what-this-is) · [2 Install](#2-install) ·
[3 The five-minute version](#3-the-five-minute-version) · [4 The mental model](#4-the-mental-model) ·
[5 Core operations](#5-core-operations) · [6 Async and the 200 trap](#6-async-and-the-200-trap) ·
[7 Errors](#7-errors) · [8 Authentication](#8-authentication) ·
[9 Dependency injection](#9-dependency-injection) · [10 Multi-bank recall](#10-multi-bank-recall) ·
[11 Reranking](#11-reranking) · [12 Mental models](#12-mental-models) ·
[13 Full operation index](#13-full-operation-index) · [14 Admin CLI](#14-admin-cli) ·
[15 Testing without a server](#15-testing-without-a-server) · [16 Traps](#16-traps) ·
[17 Task recipes](#17-task-recipes)

---

## 1. What this is

A .NET 10 client SDK for the **Hindsight** HTTP API (`hindsight.vectorize.io`) — a memory service
that stores facts, documents, and knowledge for LLM applications, and retrieves them by semantic
recall.

| | |
| --- | --- |
| Target | `net10.0` |
| Language | C# 13+, nullable enabled, warnings as errors |
| API version implemented | Hindsight **0.10.0** (OpenAPI 3.1, pinned in `spec/openapi.json`) |
| Surface coverage | **87 of 87** non-deprecated operations |
| Async style | fully async, every operation takes a `CancellationToken` |
| Not affiliated with | Vectorize (this is an independent client) |

**Packages** — install only what you need. The core library has no inference dependency.

| Package | Purpose |
| --- | --- |
| `Vectorize.Hindsight.Sdk` | Everything. Start here. |
| `Vectorize.Hindsight.Sdk.DependencyInjection` | `AddHindsight(...)`, `IConfiguration` binding, reranker registration |
| `Vectorize.Hindsight.Sdk.Admin` | Bridges the `hindsight-admin` CLI (database backup, restore, bank import/export) |
| `Vectorize.Hindsight.Sdk.Reranking.Onnx` | Local ONNX cross-encoder reranker (CPU/GPU) |
| `Vectorize.Hindsight.Sdk.Reranking.TypeSafe` | Reranking via TypeSafe System One |

---

## 2. Install

```bash
dotnet add package Vectorize.Hindsight.Sdk
# optional, only if you need them:
dotnet add package Vectorize.Hindsight.Sdk.DependencyInjection
dotnet add package Vectorize.Hindsight.Sdk.Reranking.Onnx
```

Packages are **not published to nuget.org** by this repository's CI — it packs, verifies, and
attaches them as build artifacts. Publishing is a deliberate manual act. To consume a local build,
see `eng/consume_package.sh`.

---

## 3. The five-minute version

```csharp
using Vectorize.Hindsight.Sdk;

using var client = new HindsightClient("http://localhost:8888");

// 1. Write. Note the required wrapper object: bodies are `{ items: [...] }`, not a bare array.
await client.RetainMemoriesAsync("my-bank", new RetainRequest
{
    Items = [ new MemoryItem { Content = "Rob prefers tabs over spaces." } ]
}, cancellationToken);

// 2. Read.
RecallResponse recall = await client.RecallMemoriesAsync("my-bank", new RecallRequest
{
    Query = "what does Rob prefer for indentation?"
}, cancellationToken);

foreach (RecallResult r in recall.Results)
    Console.WriteLine($"{r.Text}   (score {r.Scores?.Final})");

// 3. Synthesize across many memories rather than returning a list.
ReflectResponse answer = await client.ReflectAsync("my-bank", new ReflectRequest
{
    Query = "What are Rob's formatting conventions?"
}, cancellationToken);
```

There is no `First`, `Best`, or `TopOne` on any result. A recall returns an ordered, scored set; which
one constitutes "the answer" is the caller's (or the model's) decision.

---

## 4. The mental model

Four concepts cover almost everything:

| Concept | What it is | Key calls |
| --- | --- | --- |
| **Bank** | An isolated memory store. The unit of tenancy, permission, and lifecycle. | `CreateOrUpdateBankAsync`, `ListBanksAsync` |
| **Memory / fact** | A retained statement, extracted and embedded server-side. | `RetainMemoriesAsync`, `ListMemoriesAsync`, `RecallMemoriesAsync` |
| **Document** | A source file whose chunks are retrievable; memories can cite it. | `FileRetainAsync`, `ListDocumentsAsync`, `GetDocumentAsync` |
| **Mental model** | A living, server-maintained summary of a bank. Rebuilt over time. | `CreateMentalModelAsync`, `GetMentalModelAsync`, `RefreshMentalModelAsync` |

Plus: **directives** (standing instructions that shape what gets extracted), **knowledge base**
(curated pages, distinct from auto-extracted memory), **entities** (a graph of people/things),
**webhooks**, **operations** (async job records), and **LLM traces** (a per-bank request audit).

**Choose a bank id, and be consistent.** Bank ids are strings chosen by the caller (`"my-bank"`,
`"obsidian_rob"`). The bank must exist before you retain into it — `CreateOrUpdateBankAsync` is
idempotent, so calling it first is always safe.

---

## 5. Core operations

### Retain — write memories

```csharp
Task<RetainResponse> RetainMemoriesAsync(string bankId, RetainRequest body, CancellationToken ct)
```

`RetainRequest`: `Items` (**required**, `List<MemoryItem>`), `Async` (`bool?`), `OperationId`
(`string?`), `DocumentTags` (*deprecated — do not use*).

```csharp
await client.RetainMemoriesAsync("my-bank", new RetainRequest
{
    Items =
    [
        new MemoryItem { Content = "Alice Nakamura is a principal engineer in Vancouver." },
        new MemoryItem { Content = "Bob transferred to the Berlin office in March." },
    ]
}, ct);
```

> **The body is wrapped.** `RetainRequest` has `Items`; you cannot pass a bare list. This is the
> single most common compile-time surprise, and it mirrors the HTTP body exactly.

**`Async` defaults to `false`.** Read [§6](#6-async-and-the-200-trap) before relying on either value.

### Recall — retrieve by relevance

```csharp
Task<RecallResponse> RecallMemoriesAsync(string bankId, RecallRequest body, CancellationToken ct)
```

`RecallRequest`: `Query` (**required**), plus `Budget`, `MaxTokens`, `Types`,
`PreferObservations`, `Trace`, `QueryTimestamp`, `Include`, `Tags`, `TagsMatch`, `TagGroups`,
`MinScores`, `TemporalWindow`.

```csharp
RecallResponse recall = await client.RecallMemoriesAsync("my-bank", new RecallRequest
{
    Query = "who works in Vancouver?",
    MaxTokens = 2000,
    Tags = ["project:agentry"],       // tags are OR'd unless TagsMatch is set
    TagsMatch = "and",                // set this, or expect project-wide bleed
}, ct);
```

> **Tag trap.** Supplying `Tags` without `TagsMatch` **ORs** them. A query scoped to
> `project:x, thread:90` will return records tagged only `thread:15`. Set `TagsMatch = "and"`
> whenever you mean "all of these".

`TagsMatch` accepts `"and"` / `"or"` (plus the negation forms the API supports).

### Reflect — synthesize an answer

```csharp
Task<ReflectResponse> ReflectAsync(string bankId, ReflectRequest body, CancellationToken ct)
```

`ReflectRequest`: `Query` (**required**), plus `Budget`, `MaxTokens`, `Context`, `Include`,
`ResponseSchema`, `Tags`, `TagsMatch`, `TagGroups`, `FactTypes`, `ApplyAllDirectives`,
`ExcludeMentalModels`, `ExcludeMentalModelIds`, and the two `ReflectSearchObservations*` knobs.

Use `ReflectAsync` when you want prose; use `RecallMemoriesAsync` when you want the evidence.
`ResponseSchema` constrains the shape of the answer.

### Single memory and listing

```csharp
Task<JsonElement>                       GetMemoryAsync(string bankId, string memoryId, CancellationToken ct)
Task<ListMemoryUnitsResponse>           ListMemoriesAsync(string bankId, ListMemoriesQuery? query, CancellationToken ct)
Task<JsonElement>                       UpdateMemoryAsync(string bankId, string memoryId, UpdateMemoryRequest body, CancellationToken ct)
Task<JsonElement>                       GetObservationHistoryAsync(string bankId, string memoryId, CancellationToken ct)
Task<ClearMemoryObservationsResponse>   ClearMemoryObservationsAsync(string bankId, string memoryId, CancellationToken ct)
```

Return types of `JsonElement` mean the API's response for that operation has no stable schema in the
spec, so the raw JSON is handed back rather than a model that would invent structure. Read it with
`GetProperty` / `TryGetProperty`.

### Documents

```csharp
Task<FileRetainResponse>      FileRetainAsync(...)          // upload a file and retain its chunks
Task<ListDocumentsResponse>   ListDocumentsAsync(string bankId, ListDocumentsQuery? query, CancellationToken ct)
Task<DocumentResponse>        GetDocumentAsync(string bankId, string documentId, CancellationToken ct)
Task<UpdateDocumentResponse>  UpdateDocumentAsync(...)
Task<DeleteDocumentResponse>  DeleteDocumentAsync(string bankId, string documentId, CancellationToken ct)
Task<ListChunksResponse>      ListDocumentChunksAsync(...)
Task<ChunkResponse>           GetChunkAsync(...)
Task<ReprocessDocumentResponse> ReprocessDocumentAsync(...)
Task<Stream>                  DownloadFileAsync(...)         // NOT JSON — returns a stream
Task<Stream>                  GetBankAttachmentAsync(...)    // NOT JSON — returns a stream
```

> Two operations return a raw `Stream`, not a model: `DownloadFileAsync` (a ZIP) and
> `GetBankAttachmentAsync` (octet-stream). **Dispose them.** These are the only non-JSON responses in
> the API.

### Banks

```csharp
Task<BankListResponse>    ListBanksAsync(ListBanksQuery? query, CancellationToken ct)
Task<BankProfileResponse> CreateOrUpdateBankAsync(string bankId, CreateBankRequest body, CancellationToken ct)
Task<BankProfileResponse> UpdateBankAsync(string bankId, CreateBankRequest body, CancellationToken ct)
Task<DeleteResponse>      DeleteBankAsync(string bankId, CancellationToken ct)
Task<BankConfigResponse>  GetBankConfigAsync(string bankId, CancellationToken ct)
Task<BankStatsResponse>   GetAgentStatsAsync(string bankId, GetAgentStatsQuery? query, CancellationToken ct)
Task<MemoriesTimeseriesResponse> GetMemoriesTimeseriesAsync(...)
Task<PromptPreviewResponse> PreviewPromptAsync(string bankId, PromptPreviewRequest body, CancellationToken ct)
Task<BankLlmHealthResponse> TestBankLlmAsync(string bankId, CancellationToken ct)
Task<ConsolidationResponse> TriggerConsolidationAsync(string bankId, CancellationToken ct)
```

### Health and version

```csharp
Task<LivenessResponse>          GetLivenessAsync(CancellationToken ct)
Task<JsonElement>               GetReadinessAsync(CancellationToken ct)
Task<VersionResponse>           GetVersionAsync(CancellationToken ct)
```

`GetVersionAsync` is the cheapest way to prove connectivity and check what you are talking to.

---

## 6. Async and the 200 trap

> **This is the most important section in this document.**

**HTTP 200 does not mean the memory was stored.**

Several operations are asynchronous server-side. A `200` means *the request was accepted*. The real
outcome — including LLM and quota failures — appears only in the operation record:

```csharp
RetainResponse accepted = await client.RetainMemoriesAsync("my-bank", new RetainRequest
{
    Items = [...],
    Async = true,
}, ct);

// `accepted` is a receipt, not a result. Poll it.
OperationStatusResponse status;
do
{
    await Task.Delay(500, ct);
    status = await client.GetOperationStatusAsync("my-bank", accepted.OperationId, null, ct);
} while (status.Status is "pending" or "running");

// NOW check for failure — the HTTP call already succeeded long ago.
if (!string.IsNullOrEmpty(status.ErrorMessage))
    logger.LogError("operation failed: {Error}", status.ErrorMessage);

foreach (ChildOperationStatus child in status.ChildOperations ?? [])
    if (!string.IsNullOrEmpty(child.ErrorMessage))
        logger.LogError("child {Id} failed: {Error}", child.OperationId, child.ErrorMessage);
```

Rules to follow:

1. **Treat `RetainResponse` as a receipt.** Store the `OperationId`.
2. **Poll `GetOperationStatusAsync`** and inspect **`ChildOperations[].Error`** — a top-level
   success can still contain failed children.
3. **`Async = true` returns immediately with an operation id.** `Async = false` is synchronous but
   still subject to server-side limits.
4. There is **no request-id or trace header** — the operation id is your only correlation handle.
5. For a fire-and-forget write you still care about, the only defensible pattern is: submit, record
   the id durably, poll later.

`RefreshMentalModelAsync` follows the same submit-then-poll shape and returns
`AsyncOperationSubmitResponse`. Use `DryRunRefreshMentalModelAsync` to see what a refresh would do
without doing it.

---

## 7. Errors

All exceptions derive from `HindsightException`. Catch the specific ones; do not catch `Exception`.

| Exception | When | What to do |
| --- | --- | --- |
| `HindsightConfigurationException` | Bad options (e.g. missing base address) | Fix the configuration; do not retry |
| `HindsightValidationException` | Client-side validation failed before sending | Fix the request; do not retry |
| `HindsightBadRequestException` | HTTP 400 | Fix the request |
| `HindsightAuthenticationException` | HTTP 401 | Provide/refresh the API key |
| `HindsightPermissionDeniedException` | HTTP 403 | Wrong bank or insufficient scope |
| `HindsightNotFoundException` | HTTP 404 | Resource is gone — often the bank |
| `HindsightConflictException` | HTTP 409 | Concurrent modification; re-read and retry |
| `HindsightRequestValidationException` | HTTP 422 (`ValidationError[]`) | Inspect `ValidationErrorDetail`; fix the payload |
| `HindsightRateLimitException` | HTTP 429 | Honour `Retry-After` |
| `HindsightServerException` | HTTP 5xx | Retryable |
| `HindsightOverloadedException` | HTTP 503 | Retryable |
| `HindsightConnectionException` | Transport failure | Retryable |
| `HindsightProtocolException` | Response did not match the contract | Report; usually a version mismatch |
| `HindsightUnexpectedStatusException` | A status the client does not model | Report |

```csharp
try
{
    await client.RetainMemoriesAsync(bank, request, ct);
}
catch (HindsightRequestValidationException ex)
{
    foreach (ValidationErrorDetail d in ex.Errors ?? [])   // 422 body, modelled
        logger.LogWarning("{Field}: {Message}", d.Location, d.Message);
}
catch (HindsightRateLimitException ex) when (ex.RetryAfter is { } wait)
{
    await Task.Delay(wait, ct);
}
catch (HindsightException ex)
{
    logger.LogError(ex, "hindsight call failed");
}
```

**Retries are built in.** The pipeline retries idempotent operations on `429`/`503`, honours
`Retry-After`, and uses full-jitter backoff. Tune with `MaxAttempts`, `InitialRetryDelay`,
`MaxRetryDelay`, `MaxRetryAfter`, `RetryJitterFraction`. **Synchronous retain is excluded from
retry** — replaying a write that may have partially landed is not safe.

---

## 8. Authentication

```csharp
using var client = new HindsightClient(new HindsightClientOptions
{
    BaseAddress = "https://hindsight.example.com",
    ApiKey      = Environment.GetEnvironmentVariable("HINDSIGHT_API_KEY"),
    Tenant      = "default",
});
```

- `ApiKey` — a static key.
- `ApiKeyProvider` (`IApiKeyProvider`) — for rotating/async sources. Implement it when the key comes
  from a secret manager or needs refresh.
- `Tenant` — defaults to `"default"` and appears in every route as `/v1/{tenant}/...`.

> **Never hardcode a key.** Read it from configuration or the environment. Keys must not be logged,
> written to disk, or committed.

**A default deployment does not enforce auth** (the spec declares `authorization` as optional on 88
of 94 operations and defines no security schemes). Do not conclude from a successful unauthenticated
call that auth is unnecessary in production — it means that server has it disabled.

---

## 9. Dependency injection

```csharp
builder.Services.AddHindsight(options =>
{
    options.BaseAddress = builder.Configuration["Hindsight:BaseAddress"]!;
    options.ApiKeyProvider = new MySecretStoreKeyProvider();
});
```

```csharp
builder.Services.AddHindsightFromConfiguration(builder.Configuration);                  // "Hindsight" section
builder.Services.AddHindsightFromConfiguration(builder.Configuration, "Hindsight:Primary");  // named section
```

Binds `BaseAddress`, `Tenant`, `ApiKey`, `MaxAttempts`, `Timeout`, and the retry knobs.

`AddHindsight` registers `HindsightClient` as a disposable singleton backed by `IHttpClientFactory`.
Resolve `HindsightClient` and inject it — do not `new` one per call.

**Warm the connection** (optional, avoids a first-call stall):

```csharp
// A static call, NOT an extension method:
VersionResponse v = await HindsightClientWarmup.WarmAsync(serviceProvider, ct);
```

It throws rather than swallowing: a cancelled token raises `OperationCanceledException`, and a
transport failure raises the library's own exception for that condition. A catch block here would
erase the distinction between "the server is not up yet" and "the key is wrong", which is the only
reason the check is worth making.

**Reranker registration** — selection is a registration, never a `switch`:

```csharp
services.AddReranker<MyRerankerFactory>();   // your IRerankerFactory
services.AddReranker(factoryInstance);
services.AddIdentityReranker();              // explicit no-op default

IReranker reranker = services.ResolveReranker();
IReranker r2 = services.ResolveReranker(out string? reason);   // reason when it fell back
```

`AddHindsight` deliberately does **not** pre-register the identity reranker. An eager default made
every later explicit registration a silent no-op (it registered with `TryAdd`), so a host asking for
a real reranker silently received the no-op one. `ResolveReranker` falls back instead.

---

## 10. Multi-bank recall

When relevant information spans several banks, query them **concurrently** and merge. Do not query
banks in sequence, and do not assume their scores are comparable.

```csharp
using Vectorize.Hindsight.Sdk.CrossBank;

CrossBankRecallResult result = await client.RecallAcrossBanksAsync(
    query:   "What did we decide about the migration?",
    bankIds: ["project-bank", "team-bank", "personal-bank"],
    options: new CrossBankRecallOptions
    {
        PerBankLimit = 10,                        // default 10 — stops one big bank dominating
        MergeOrder   = CrossBankMergeOrder.InterleavedByRank,   // this is the default
    },
    cancellationToken);

// Partial failure is first-class. CHECK IT.
if (result.IsPartial)
    logger.LogWarning("banks failed: {Banks}",
        string.Join(", ", result.Failures.Select(f => $"{f.BankId} ({f.Failure?.Message})")));

foreach (CrossBankCandidate c in result.Candidates)
    Console.WriteLine($"{c.BankId}  rank {c.Rank}  retrieval {c.RetrievalScore}  rerank {c.RerankScore}\n{c.Text}");
```

Overload for per-bank control:

```csharp
Task<CrossBankRecallResult> RecallAcrossBanksAsync(
    string query, IReadOnlyList<CrossBankTarget> targets,
    CrossBankRecallOptions? options, CancellationToken ct)

new CrossBankTarget
{
    BankId       = "authoritative-bank",
    PerBankLimit = 5,
    PolicyWeight = 2.0,                          // recorded on the candidate, NEVER applied
    Request      = new RecallRequest { Query = "..." },   // per-bank override
}
```

### Merge orders

| `CrossBankMergeOrder` | Behaviour | When |
| --- | --- | --- |
| `InterleavedByRank` | **default** — A1, B1, C1, A2, B2, C2… | Always safe. Assumes nothing about score comparability. |
| `ScoreDescending` | Sorts by raw retrieval score | Only when banks demonstrably share configuration |
| `BankOrder` | Caller's bank order, then each bank's own ranking | When bank order *is* the policy |

### Reading the result

`CrossBankRecallResult`: `Candidates`, `Banks` (`CrossBankOutcome` per bank), `BankCount`,
`SucceededCount`, `Failures`, `IsPartial`, `AllBanksSucceeded`, `ContributingBanks`,
`CandidatePoolSize`, `DuplicatesMerged`, `RerankApplied`, `RerankFailure`.

`CrossBankCandidate`: `BankId`, `Text`, `Rank`, `RetrievalScore`, `RerankScore`, `PolicyWeight`,
`Source` (the original `RecallResult`), `SeenInBanks`, `IsDuplicateAcrossBanks`.

`CrossBankRecallResult.ToContext()` renders all candidates with a source marker per passage, **and
names any bank that could not be reached** — a model told only about the banks that answered would
present a partial picture as the whole one. Prefer it over hand-formatting: a failure cannot then be
silently dropped from the model's context.

**Decision rules:**

- `RerankApplied == true` → candidates are already relevance-sorted. **Do not sort again.**
  `RerankScore` is populated.
- `RerankApplied == false` → all `RerankScore` are `null` and the interleaved merge order holds.
  **Null is meaningful**, not missing data.
- On reranker failure, `RerankFailure` is set and the merge order is preserved — the call does not
  throw away results because the reranker failed.
- `RerankScore` is **ordinal, not calibrated.** Order by it; do not threshold on it.

### Provenance

Every candidate keeps `BankId` from retrieval through to output, so you can trace an answer to its
source, apply per-bank policy, or resolve conflicts. `PolicyWeight` is **recorded and never applied**
— a weight meaning "intentional policy" and one meaning "compensating for score differences" are the
same arithmetic, so the library refuses to guess.

### Identity, not semantics

Duplicates are merged on **identity only** (`chunk_id` / `document_id`), never on semantic
similarity. `SeenInBanks` lists every bank a candidate appeared in.

> **Trap:** a `chunk_id` has the form `{bank_id}_{document_id}_{n}` — it embeds the bank. So a dedup
> key built on `chunk_id` can never match across banks. Dedup uses document identity instead.

> **Trap:** recalling from a bank that does not exist returns **HTTP 200 with empty results**, not
> 404. An empty result therefore proves nothing. `CrossBankOutcome.IsAbsentFromResults` flags this.

---

## 11. Reranking

Reranking is the **second stage**: stage one asks "what is relevant inside this bank?", stage two
asks "of everything retrieved from every bank, what is most useful for this query?".

```csharp
IReranker reranker = ...;                                  // from DI, or construct directly
using var client = new HindsightClient(options, logger, reranker);
```

Or construct a provider directly:

```csharp
// ONNX cross-encoder (bge-reranker-v2-m3). Needs model assets — see the package README.
var reranker = new OnnxReranker(new OnnxRerankerOptions
{
    ModelPath = "/models/bge-reranker-v2-m3/model_int8.onnx",
    TokenizerJsonPath = "/models/bge-reranker-v2-m3/tokenizer.json",   // see the package README
});
```

| Provider | Package | Notes |
| --- | --- | --- |
| `IdentityReranker` | core (default) | Pure no-op, preserves merge order. `IsIdentity == true`. **Skipped, not run** — costs nothing, and never reported as a successful rerank. |
| `OnnxReranker` | `.Reranking.Onnx` | Local cross-encoder. CPU works; GPU needs a CUDA-compatible runtime. |
| `TypeSafeChoiceReranker` | `.Reranking.TypeSafe` | One Choice question ranks all candidates via a probability distribution. Needs an `IChoiceAsker`. |

**Implementing `IReranker`:**

```csharp
public interface IReranker
{
    bool IsIdentity => false;                                  // true == makes no judgement
    Task<IReadOnlyList<double>> RerankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken ct);
}
```

- Return **one score per document, aligned by index**. The count is checked; a mismatch is a failure,
  because scores silently misaligned to passages look plausible and are worse than no reranker.
- Honour the `CancellationToken` and bound your own concurrency.
- Higher = more relevant, comparable **within one call only**.

`IdentityReranker` is the default so no call site needs a null check, and `IsIdentity` distinguishes
"no judgement was made" from "relevance was evaluated" — the single distinction this stage exists to
provide.

---

## 12. Mental models

A mental model is a **living summary** the server maintains and rebuilds over time. It is the right
input for driving decisions (which bank applies, how relevant is this) without inventing prompting.

```csharp
Task<MentalModelListResponse>      ListMentalModelsAsync(string bankId, ListMentalModelsQuery? query, CancellationToken ct)
Task<MentalModelResponse>          GetMentalModelAsync(string bankId, string mentalModelId, GetMentalModelQuery? query, CancellationToken ct)
Task<CreateMentalModelResponse>    CreateMentalModelAsync(string bankId, CreateMentalModelRequest body, CancellationToken ct)
Task<MentalModelResponse>          UpdateMentalModelAsync(string bankId, string mentalModelId, UpdateMentalModelRequest body, CancellationToken ct)
Task<AsyncOperationSubmitResponse> RefreshMentalModelAsync(string bankId, string mentalModelId, CancellationToken ct)
Task<MentalModelDryRunRefreshResult> DryRunRefreshMentalModelAsync(string bankId, string mentalModelId, CancellationToken ct)
Task<JsonElement>                  GetMentalModelHistoryAsync(string bankId, string mentalModelId, CancellationToken ct)
Task<MentalModelResponse>          ClearMentalModelAsync(string bankId, string mentalModelId, CancellationToken ct)
Task<JsonElement>                  DeleteMentalModelAsync(string bankId, string mentalModelId, CancellationToken ct)
```

**Three things to know:**

1. **By ID is native. By name is not.** `GetMentalModelAsync` takes an id. The list operation filters
   only on `tags`, `tags_match`, `detail`, `limit`, `offset` — **there is no `name` parameter.** To
   look up by name, list and match client-side on `MentalModelResponse.Name`. On duplicates, take the
   first.

   ```csharp
   async Task<MentalModelResponse?> ByNameAsync(string bankId, string name, CancellationToken ct)
   {
       MentalModelListResponse all = await client.ListMentalModelsAsync(
           bankId, new ListMentalModelsQuery { Detail = "content" }, ct);

       return all.Items.FirstOrDefault(m =>
           string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
   }
   ```

2. **`detail` defaults to `metadata`.** The content is not returned unless you ask:
   `new GetMentalModelQuery { Detail = "content" }` or `new ListMentalModelsQuery { Detail = "content" }`.
   Fetching metadata and wondering why the text is empty is a common wasted round trip.

3. **`RefreshMentalModelAsync` is asynchronous** — it returns an operation to poll
   ([§6](#6-async-and-the-200-trap)). Prefer `DryRunRefreshMentalModelAsync` first.

**Cache them, but refresh on the server's signal, not a timer.** Each model carries `is_stale` and
`last_refreshed_at`, plus a `min_refresh_interval_seconds` governing how often a refresh is
permitted. Refresh when the server says stale — but note **stale is the normal state**, so `is_stale`
alone cannot be the trigger or you would refetch constantly. The `min_refresh_interval_seconds`
interval is the throttle.

> **Sizing trap.** Mental models are documents, not passages — real ones run to thousands of
> characters. A cross-encoder with a 512-token limit will **truncate most of one**, losing exactly
> the discriminating detail. If you feed a mental model to a reranker-style model, chunk it or embed
> it separately; do not assume it fits.

---

## 13. Full operation index

All 87 operations, grouped as the generated surface groups them. Every method takes a trailing
`CancellationToken`.

<details>
<summary><b>Banks (13)</b></summary>

`ListBanksAsync` · `CreateOrUpdateBankAsync` · `UpdateBankAsync` · `DeleteBankAsync` ·
`GetBankConfigAsync` · `UpdateBankConfigAsync` · `ResetBankConfigAsync` · `GetAgentStatsAsync` ·
`GetMemoriesTimeseriesAsync` · `PreviewPromptAsync` · `TestBankLlmAsync` ·
`TriggerConsolidationAsync` · `RecoverConsolidationAsync` · `ClearObservationsAsync`
</details>

<details>
<summary><b>Memory (14)</b></summary>

`RetainMemoriesAsync` · `RecallMemoriesAsync` · `ReflectAsync` · `GetMemoryAsync` ·
`UpdateMemoryAsync` · `ListMemoriesAsync` · `ClearBankMemoriesAsync` ·
`GetObservationHistoryAsync` · `ClearMemoryObservationsAsync` · `ListObservationScopesAsync` ·
`DryRunExtractMemoriesAsync` · `ListTagsAsync` · `GetGraphAsync` · `GetBankAttachmentAsync`
</details>

<details>
<summary><b>Documents (7)</b></summary>

`ListDocumentsAsync` · `GetDocumentAsync` · `UpdateDocumentAsync` · `DeleteDocumentAsync` ·
`ListDocumentChunksAsync` · `GetChunkAsync` · `ReprocessDocumentAsync`
</details>

<details>
<summary><b>Mental models (9)</b></summary>

`ListMentalModelsAsync` · `GetMentalModelAsync` · `CreateMentalModelAsync` ·
`UpdateMentalModelAsync` · `DeleteMentalModelAsync` · `ClearMentalModelAsync` ·
`RefreshMentalModelAsync` · `DryRunRefreshMentalModelAsync` · `GetMentalModelHistoryAsync`
</details>

<details>
<summary><b>Directives (5)</b></summary>

`ListDirectivesAsync` · `GetDirectiveAsync` · `CreateDirectiveAsync` · `UpdateDirectiveAsync` ·
`DeleteDirectiveAsync`
</details>

<details>
<summary><b>Knowledge base (8)</b></summary>

`GetKnowledgeBaseTreeAsync` · `GetKnowledgePageAsync` · `CreateKnowledgePageAsync` ·
`CreateKnowledgeFolderAsync` · `UpdateKnowledgeNodeAsync` · `DeleteKnowledgeNodeAsync` ·
`SearchKnowledgeBaseAsync` · `ExportKnowledgeBaseAsync`
</details>

<details>
<summary><b>Entities (3)</b></summary>

`ListEntitiesAsync` · `GetEntityAsync` · `GetEntityGraphAsync`
</details>

<details>
<summary><b>Operations (5)</b></summary>

`ListOperationsAsync` · `GetOperationStatusAsync` · `CancelOperationAsync` ·
`RetryOperationAsync` · `DeleteOperationAsync`
</details>

<details>
<summary><b>Webhooks (5)</b></summary>

`ListWebhooksAsync` · `CreateWebhookAsync` · `UpdateWebhookAsync` · `DeleteWebhookAsync` ·
`ListWebhookDeliveriesAsync`
</details>

<details>
<summary><b>Bank transfer &amp; templates (7)</b></summary>

`ExportBankTransferAsync` · `ImportBankTransferAsync` · `CloneBankAsync` ·
`ExportBankTemplateAsync` · `ImportBankTemplateAsync` · `GetBankTemplateSchemaAsync` ·
`DownloadFileAsync`
</details>

<details>
<summary><b>Audit &amp; LLM traces (4)</b></summary>

`ListAuditLogsAsync` · `AuditLogStatsAsync` · `ListLlmRequestsAsync` · `LlmRequestStatsAsync`
</details>

<details>
<summary><b>Monitoring (5)</b></summary>

`GetLivenessAsync` · `GetReadinessAsync` · `GetVersionAsync` · `HealthEndpointHealthGetAsync` ·
`MetricsEndpointMetricsGetAsync`
</details>

**Excluded (7):** deprecated operations are deliberately not implemented (`POST /document-transfer`
among them). Everything else is present.

---

## 14. Admin CLI

Database backup, restore, migrations, bank import/export, and worker control are **not HTTP routes** —
they live in a CLI inside the Hindsight container. This package bridges it.

```csharp
using Vectorize.Hindsight.Sdk.Admin;

using var admin = new HindsightAdminClient(new HindsightAdminOptions
{
    Executable = "docker",                       // or the in-container path
    // LauncherPrefixArguments carries the container launch, e.g.:
    //   ["exec", "-i", "hindsight", "/app/api/.venv/bin/hindsight-admin"]
    // Executable defaults to "hindsight-admin".
});
admin.Options.LauncherPrefixArguments.Add("exec");
admin.Options.LauncherPrefixArguments.Add("hindsight");
admin.Options.LauncherPrefixArguments.Add("/app/api/.venv/bin/hindsight-admin");

AdminResult backup = await admin.BackupAsync("/backups/hs.dump", schema: null, ct);
if (!backup.Succeeded) logger.LogError("{Output}", backup.StandardOutput);

await admin.BackupAsync(outputPath: "/backups/hs.dump", schema: null, ct);
await admin.RestoreAsync(inputPath: "/backups/hs.dump", schema: null, confirm: true, ct);
await admin.RunDatabaseMigrationAsync(schema: null, embeddingDimension: null,
                                      skipExtensionReconcile: false, ct);
await admin.ExportBankAsync(bankId: "my-bank", outputPath: "/backups/bank.zip",
                            schema: null, includeHistory: true, ct);
await admin.ImportBankAsync(archivePath: "/backups/bank.zip", schema: null,
                            targetBankId: null, includeHistory: true, ct);
await admin.RepairBankAsync(bankId: "my-bank", allBanks: false, schema: null,
                            dryRun: true, ct);          // dryRun first
await admin.GetWorkerStatusAsync(schema: null, ct);
await admin.DecommissionWorkerAsync(workerId: "w1", schema: null,
                                    confirm: true, ct);   // confirm: true required
await admin.DecommissionWorkersAsync(schema: null, confirm: true, ct);
AdminResult any = await admin.RunAsync(
    new AdminCommand { Subcommand = "some-verb", Arguments = ["--flag"] }, ct);
AdminResult must = await admin.RunOrThrowAsync(command, ct);   // throws HindsightAdminException
```

`AdminResult`: `ExitCode` (0 = success, `2` = usage error, `1` = declined), plus `StandardOutput`,
`StandardError`, `Elapsed`, and computed helpers `Succeeded`, `IsUsageError`, `WasDeclined`,
`BackedUpRows`, `RepairTotals`.

> **Interactive commands need confirmation.** `restore` and `decommission-*` prompt unless
> `--yes` is passed. `RunOrThrowAsync` is the safer default — an exit code is easy to ignore.
>
> These operations are **destructive**. Never run `restore` or `decommission-*` without explicit
> human approval, and never point them at a bank you have not confirmed.

---

## 15. Testing without a server

The repo ships a repeatable rig and the tests skip themselves cleanly when no server is present.

```bash
bash test-infra/up.sh          # isolated Hindsight on 127.0.0.1:8888, empty database
bash test-infra/down.sh --purge
bash eng/ci_local.sh           # every CI gate, in CI order, from committed state
```

Point integration tests at any server with the usual environment variables. `HttpHindsightTransport`
is an interface (`IHindsightTransport`) — implement it to fake the wire entirely for unit tests:

```csharp
using var client = new HindsightClient(new HindsightClientOptions
{
    BaseAddress = "http://localhost",
    Transport   = new MyFakeTransport(),
});
```

---

## 16. Traps

The failures that look like success. Each was found by running against a live server.

| Trap | Reality | Guard |
| --- | --- | --- |
| **200 means stored** | A 200 means *accepted*. LLM/quota errors surface only in `GetOperationStatusAsync` → `ChildOperations[].Error`. | Poll the operation |
| **Empty result means nothing there** | Recall against a **nonexistent bank returns 200 empty**, not 404. | Check `CrossBankOutcome.IsAbsentFromResults`; verify the bank exists |
| **`Tags` filters** | Supplying `Tags` without `TagsMatch` **ORs** them — project-wide bleed. | `TagsMatch = "and"` |
| **Scores are comparable across banks** | Each bank scores within its own pipeline. A 0.9 elsewhere is not this 0.9. | Use `InterleavedByRank`, or a reranker |
| **`chunk_id` is a cross-bank dedup key** | It embeds the bank (`{bank_id}_{document_id}_{n}`), so it can never match across banks. | Dedup on document identity |
| **Rerank scores are calibrated** | They are **ordinal**. A quantized model compresses into a narrow band while getting order right. | Order by it; never threshold |
| **`RerankApplied == false` means scores are missing** | All `RerankScore` are `null` **deliberately** — no scoring happened. | Branch on `RerankApplied` |
| **`detail` defaults to content** | It defaults to `metadata`. Content is opt-in. | `Detail = "content"` |
| **Mental models can be looked up by name** | No `name` filter exists on the list operation. | List and match client-side |
| **Mental models fit a 512-token model** | They are documents — thousands of characters. | Chunk or embed separately |
| **An identity reranker still reran** | It is **skipped**, and never reported as a successful rerank. | Branch on `IsIdentity` / `RerankApplied` |
| **`PolicyWeight` influences ordering** | It is **recorded, never applied**. | Apply your own weighting |
| **A retry is always safe** | Synchronous retain is deliberately excluded from retry — a partially-landed write must not be replayed blindly. | Use `Async = true` + idempotency |
| **`DownloadFileAsync` returns a model** | It returns a raw `Stream` (ZIP), as does `GetBankAttachmentAsync`. | Dispose it |

---

## 17. Task recipes

### "Add memory to my app"

```csharp
// Startup
builder.Services.AddHindsight(o => o.BaseAddress = cfg["Hindsight:BaseAddress"]!);

// Write (durably recorded async submit — see §6)
public async Task RememberAsync(string text, CancellationToken ct)
{
    await client.RetainMemoriesAsync(_bankId, new RetainRequest
    {
        Items = [new MemoryItem { Content = text }],
        Async = true,
    }, ct);
}

// Read
public async Task<IReadOnlyList<string>> RecallAsync(string query, CancellationToken ct)
{
    RecallResponse r = await client.RecallMemoriesAsync(_bankId, new RecallRequest
    {
        Query = query, MaxTokens = 2000,
    }, ct);

    return r.Results.Select(m => m.Text ?? "").ToList();
}
```

### "Search across several banks"

```csharp
CrossBankRecallResult r = await client.RecallAcrossBanksAsync(query, bankIds, new CrossBankRecallOptions
{
    PerBankLimit = 10,
    Order = CrossBankMergeOrder.InterleavedByRank,
}, ct);

if (r.IsPartial)
    logger.LogWarning("{Failed} of {Total} banks failed", r.Failures.Count, r.BankCount);

string context = r.ToContext();   // provenance-marked, and discloses failed banks
```

`CrossBankRecallResult.ToContext()` renders candidates for a prompt and **discloses partial loss** —
prefer it over manual formatting so a failure cannot be silently dropped from the model's context.

### "Use a mental model to decide something"

```csharp
MentalModelListResponse models = await client.ListMentalModelsAsync(
    bankId, new ListMentalModelsQuery { Detail = "content" }, ct);

MentalModelResponse? purpose = models.Items
    .FirstOrDefault(m => m.Name == "bank-purpose-and-goals");   // first match wins

if (purpose?.Content is { Length: > 0 } description)
{
    // Feed `description` to your own classifier/LLM as grounding.
    // Cache it; refresh on the server's is_stale signal, throttled by
    // min_refresh_interval_seconds.
}
```

### "Verify connectivity before doing work"

```csharp
try
{
    VersionResponse v = await client.GetVersionAsync(ct);
    logger.LogInformation("hindsight {Version} at {Address}", v.ApiVersion, client.Options.BaseAddress);
}
catch (HindsightConnectionException)
{
    // Fail fast and visibly rather than on the first real call.
}
```

---

## Rules that are not negotiable

1. **Never commit an API key.** Read from configuration or environment.
2. **Never treat HTTP 200 as proof of storage.** Poll the operation.
3. **Never assume an empty result means absence** — a missing bank returns 200 empty.
4. **Never sort reranked results again**; branch on `RerankApplied`.
5. **Never threshold on a rerank score**; they are ordinal.
6. **Never assume cross-bank scores are comparable**; the default merge order exists for that reason.
7. **Never silently drop a failed bank**; surface `IsPartial` and `Failures`.
8. **Always honour the `CancellationToken`.**
9. **Always dispose** the client and any returned `Stream`.
10. **Confirm before destructive admin operations** (`restore`, `decommission-*`).

## Where to read more

| Document | Contents |
| --- | --- |
| `README.md` | Full API notes, decisions, and an explicit "not verified" section |
| `docs/api-surface.md` | Verified reconnaissance of the HTTP surface |
| `docs/choosing-a-reranker.md` | Reranker comparison, timings, and how to obtain each ONNX model |
| `docs/reranker-live-verification.md` | Live reranker results against a real server |
| `docs/admin-cli-surface.md` | The admin CLI's verbs and exit codes |
| `requirements/` | Product and technical requirements |
| `spec/openapi.json` | The pinned specification everything is generated from |
| `samples/` | Runnable end-to-end examples (core, cross-bank, reranker) |
