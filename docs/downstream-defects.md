# Defects a downstream consumer found — and what this library did about them

This file records five changes that came back from **Agentry**, a host that vendored this library as
source on 2026-09-23 (snapshot `0784fa2`) and then spent five weeks running it against a live server.
Its copy was patched locally; this branch brings the patches back to the library they belong in.

The value of the file is not the five diffs. It is the reason each one existed and the reason this
library's own suite could not see it, because every one of them was silent: nothing threw where a test
was looking, nothing warned, and each part looked correct on its own.

| # | What was wrong | Where the defect actually was | Why the suite missed it |
| --- | --- | --- | --- |
| 1 | Every file upload was rejected by the server | `HttpHindsightTransport` — the multipart wrapper never published its inner `Content-Type` | Every upload test asserted the library's own request model, which never described a header |
| 2 | Every mental-model create/update with a trigger was rejected | `eng/generate_models.py` — nullability was honoured for `List<>` members and not for `Dictionary<>` ones | No test asserted the *serialised* shape of an unset `response_schema` |
| 3 | A corrupt tokenizer asset killed the whole composition | `OnnxReranker` — the tokenizer load sat outside the wrap that `TryCreate` filters on | The existence checks were covered; a file that exists but cannot be *parsed* was not |
| 4 | Mental-model reads silently over-fetched content | `spec/openapi.json` — the declared `detail` default disagreed with the deployed server | Nothing compared the declared default with what a server actually serves |
| 5 | Rerank cost was estimated from outer timings and repeatedly misjudged | `OnnxReranker` — no measurement existed where the cost is incurred | Nothing to miss: the capability was absent |

---

## 1. The multipart request announced no boundary

**Symptom.** Every file-retain failed with `422 body.files: Field required; body.request: Field
required` — both fields reported missing while both were present in the body.

**Root cause.** `MultipartFormDataContent` generates a boundary and puts it in **its own** `Content-Type`
header, which lives on the inner object. The library wraps that inner content in
`MultipartFormDataContentAdapter` to attach streams, and the wrapper never copied the header across. The
body went out carrying a boundary that no header announced, so the server had no way to split the parts
and reported every field as absent. The body bytes were correct the whole time.

**Why the suite missed it.** The upload tests assert on `HindsightRequest.Multipart` — the library's own
model, which is a list of parts and has no concept of a header. They were all green against a body that
no server would accept. It also only failed on the path production takes: a caller who supplies an
`HttpClient` and a caller who does not take different routes through the transport, and the tests covered
the one that happened to work.

**Fix.** One line, in the wrapper's constructor: `Headers.ContentType = _inner.Headers.ContentType;`.

**Proof it is fixed.** `HttpTransportTests.A_multipart_request_announces_the_boundary_that_its_body_uses`
drives the real transport over a stub handler and asserts the *sent message*: content type is
`multipart/form-data`, it carries a non-empty `boundary` parameter, and that exact boundary appears in
the body. Reverting the one line makes it fail with `Assert.NotNull() Failure: Value is null` — observed,
not inferred.

## 2. `response_schema` was serialised as an empty object

**Symptom.** Every mental-model create or update that set a trigger was refused with
`422 response_schema must define a non-empty 'properties' object`, and any reflect call that set one with
it. The server was right: the request carried `"response_schema": {}`.

**Root cause — and it was not the model.** `response_schema` is declared `anyOf: [object, null]` in the
specification. The generator computed a `nullable` flag correctly from that, then applied it to
`List<>` members only:

```python
nullable_collection = nullable and ctype.startswith("List<")
```

A nullable `Dictionary<>` member fell through to the ordinary branch and was emitted **non-nullable with
`= []`**, so the serialiser — configured with `WhenWritingNull` — had a value to write and wrote `{}`.
The reasoning already in that file for lists ("`tags: []` is a real filter value; absent is not empty")
was never extended to dictionaries, where an empty object is equally a distinct value.

**Fix.** The rule now covers both collection shapes:

```python
nullable_collection = nullable and (
    ctype.startswith("List<") or ctype.startswith("Dictionary<")
)
```

This is a generator fix rather than a hand-edit of generated code, which is the only honest place for it:
`*.g.cs` files are regenerated, so a hand-patch to a model is a patch that disappears on the next run.
The downstream consumer patched the model directly, in a copy where the generator would have undone it.

**Blast radius, stated rather than discovered later.** 19 model files change, 30 declarations, every one
of them a specification-declared nullable dictionary. Most are response members (`Trace`,
`StructuredOutput`, `Metadata`, `ResultMetadata`, …) and one is the request member that caused the
defect. All become `Dictionary<…>?` with no initialiser, so an absent field stays absent instead of
arriving as an empty collection — the same posture this generator already took for optional
non-collection members, and consistent with the rule it already applied to lists. It is a source-level
change for a consumer who dereferenced one of these without a null check; the packages are `0.x`, and the
declared policy is that a minor release may change the public API.

**Proof it is fixed.** `ResponseSchemaDefaultTests` asserts both directions for reflect, mental-model
create and mental-model update: the key is absent from the serialised body when unset, and present when
set. The second half matters — it is what stops "delete the member" from passing as a fix. Against the
unfixed generator, three of the six cases failed with the body `{"trigger":{"response_schema":{}}}`; the
three positive cases passed, which is how we know they are not vacuous.

## 3. A corrupt-but-present tokenizer escaped the factory

**Symptom.** None, in the log. Every routing service came out null and the feature was simply dead.

**Root cause.** `OnnxRerankerFactory.TryCreate` catches only `HindsightConfigurationException` and
`HindsightProtocolException`, by design. The constructor's asset checks throw exactly those. The tokenizer
*load* sat outside that wrap, and a file that exists but cannot be parsed throws `JsonReaderException`,
`KeyNotFoundException` or `InvalidProtocolBufferException` — none of which the filter admits. So the
exception crossed `TryCreate`, was swallowed by the host's composition catch as a generic failure, and
left the host running with nothing wired. It also broke the factory's own documented contract: *a missing
or broken model does not throw by default; `TryCreate` reports the reason and returns null.*

**Why the suite missed it.** The factory tests pointed at paths that do not exist, which is the common
misconfiguration and the one the existence checks handle. "Precious but wrong" — a truncated download, a
file left by a failed fetch — was the gap.

**Fix.** Wrap the load and rethrow as `HindsightConfigurationException`, preserving the original type and
message in the text: the failure is a configuration failure, and the reason string is where a host learns
which asset was unreadable.

**Proof it is fixed.** `OnnxRerankerFactoryTests.A_corrupt_but_present_tokenizer_is_reported_rather_than_thrown`
builds a temp directory with an existing model file and a truncated `tokenizer.json`, then asserts
`TryCreate` returns null with a reason naming the tokenizer. Reverting the wrap makes the test fail with
the raw `System.Text.Json.JsonReaderException` escaping `TryCreate` — which is the defect, reproduced.

## 4. The declared `detail` default was not the served default

**Symptom.** A mental-model read that omitted `detail` received full content, including `reflect_response`
and trace data, from a 0.9.2 deployment — while this 0.10.0 contract declares the default is `metadata`.
Silent over-fetch, and a size surprise on a resource already documented as large.

**Fix.** The description is corrected at the specification and regenerated into the operation, so it
cannot drift back: it states that callers must pass an explicit `detail` rather than depend on the
default, and records the version where the two disagreed.

**What this one teaches.** A declared default is not evidence of a served default. The only thing that
establishes the latter is a request against a running server, which is what `live-verification.md` is for.

## 5. Rerank cost is now measured where it is incurred

Rerank cost had only ever been estimated from outer wall-clock timings, and the estimates kept being
wrong — a batched int8 forward at the configured sequence length is the largest single term in a
cross-bank recall, and it is invisible from outside.

The downstream consumer added a `Console.WriteLine` probe to the scoring path. That is not the shape for
a library: no code in this repository writes to the console outside README examples, and a library that
does corrupts its host's output in the middle of a hot path. The capability is ported, the mechanism is
not — `RerankLog.RerankTiming`, a source-generated `Debug` event on the existing log seam, reporting
documents, batches, batch size, sequence length, encoded tokens, and encode vs score vs total
milliseconds. It costs nothing when the level is off, and it obeys the convention already stated in
`RerankLog`: bounded, non-caller-supplied values only.

---

## What was deliberately not ported

The downstream copy also contains work that belongs to that host, not to this library, and importing it
would have made this library's surface claim things its server does not serve:

- **A `state` member on the recall result.** Declared by that host because its adapter is written against
  it, but not in the 0.10.0/0.10.1 contract and not returned by the running 0.9.2 instance — a live
  recall returns no `state` key even for a fact whose single read reports `state: invalidated`. Their own
  doc comment says not to depend on it. Adding it here would make a lie of the generated surface.
- **A typed file-retain body.** A convenience object plus a public multipart builder. Useful, and a
  feature rather than a fix; nothing in the defects above needs it, and it is new public API that would
  need its own documentation pass.
- **Descriptor routing** (classified bank selection, selection strategies, a window guard). A whole
  capability built on top of this library inside that host, not a change to it.
- **`FileRetainMetadata.FileName` removal.** That host measured it as server-inert (the server accepts a
  payload carrying one and silently drops it) and kept it, correctly. Removing a member is a breaking
  change with no defect behind it.

## How this was verified

```
dotnet build Vectorize.Hindsight.Sdk.slnx -c Release
  Build succeeded.  0 Warning(s)  0 Error(s)

dotnet test Vectorize.Hindsight.Sdk.slnx -c Release -m:1
  Vectorize.Hindsight.Sdk.Admin.Tests        Passed:  47  Failed: 0  Skipped:  4
  Vectorize.Hindsight.Sdk.IntegrationTests   Passed:   4  Failed: 0  Skipped: 11
  Vectorize.Hindsight.Sdk.Reranking.Tests    Passed:  65  Failed: 0  Skipped: 26
  Vectorize.Hindsight.Sdk.Tests              Passed: 351  Failed: 0  Skipped:  0

python3 eng/generate_models.py --check      Generated models are up to date (186 files).
python3 eng/generate_operations.py --check  Generated operations are up to date (15 files, 85 operations).
```

The three regression tests were each shown able to fail by reverting the corresponding fix in place and
re-running, and the results are quoted under each defect above. That check is not ceremony: the
`response_schema` bug existed precisely because a test that looked like it covered the field never
exercised what went on the wire.

## The gap this exposes, kept as a note rather than a promise

Every defect here was a `200`-shaped, silent kind of wrong, and three of the five were only reachable at
a layer the suite had no assertions on: the bytes and headers actually sent, and the difference between a
declared default and a served one. Tests that assert the library's request model prove the library agrees
with itself. `docs/live-verification.md` is what establishes agreement with a server.
