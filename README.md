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
```

Target framework: `net10.0`. The library is trimmable and Native AOT friendly — every
serializer path is source-generated and no reflection is used over caller types.

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
| Deliberately excluded | 7 deprecated, named individually in `docs/api-surface.md` |
| Component schemas modelled | 181 of 181 |

The two hand-written operations are `FileRetainAsync` and `ImportBankTransferAsync`, because a
file upload's parameters are streams and filenames rather than a JSON body. The generator
asserts the hand-written set matches the spec's non-deprecated multipart set exactly, so the
split cannot silently drift.

### The seven excluded operations

Out of scope by decision, and named so the claim is auditable: `get_bank_profile`,
`update_bank_disposition`, `add_bank_background`, `export_documents_sync_removed`,
`import_documents`, `export_documents`, `regenerate_entity_observations`. Each has a live
replacement among the 87 (except the last, which has none declared), so no capability is lost.

---

## Building and verifying

```bash
dotnet build Vectorize.Hindsight.Sdk.slnx -c Release
dotnet test  Vectorize.Hindsight.Sdk.slnx -c Release

# Generated code must match the spec - CI fails on drift.
python3 eng/generate_models.py --check
python3 eng/generate_operations.py --check

# Coverage floors: 90% line / 60% branch core, 95% / 80% DI.
dotnet test Vectorize.Hindsight.Sdk.slnx -c Release \
  --collect:"XPlat Code Coverage" --results-directory ./artifacts/test-results --logger trx
python3 eng/coverage_gate.py
```

### Measured state

| Assembly | Line | Branch | Floor |
| --- | --- | --- | --- |
| `Vectorize.Hindsight.Sdk` | 91.7% | 80.3% | 90% / 60% |
| `Vectorize.Hindsight.Sdk.Admin` | 91.0% | 82.5% | 90% / 60% |
| `Vectorize.Hindsight.Sdk.DependencyInjection` | 100% | 97.7% | 95% / 80% |

250 tests, zero exemptions — nothing is excluded from the measurement, so every counted line is
one a test exercises. Coverage describes hand-written code only; the generated model, operation
and metadata layers are excluded, because counting them would measure the compiler rather than
the library.

The integration suite runs against a live server when one is present and **skips with a
visible reason** when it is not, rather than failing — and it never silently passes without
running.

---

## Not verified

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
  Unions/                                        the four hand-written unions + converters
  Http/                                          transport, JSON, retry policy
  Errors/                                        the exception surface
  Telemetry/                                     logging, activity, meter
  Authentication/                                key providers
  Configuration/                                 options
src/Vectorize.Hindsight.Sdk.Admin/               the admin-CLI bridge (process, not HTTP)
src/Vectorize.Hindsight.Sdk.DependencyInjection/ DI integration
tests/Vectorize.Hindsight.Sdk.Tests/             unit suite (no network, no credentials)
tests/Vectorize.Hindsight.Sdk.Admin.Tests/       admin bridge (stub, real-process, and live levels)
tests/Vectorize.Hindsight.Sdk.IntegrationTests/  live suite + the coverage assertions
samples/Vectorize.Hindsight.Sdk.Sample/          runnable example
eng/                                             generators + coverage gate
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
