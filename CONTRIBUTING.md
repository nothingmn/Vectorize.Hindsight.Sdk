# Contributing

## Before you start

This repository is a client library for an API someone else owns. Two consequences shape everything
below:

1. **The specification is the authority.** Where this library and the OpenAPI document disagree, the
   document wins, and the disagreement gets recorded rather than resolved silently.
2. **A claim is only as good as the evidence behind it.** "It should work" is not a result.
   [Verified against a live server](#verifying-against-a-live-server) is.

## Ground rules

These are enforced by the build rather than by review, which is the point — a rule a reviewer has to
remember is a rule that eventually gets missed.

- **`net10.0` only.** No multi-targeting. Adding a target later is additive; removing one is not.
- **Warnings are errors**, analyzers at `latest-recommended`. If a rule genuinely does not apply,
  suppress it narrowly with a comment saying why — never by lowering `AnalysisLevel`.
- **All I/O is asynchronous.** Every public I/O method returns `Task<T>`, takes a
  `CancellationToken` with **no default value**, and returns no stream or `IAsyncEnumerable` to the
  caller. No `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()`.
- **A constructor performs no I/O.** It cannot `await`, and a synchronous read there is the first
  breach of the rule above.
- **File discipline.** Split by function first, then by size. No file over **100 KB**; working
  targets are 400 lines soft and 800 hard.
- **Generated code is generated.** Files under `Models/`, `Operations/`, and `HindsightJsonContext.g.cs`
  come from `eng/` and must not be hand-edited. Change the generator.

## Change the specification, not the output

`Models/` and `Operations/` are produced by `eng/generate_models.py` and `eng/generate_operations.py`
from `spec/openapi.json`. To change them:

```bash
# 1. Update the generator (or the pinned spec, deliberately).
# 2. Regenerate.
python3 eng/generate_models.py
python3 eng/generate_operations.py

# 3. Confirm nothing drifted.
python3 eng/generate_models.py --check
python3 eng/generate_operations.py --check
```

CI runs both `--check` steps **before** the build, so a stale generated file is reported as drift
rather than as whatever confusing compile error it happens to produce. A pull request that edits a
generated file by hand will fail.

Hand-written types live outside the generated directories on purpose — the unions in
`Sdk/Unions/`, for instance — so that regenerating cannot delete them.

## Testing

```bash
dotnet build Vectorize.Hindsight.Sdk.slnx -c Release
dotnet test  Vectorize.Hindsight.Sdk.slnx -c Release

# Coverage floors: 90% line / 60% branch per library, 95% / 80% for the DI package.
dotnet test Vectorize.Hindsight.Sdk.slnx -c Release \
  --collect:"XPlat Code Coverage" --results-directory ./artifacts/test-results --logger trx
python3 eng/coverage_gate.py
```

Expectations:

- **No mocking framework and no assertion library.** Hand-written stubs of this library's own seams
  are smaller than mock setup and survive refactors that would break a setup string.
- **Assert on the serialized bytes**, not only the object graph. A wire-shape test is what catches
  contract drift.
- **Test the real implementation, not only the stub.** A transport stubbed at one level proves the
  client's logic; a stub `HttpMessageHandler` exercises the actual HTTP path. The admin bridge is
  tested the same way, against scripted child processes.
- **Never weaken a test to get green.** A test that fails because a stated fact is wrong is a
  finding. Skip it with a documented reason, or report it.
- **A skip must be visible.** A test that returns early reports as *passed* and looks like coverage
  while proving nothing. Use a skip that states its reason.
- Coverage floors are raised as coverage rises. They are never lowered to make a build pass; if one
  ever has to come down, the commit message says why.

## Verifying against a live server

Three things in the specification's shape are only checkable against a running instance, and the
repository has evidence that all three have bitten:

- undeclared status codes (`405`, and a `409` the spec never mentions);
- whether auth is enforced at all (`401`/`403` are unverifiable when it is not);
- round-trip behaviour a stub cannot reproduce — including that a `2xx` does not mean the work
  happened.

```bash
cd test-infra && ./up.sh     # a local, isolated Hindsight instance
./down.sh --purge            # clean slate
```

The rig is isolated by construction: its own container, its own volume, loopback-only ports, and a
digest-pinned image so a result is reproducible. Never point it at a shared or production instance.

Findings from a live probe belong in `docs/live-verification.md`, including anything that
**contradicts** the published specification. When a claim there is later corrected, record *how it
was wrong* as well as the new fact.

## Commits

- One concern per commit, with a message that explains **why** — the code already says *what*.
- Say what was verified, with the real output, and state plainly what was **not** verified.
- A defect found by probing rather than by reading is worth naming as such: it is evidence the
  surrounding approach is sound, and a warning to whoever reads it next.

## What not to do

- Do not add a dependency to the core library. Its only package dependency is
  `Microsoft.Extensions.Logging.Abstractions`; telemetry is BCL-only and the consumer wires the sink.
- Do not log, tag, or measure anything caller-supplied. See [`SECURITY.md`](SECURITY.md).
- Do not make an admin command run automatically, and never supply `--yes` on a caller's behalf.
- Do not commit build output. `bin/`, `obj/`, `artifacts/` and `vendor/` are ignored; CI asserts they
  stay that way.
