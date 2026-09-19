# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

While the version is `0.x`, the public API may change in a minor release. From `1.0.0` onward, a
breaking change requires a major version, and the package validation in CI enforces it rather than
leaving it to review.

The version is declared once, in `Directory.Build.props` (`VersionPrefix`), so every package in the
solution moves together. That is deliberate: the packages describe one API surface, and a consumer
mixing a 0.1.0 client with a 0.2.0 admin bridge would be reading a contract that never existed.

---

## [Unreleased]

### Added

**`Vectorize.Hindsight.Sdk.Reranking.Onnx`** — optional local ONNX cross-encoder reranking.

- Implements the `IReranker` seam declared in the core library, adding the second stage of
  cross-bank retrieval: candidates from every bank scored against one query under one model, which
  is the only comparison meaningful across banks whose retrieval scores are not on a common scale.
  The seam lives in the core and the implementation in this package, so a caller who does not want
  a reranker never takes an inference dependency.
- Verified to reproduce the reference Hugging Face tokenization **exactly** (`bge-reranker-v2-m3`).
  This is the part that fails silently: ONNX Runtime runs happily on wrong input and returns
  plausible numbers. An XLM-R reranker needs both `tokenizer.json` and `sentencepiece.bpe.model`
  because they disagree — the HF vocabulary inserts a `<pad>` at index 1, shifting every content
  piece by one, and orders the special tokens differently. Segmentation comes from SentencePiece and
  each piece is mapped through the HF vocabulary, so the mapping is derived from the model's own
  artifacts rather than hardcoded. The suite asserts the exact reference ids and that no piece falls
  back to `<unk>` across five scripts.
- Scores measured to be **ordinal, not calibrated** — documented as a contract rather than a caveat,
  with the measurement that establishes it.
- Batching with length grouping, bounded concurrency, CPU by default, and a GPU that falls back to
  CPU with a warning rather than failing.
- A reranker that throws, or that returns the wrong number of scores, does not cost the caller the
  memories the banks already returned: the fallback is reported on `RerankFailure` and disclosed in
  the rendered context, so a model is never told that position means relevance when it does not.

## [0.1.0] — 2026-09-19

First release. Not published to nuget.org; produced by CI and attached as a build artifact.

### Added

**`Vectorize.Hindsight.Sdk`** — a .NET client for the Hindsight HTTP API.

- The full non-deprecated surface: **87 of 87 operations** and **181 of 181 component schemas**
  from OpenAPI 3.1 specification version 0.10.0, generated rather than hand-typed so the mapping
  from specification to code is reviewable and re-runnable.
- The 7 deprecated operations are deliberately excluded and named individually, so the coverage
  claim is auditable rather than approximate.
- Retain, recall, reflect, memory banks, entities, directives, mental models, knowledge base,
  documents, webhooks, operations, transfers, audit logs and LLM traces.
- Typed errors for the statuses a live server actually returns — including `405` and the
  feature-disabled `404`, **neither of which the specification declares** — plus a
  `HindsightUnexpectedStatusException` that preserves the true status for anything unmapped, so a
  real `405` is not reported as a `400`.
- Retry classification: `408`, `429` and every `5xx` retryable; never a `4xx`; and never a
  non-idempotent operation, so a synchronous retain cannot be duplicated by a retry.
- Subtractive jitter, a bounded `Retry-After`, and retry timing as a pure function of
  (attempt, server delay, options, jitter) so it is asserted without waiting.
- The four unions the specification declares — multimodal retain content, the recursive
  tag-group expression tree, and observation scopes — each preserving an unmodelled arm as raw
  JSON so a new shape does not fail a whole payload.
- BCL-only telemetry. Nothing caller-supplied is logged, tagged on a span, or used as a metric
  tag, and the API key cannot be supplied as an ordinary header.
- Trimmable and Native AOT friendly: source-generated serialization throughout, no reflection
  over caller types.

**`Vectorize.Hindsight.Sdk.DependencyInjection`** — `IServiceCollection` integration.

- `AddHindsight` and `AddHindsightFromConfiguration`, with a caller's own registrations winning.
- Repeated registrations compose rather than silently discarding all but one.
- Opt-in warmup that never runs automatically and never swallows a failure.

**`Vectorize.Hindsight.Sdk.Admin`** — a bridge for the `hindsight-admin` CLI.

- All nine deployed commands: backup, restore, database migration, bank export/import,
  vector-index repair, and worker management.
- This CLI talks to PostgreSQL directly rather than over HTTP, so it is driven as a process and
  returns an exit code with two text streams rather than a typed body.
- Destructive commands require explicit confirmation and never pass the CLI's `--yes` on the
  caller's behalf, so an accidental call aborts rather than destroying data.
- Stdin is closed on every command, so a confirmation prompt cannot hang a service; the command
  timeout kills the process tree rather than only the parent.
- `rename-bank` is deliberately not bridged: it is registered in the vendor's `main` branch but
  absent from the deployed build, where it answers "No such command".

### Verified against a live server

Behaviour the specification does not state, pinned by the integration suite:

- The **served** spec can be smaller than the **published** one at the same version string,
  because feature flags gate route registration.
- Feature flags are not a reliable availability oracle: one flag makes a spec-declared `200`
  operation answer a feature-disabled `404`, while another leaves its route answering `200`.
- Authentication is **not enforced** by a default deployment — a deliberately invalid bearer token
  is accepted — so `401` and `403` cannot be verified there and the suite says so rather than
  implying coverage.
- **A `200` does not mean the memory was stored.** An upstream LLM failure surfaces as `200` with
  the error only reachable through the child operation.
- The documented `409` for a reused `operation_id` does not reproduce; a *different* `409` does,
  deterministically, on retrying a completed operation.

### Build and packaging

- `global.json` pins the SDK line with `rollForward: latestFeature`.
- SourceLink is referenced **only** in CI: version 8.0.0 pulls a dependency carrying a known
  advisory, which fails this build because warnings are errors.
- `EnablePackageValidation` runs on every pack. Verified to catch a removed public member.
- Coverage floors enforced by `eng/coverage_gate.py`, with generated code correctly excluded.

[Unreleased]: https://github.com/robchartier
[0.1.0]: https://github.com/robchartier
