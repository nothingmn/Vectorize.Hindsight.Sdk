# Vectorize.Hindsight.Sdk

A .NET client for the [Hindsight](https://hindsight.vectorize.io) agent-memory HTTP API.

It covers the **full non-deprecated surface**: all **87 operations** and all **181 component
schemas** from the published OpenAPI 3.1 specification (version 0.10.0), generated rather than
hand-typed so the mapping from specification to code is reviewable and re-runnable.

---

## Not affiliated

This is an independent client. It is not published by, endorsed by, or affiliated with
Vectorize. "Hindsight" is their product; the API is theirs. Where this library and the
specification disagree, the specification wins and the disagreement is recorded in
[`docs/live-verification.md`](docs/live-verification.md).

---

## Install

```
dotnet add package Vectorize.Hindsight.Sdk
dotnet add package Vectorize.Hindsight.Sdk.DependencyInjection   # optional: DI integration
dotnet add package Vectorize.Hindsight.Sdk.Admin                 # optional: admin CLI bridge
dotnet add package Vectorize.Hindsight.Sdk.Reranking.Onnx        # optional: local cross-encoder reranking
dotnet add package Vectorize.Hindsight.Sdk.Reranking.TypeSafe    # optional: TypeSafe Choice reranking
```

Target framework: `net10.0`. The core library is trimmable and Native AOT friendly — every
serializer path is source-generated and no reflection is used over caller types. The optional
packages carry that property only as far as their own dependencies allow: the reranking package
depends on ONNX Runtime, which is not AOT compatible, which is exactly why reranking is a separate
package rather than a feature of the core.

---

## Quick start

```csharp
using Vectorize.Hindsight.Sdk;

using var client = new HindsightClient("http://localhost:8888");

// Store something. The server extracts facts, entities and relationships.
await client.RetainMemoriesAsync(
    "my-bank",
    new RetainRequest
    {
        Items =
        [
            new MemoryItem
            {
                Content = HindsightMemoryContent.FromText("Alice works at Google as a software engineer"),
                Context = "career",
            },
        ],
    },
    cancellationToken);

// Search.
var recalled = await client.RecallMemoriesAsync(
    "my-bank", new RecallRequest { Query = "What does Alice do?" }, cancellationToken);

foreach (var result in recalled.Results)
{
    Console.WriteLine(result.Text);
}

// Or ask for a synthesized answer.
var answer = await client.ReflectAsync(
    "my-bank", new ReflectRequest { Query = "Tell me about Alice" }, cancellationToken);
```

A local server for development:

```bash
pip install hindsight-api
export HINDSIGHT_API_LLM_API_KEY=sk-xxx
hindsight-api          # serves http://localhost:8888
```

This repository also ships a container-based rig — see [`test-infra/`](test-infra/README.md).

---

## What is asynchronous, and what is not

Every I/O method is asynchronous, takes a required `CancellationToken` with **no default
value**, and returns a `Task`. There is no synchronous counterpart and no sync wrapper. The
`CancellationToken` is deliberately mandatory at every call site, so cancellation is a
decision rather than an oversight.

The constructor performs **no I/O**. It cannot fail on a network problem, a bad key, or a
rate limit, because it never touches the network. Anything needing I/O — a key from a vault,
configuration from disk — is resolved at request time through `IApiKeyProvider`, or loaded by
the host before construction. A constructor cannot `await`, and a synchronous file read there
would be the first breach of the rule above.

---

## Authentication

The specification declares **no** `securitySchemes` and no `security` requirement. Instead, 88
of the 94 operations declare an optional `authorization` header parameter. So:

- A key is **optional**. A client constructs and works without one.
- The default deployment enforces nothing: a deliberately invalid bearer token is accepted
  (verified against a live server, and asserted by the integration suite).
- A deployment with a non-default `TenantExtension` requires `Bearer <key>` and answers 401
  when it is not satisfied.

```csharp
using var client = new HindsightClient(new HindsightClientOptions
{
    BaseAddress = "https://hindsight.example.com",
    ApiKey = "your-key",
    Tenant = "default",    // see below
});
```

`401` and `403` handling exists and is unit-tested against synthetic responses, but **cannot
be verified against the default deployment** because it does not enforce authentication. That
is stated rather than glossed: see [`docs/live-verification.md`](docs/live-verification.md).

### Tenant

Every bank-scoped path is `/v1/{tenant}/banks/{bank_id}/...` and the specification hardcodes
the literal `default`. `HindsightClientOptions.Tenant` makes that configurable, because a
non-default tenant deployment would otherwise require patching 85 call sites.

---

## Errors

One root type, with status-specific subclasses:

| Status | Type |
| --- | --- |
| local, before any request | `HindsightValidationException` |
| misconfiguration | `HindsightConfigurationException` |
| transport / timeout | `HindsightConnectionException` (`WasTimeout` distinguishes a deadline from cancellation) |
| 400 | `HindsightBadRequestException` |
| 401 | `HindsightAuthenticationException` |
| 403 | `HindsightPermissionDeniedException` |
| 404 | `HindsightNotFoundException` (`IsFeatureDisabled` distinguishes a missing resource from a disabled feature) |
| 409 | `HindsightConflictException` |
| 422 | `HindsightRequestValidationException`, with per-field `Errors` |
| 429 | `HindsightRateLimitException`, with `RetryAfter` |
| 5xx | `HindsightServerException` |
| any other non-success | `HindsightUnexpectedStatusException`, carrying the true status |
| unparseable success body | `HindsightProtocolException` |

Two deliberate asymmetries, each of which a caller relies on:

- `HindsightRateLimitException` does **not** derive from `HindsightServerException`. A rate
  limit is the server declining to serve, not failing. Collapsing them would make a caller
  report an outage when it should slow down.
- `HindsightValidationException` (local) is distinct from
  `HindsightRequestValidationException` (the server's 422). One is a bug the caller can fix;
  the other is a rejection they must read.

`HindsightNotFoundException.IsFeatureDisabled` exists because a live server returns 404 for an
operation whose backing feature is off, where the specification declares 200. Reported as
"bank not found", that would send a caller hunting for a typo that does not exist.

---

## Retries

- **Retryable**: `408`, `429`, and every `5xx`. The whole 5xx range is taken rather than naming
  `503` and `529` individually, because retrying only the named ones leaves `500`, `502` and
  `504` un-retried — which is how a transient outage becomes a caller-visible failure.
- **Never retried**: any `4xx`. `400`, `401`, `403`, `404` and `422` cannot be improved by
  repetition.
- **And never retried for a non-idempotent operation.** A synchronous retain is excluded: its
  `operation_id` is ignored on that path, so a retry could duplicate a write.
- Backoff is exponential from `InitialRetryDelay`, bounded by `MaxRetryDelay`, with
  **subtractive** jitter — each delay is reduced by a random fraction no greater than
  `RetryJitterFraction`, so it never collapses to zero the way full jitter can.
- A server-supplied `Retry-After` wins, bounded by `MaxRetryAfter`. A delay longer than that is
  discarded in favour of the computed backoff: a caller waiting minutes inside one call would
  rather fail and retry at its own level.
- Retry timing is a pure function of (attempt, server delay, options, jitter), so it is
  asserted without waiting.
- Retry delays are cancellable: the wait honours the caller's token.

---

## A 200 does not mean the memory was stored

The most important operational fact about this API, and the reason the integration suite
exists:

> An upstream LLM failure surfaces as **HTTP 200**. The error appears only in the child
> operation's `error_message`, reachable by polling `GET .../operations/{id}`.

Async retain returns `200 {"success":true}` and creates a parent operation with one child per
sub-batch. If the provider refuses the work — a quota limit, an outage — the parent still looks
healthy. A caller that treats `200` as "stored" will silently record nothing.

The client surfaces this honestly: `RetainResponse.Success` is what the server said, and
discovering that extraction failed means polling the operation. The integration suite pins the
behaviour, and [`docs/live-verification.md`](docs/live-verification.md) records the observation
that produced it.

---

## Dependency injection

```csharp
builder.Services.AddHindsight(options =>
{
    options.BaseAddress = "https://hindsight.example.com";
});

// Or from configuration:
builder.Services.AddHindsightFromConfiguration(builder.Configuration);

// Opt-in connectivity check, at the host's own startup where it can await and handle failure:
await HindsightClientWarmup.WarmAsync(app.Services, cancellationToken);
```

Warmup is **never** automatic. Registration does not issue a request, and warmup does not
swallow failures — a check that hid the failure would report a healthy host that cannot reach
its memory service.

A caller's own registrations win: registering your own `IHindsightTransport`, `HttpClient`,
`IApiKeyProvider` or `HindsightClient` means the extension uses it rather than replacing it.

See [`src/Vectorize.Hindsight.Sdk.DependencyInjection/README.md`](src/Vectorize.Hindsight.Sdk.DependencyInjection/README.md).

---

## Telemetry

BCL only — `ILogger`, `ActivitySource` (`Vectorize.Hindsight.Sdk`), `Meter`
(`Vectorize.Hindsight.Sdk`). No OpenTelemetry or exporter dependency; the consumer wires the
sink, and the library is silent when nobody listens.

**Nothing caller-supplied is ever recorded.** The content being retained is not logged, not
tagged on a span, and not used as a metric tag at any level. Bank ids and other caller-chosen
identifiers are excluded too: they can be identifying, and an unbounded tag value explodes
cardinality. Spans and metric tags carry the route template, the status, retry counts and
durations — bounded values only. The API key is never logged, and `Authorization` cannot even
be supplied through `Headers`, because a credential must not travel as an ordinary string.

There is a test that drives a sentinel value through a call and then searches every captured
log message, span tag and metric tag for it.

---

## Serialization

`System.Text.Json` with source generation. One `JsonSerializerContext` covering every wire
type, generated from the same schema set as the models. Options are built once per client and
frozen; no reflection-based fallback, so an unregistered type fails at build time rather than in
a trimmed deployment.

`DictionaryKeyPolicy` is deliberately **left null**: every dictionary key on this API is a
caller-supplied identifier, and any key policy would silently rewrite it so a response map would
no longer be keyed by what the caller asked for.

**Strict about what we send, tolerant about what we receive.** `required` is emitted on request
models, where it is compile-time enforcement at the construction site. It is *not* emitted on
response models, where it would make the deserializer throw whenever the server omits a field —
turning minor server drift into an unreadable payload the caller can do nothing about.

### Forward compatibility

The four unions the specification declares each keep an escape hatch for a shape this version
does not model, so a server that adds one does not fail a whole payload:

- `HindsightMemoryContent` / `HindsightContentBlock` — a plain string, or ordered `text`,
  `image` and `file` blocks.
- `HindsightTagGroup` — the recursive `and`/`or`/`not`/leaf tag-filter tree.
- `HindsightObservationScopes` — a named mode, or explicit tag sets.

`HindsightValues` carries the spec's closed value sets as constants rather than C# enums: the
sets are extensible (`TagGroupLeaf.match` has already grown once) and an enum member
deserialised from an unrecognised string throws, where a `string` carries the new value
through.

---

## Surface coverage

Coverage is **measured, not asserted**. The integration suite enumerates the non-deprecated
operations from the spec, maps each to the method that should exist, and checks by reflection
that it is there — with a guard that the enumeration found the expected number rather than
passing vacuously. A companion test verifies the 7 deprecated operations are genuinely
**absent**, so the exclusion cannot drift.

| | |
| --- | --- |
| Non-deprecated operations covered | 87 of 87 |
| Generated | 85 (grouped by the spec's own tags) |
| Hand-written | 2 multipart uploads |
| Compositions | 2 (`RecallAcrossBanksAsync` overloads), excluded from the count |
| Deliberately excluded | 7 deprecated, named individually in `docs/api-surface.md` |
| Component schemas modelled | 181 of 181 |
| Compositions beyond the spec | 2 (cross-bank recall), plus the optional reranker seam |

The two hand-written operations are `FileRetainAsync` and `ImportBankTransferAsync`, because a
file upload's parameters are streams and filenames rather than a JSON body. The generator
asserts the hand-written set matches the spec's non-deprecated multipart set exactly, so the
split cannot silently drift.

### The seven excluded operations

Out of scope by decision, and named so the claim is auditable: `get_bank_profile`,
`update_bank_disposition`, `add_bank_background`, `export_documents_sync_removed`,
`import_documents`, `export_documents`, `regenerate_entity_observations`. Each has a live
replacement among the 87 (except the last, which has none declared), so no capability is lost.

### Recall across banks

`RecallAcrossBanksAsync` is a **composition**, not an operation: it fans out to every bank you
name, concurrently, and merges the results with their provenance intact. It is marked with
`HindsightCompositionAttribute` so the operation count above still means what it says.

```
User query ──┬─ Bank A ─ recall ─┐
             ├─ Bank B ─ recall ─┼─→ candidates → merge → ordered context
             └─ Bank C ─ recall ─┘
```

The banks stay isolated for storage and permissions; relevance across them is decided per query.
There is **no reranker** — merging is a policy this library can defend, and relevance over the
combined set is left to the model the candidates are handed to.

```csharp
var result = await client.RecallAcrossBanksAsync(
    "what do we know about the billing migration?",
    ["bank-personal", "bank-team", "bank-archive"],
    new CrossBankRecallOptions { PerBankLimit = 10, MaxCandidates = 20 },
    cancellationToken);

Console.WriteLine(result.ToContext());            // [SOURCE bank-team | result …] …
foreach (var bank in result.Failures)             // a bank that failed, not silently dropped
{
    Console.WriteLine($"{bank.BankId}: {bank.Failure?.Message}");
}
```

What it deliberately does **not** do:

- **No score comparison across banks.** Each bank's native score ranks candidates within its own
  pipeline and content distribution. The default order takes one candidate from each bank in
  turn, following each bank's own ranking — the only order that assumes nothing. Every candidate
  keeps `RetrievalScore`, `Rank` and `PolicyWeight` as *separate* fields, so a surprising order
  can be traced to the field that caused it.
- **No reordering by policy weight.** A per-bank weight is carried on the candidate, never
  applied, because weighting that "represents an intentional policy" and weighting that
  "compensates blindly for retrieval-score differences" look identical to a library. The caller
  decides; the library records.
- **No semantic deduplication.** Only identity is collapsed. Two differently-worded facts are
  never merged — deciding they are the same fact is the model's judgement, not the library's.

Two traps, both found by running against a real server and both now covered by tests:

- **Recall against a bank that does not exist returns HTTP 200 with an empty result set**, not an
  error. A nonexistent bank is therefore indistinguishable from an empty one, and a typo in a bank
  id looks exactly like a bank with nothing relevant in it. Set `ValidateBanksExist` to turn that
  into a reported failure.
- **Neither `id` nor `chunk_id` is usable as a cross-bank content identity.** `chunk_id` is
  literally `{bank_id}_{document_id}_{n}`, so the same document in two banks yields two different
  chunk ids; `id` is a per-bank UUID. A deduplication key built on either could never match, which
  would leave deduplication silently dead rather than visibly broken. The library matches on
  document plus normalized text instead.

Run it against the rig with:

```bash
dotnet run --project samples/Vectorize.Hindsight.Sdk.CrossBankSample
```

### Choosing a reranker

Reranking is **opt-in, and selecting a provider is a registration rather than a branch**. There is no
switch statement over provider names and no registry to update: an `IRerankerFactory` is registered
and that registration is the selection, so a provider added later needs no change to the core.

```csharp
// The default: no judgement, no cost, no dependency. Preserves the merge order.
services.AddHindsight();

// Local ONNX. Free per call; needs the model assets.
services.AddOnnxReranker(new OnnxRerankerOptions { ModelPath = ..., ... });

// TypeSafe. A model call per recall; needs an IChoiceAsker adapter over your TypeSafe client.
services.AddTypeSafeReranker();
```

```csharp
var result = await client.RecallAcrossBanksAsync(query, banks, options, ct);
Console.WriteLine(result.RerankApplied);   // false when the identity default ran
```

`HindsightClient` takes a **nullable reranker** and defaults to the pure-C# `IdentityReranker`, so a
call site never null-checks one and `IReranker.IsIdentity` is how a caller tells "no judgement was
made" from "relevance was evaluated". A client built from the container carries whichever provider
was registered.

**Registration order does not matter.** An earlier version had `AddHindsight` register the identity
factory eagerly, and because every registration uses `TryAdd` the first one won — so a host calling
`AddHindsight()` before `AddTypeSafeReranker()` silently kept the no-op reranker and never ranked
anything. There is now a regression test for exactly that.

A provider that cannot be built **falls back to the identity reranker** rather than failing, because
losing cross-bank recall entirely is worse than losing its ranking; `ResolveReranker(out var reason)`
reports why. A host that disagrees registers with `required: true` and fails at first use instead.

### Optional reranking

The merge above deliberately does **not** rerank. Adding that second stage is opt-in, through an
`IReranker` seam that lives in the core library so a caller who does not want one never takes an
inference dependency:

```csharp
using var reranker = new OnnxReranker(new OnnxRerankerOptions
{
    ModelPath = "artifacts/reranker/model_int8.onnx",
    TokenizerJsonPath = "artifacts/reranker/tokenizer.json",
    SentencePieceModelPath = "artifacts/reranker/sentencepiece.bpe.model",
});

var result = await client.RecallAcrossBanksAsync(
    query, ["bank-personal", "bank-team", "bank-archive"],
    new CrossBankRecallOptions { Reranker = reranker },
    cancellationToken);

Console.WriteLine(result.RerankApplied);   // false means the ranking degraded, and why is on RerankFailure
```

`Vectorize.Hindsight.Sdk.Reranking.Onnx` runs a BGE-style cross-encoder in-process on CPU or GPU.
The model assets are fetched with `test-infra/fetch-reranker-model.sh`. Design points worth knowing:

- **A reranker failure never costs the caller their memories.** The caller asked two questions —
  what is relevant, and in what order — and the banks already answered the first. A failed reranker
  is reported on `RerankFailure` and the candidates come back in the pre-rerank order, with the
  degradation stated in `ToContext()` so a model is not told position means relevance.
- **A reranker that returns the wrong number of scores is treated as a failure**, not mapped
  positionally. Mismatched scores would produce a plausible-looking order that is simply wrong,
  which is worse than no ranking.
- **Scores are ordinal, not calibrated.** Measured with the INT8 export: the right answer scored
  0.29 while every other candidate sat between 0.000016 and 0.0018, with the two least relevant
  separated only in the sixth decimal. The order was right and the magnitudes were near-meaningless
  — normal for a quantized cross-encoder. Order by the score; do not threshold on it.
- **A missing GPU falls back to CPU with a warning** rather than failing. Check `ProviderInUse`.
- **Each provider answers a different question.** ONNX is local and free per call, so it scales with
  traffic; TypeSafe is a model call per recall, so it costs money and a round trip but needs no assets
  and no deployment. Pick on that axis, not on benchmark folklore.

**TypeSafe `Choice` as a reranker.** It fits, and the reason is specific: a
[Choice](https://docs.typesafe.ai/primitives/choice) question returns a **probability distribution
over the options you supply**, so putting every candidate in as an option makes them compete inside
one judgement and the probabilities rank. The obvious alternative does not work — asking "is this
relevant?" once per candidate returns *independent* answers, so twenty unrelated 0.9s rank nothing.
That mode (`TypeSafeRerankMode.Noul`) exists for pools too large to list as options and is documented
as not mutually comparable.

The package depends on an `IChoiceAsker` seam rather than a TypeSafe client, because `Jev.Sdk` is not
published to nuget.org and a published package cannot depend on an unpublished one. Adapting your
client is one small class, shown in that package's README.

- **The tokenizer is verified against the reference implementation**, not assumed. This is the part
  that goes silently wrong — ONNX Runtime runs happily on wrong input and returns plausible numbers.
  An XLM-R reranker needs two assets that disagree: the HF vocabulary inserts a `<pad>` at index 1
  (shifting every content piece by one), and orders the special tokens differently. Segmentation
  comes from `sentencepiece.bpe.model` and each piece is mapped through the vocabulary in
  `tokenizer.json`, so the mapping is derived from the model's own artifacts rather than hardcoded.
  The suite asserts the exact reference ids on English and multilingual input, and that not one
  piece falls back to `<unk>` across five scripts.

---

## Building, packaging, and CI

### Locally

```bash
# Every gate CI runs, in the same order, from committed state.
bash eng/ci_local.sh

# Or step by step:
dotnet build Vectorize.Hindsight.Sdk.slnx -c Release
dotnet test  Vectorize.Hindsight.Sdk.slnx -c Release

# Generated code must match the pinned spec - CI fails on drift.
python3 eng/generate_models.py --check
python3 eng/generate_operations.py --check
python3 eng/generate_inventory.py --check

# Coverage floors: 90% line / 60% branch, 95% / 80% for the DI package, and 80% for the
# reranker. That last one is CONDITIONAL: the reranker's inference paths only run when the
# model assets are present, so with them absent the gate reports the package as unmeasured
# rather than failing every clean checkout - and applies the floor in full when they are there.
#
# To exercise the reranker suite as well:
#   bash test-infra/fetch-reranker-model.sh int8
#   export HINDSIGHT_RERANKER_DIR=artifacts/reranker
dotnet test Vectorize.Hindsight.Sdk.slnx -c Release \
  --collect:"XPlat Code Coverage" --results-directory ./artifacts/test-results --logger trx
python3 eng/coverage_gate.py

# Package, then verify and consume what was produced.
dotnet pack Vectorize.Hindsight.Sdk.slnx -c Release -o ./artifacts/packages
python3 eng/verify_packages.py ./artifacts/packages --expect-version "$(python3 eng/package_version.py)"
bash eng/consume_package.sh
```

`eng/ci_local.sh` runs twelve gates and names the four things it cannot reproduce. It is the
honest substitute for a workflow that has not executed on GitHub, not a claim that it has.

### Workflows

| Workflow | Trigger | Does |
| --- | --- | --- |
| `build.yml` | push, pull request, manual | Validates its own definitions, checks for spec drift, builds, tests, enforces coverage, packs, verifies and consumes the packages, uploads them as artifacts |
| `release.yml` | version tag, manual | Re-runs every gate, refuses a tag that disagrees with the declared version, verifies the changelog describes that version, extracts the release notes, records SHA-256 checksums, and attaches the packages to a GitHub release |

**Publishing to nuget.org is deliberately not automated.** The release workflow produces artifacts
and attaches them to a GitHub release; a separate, deliberate act publishes them. A workflow that
pushed to a public feed on a tag would be one mistyped tag away from an irreversible mistake.

### The version is declared once

`Directory.Build.props` holds `VersionPrefix`; `eng/package_version.py` reads it. The release
workflow refuses to proceed when a tag and that value disagree, because the alternative is a release
page advertising one version while the package contains another.

### What the packaging checks actually catch

Each was verified by injecting the failure it guards against:

| Check | Catches |
| --- | --- |
| `generate_* --check` | A generated file that has drifted from the pinned spec |
| `validate_workflows.py` | An action pinned to a branch; a step with neither `uses` nor `run`; a step with both; a reference to a script that does not exist |
| `verify_packages.py` | A missing package or symbol package; a placeholder description; an absent README or LICENSE; no `lib/` payload; a sibling dependency on a version that cannot exist; a version that disagrees with the build |
| `consume_package.sh` | A package that packs but cannot be restored, loaded, or called |
| `coverage_gate.py` | A floor that stopped applying, and a test run that discovered zero tests |
| `EnablePackageValidation` | A public member removed since the baseline |

### Dependency hygiene

`Dependabot` runs weekly for NuGet and for the workflow actions themselves, with
`Microsoft.Extensions.*` and the SourceLink packages grouped so a review is one version bump rather
than six that must all land together.

One constraint is worth knowing before updating: **SourceLink must stay on the SDK's version line.**
The widely-cited 8.0.0 pulls `Microsoft.Build.Tasks.Git` 8.0.0, which carries a known
moderate-severity advisory — and because warnings are errors here, restoring it fails the build
outright with `NU1902`. Verified, not assumed.

## Not verified

The ONNX reranker has been exercised on **CPU only**. It was built and tested on a machine with an
RTX A5000 present but no CUDA libraries (cuDNN/cuBLAS) installed, so the GPU path is the one code
path in this repository that has never actually run: `AppendExecutionProvider_CUDA` is reached only
when the runtime reports a CUDA provider, and that report was always negative here. The fallback
behaviour **is** verified — a requested GPU with no CUDA libraries falls back to CPU with a warning
rather than failing — but GPU inference itself is not.


Stated plainly rather than left implicit:

- **`401` and `403`** cannot be provoked against the default deployment, which does not enforce
  authentication. They are unit-tested against synthetic responses and unverified live.
- **The `409` the documentation describes** for a reused `operation_id` did not reproduce: a second
  submission with different content and the same id returned `200`. A *different* `409` is real and
  deterministic — retrying a completed `batch_retain` operation — and is covered by the suite. The
  correction is recorded in [`docs/live-verification.md`](docs/live-verification.md) §6, including
  why the first conclusion generalised wrongly from a single negative probe.
- **The GitHub Actions workflow** has not executed on GitHub. The steps it runs were each run
  locally, but the pipeline itself is unverified.
- **Operations only a non-default deployment serves** — `clone`, `transfer/export`,
  `transfer/import` — are modelled and generated but cannot be called against the reference
  server, whose feature is off. The suite skips them with that reason.
- **The admin bridge's live tests ran against a containerised `hindsight-admin`** and are off by
  default. They were verified once with the docker prefix, covering the read-only commands and the
  declined-confirmation path. The destructive commands (`backup`, `restore`, migration,
  export/import, decommissions) were **not** driven end to end against a real database; their
  argument shapes are asserted against stubs, and only `repair-bank --dry-run` was run for real.

---

## Repository layout

```
src/Vectorize.Hindsight.Sdk/                     the HTTP client
  Models/                                        generated DTOs (181) + HindsightValues
  Operations/                                    generated operations (15 files, tagged)
  CrossBank/                                     recall across banks: fan-out, merge, provenance
  Reranking/                                     the IReranker + IRerankerFactory seams, and the
                                                 pure-C# identity default (no inference dependency)
  Unions/                                        the four hand-written unions + converters
  Http/                                          transport, JSON, retry policy
  Errors/                                        the exception surface
  Telemetry/                                     logging, activity, meter
  Authentication/                                key providers
  Configuration/                                 options
src/Vectorize.Hindsight.Sdk.Admin/               the admin-CLI bridge (process, not HTTP)
src/Vectorize.Hindsight.Sdk.DependencyInjection/ DI integration
src/Vectorize.Hindsight.Sdk.Reranking.Onnx/      local ONNX cross-encoder reranker
src/Vectorize.Hindsight.Sdk.Reranking.TypeSafe/  TypeSafe System One Choice reranker
tests/Vectorize.Hindsight.Sdk.Tests/             unit suite (no network, no credentials)
tests/Vectorize.Hindsight.Sdk.Admin.Tests/       admin bridge (stub, real-process, and live levels)
tests/Vectorize.Hindsight.Sdk.IntegrationTests/  live suite + the coverage assertions
tests/Vectorize.Hindsight.Sdk.Reranking.Tests/   reranker, incl. tokenizer parity vs the reference
samples/Vectorize.Hindsight.Sdk.Sample/          runnable example
samples/Vectorize.Hindsight.Sdk.CrossBankSample/ multi-bank recall against the rig
samples/Vectorize.Hindsight.Sdk.RerankerSample/  choosing a reranker by registration
eng/                                             generators, gates, and CI helpers
.github/workflows/                               build and release pipelines
spec/                                            the pinned OpenAPI specification
test-infra/                                      the local server rig
docs/                                            verified API notes, live findings, decisions
inventory/                                       derived operation and schema inventory
```

---

## Documentation

- [`docs/api-surface.md`](docs/api-surface.md) — the verified surface, the decisions taken, and
  the seven excluded operations.
- [`docs/live-verification.md`](docs/live-verification.md) — what the specification does not
  tell you, observed on a running server.
- [`inventory/`](inventory/) — every operation and schema, derived from the spec in code.
- [`docs/admin-cli-surface.md`](docs/admin-cli-surface.md) — the admin CLI surface, verified against
  the deployed binary, including observed exit codes and output formats.
- [`test-infra/README.md`](test-infra/README.md) — the local rig and its isolation guarantees.

---

## License

MIT.
