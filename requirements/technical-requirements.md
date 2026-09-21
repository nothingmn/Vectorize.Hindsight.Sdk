# Vectorize.Hindsight.Sdk — Technical Requirements

Status: **draft, awaiting sign-off**
Created: 2026-09-19
Companion to: [`requirements.md`](requirements.md) (product requirements, P1–P17)

This document records *how* the product requirements are met, the shapes chosen, and the measured
constraints that force those shapes. Each decision is numbered **D1..Dn** and marked as *recommended*
or *user-accepted*. Where a decision is still open it says so and points at the product open item.

---

## 1. What already exists

This work extends an established seam rather than inventing one. The reranker side is the template.

| Component | Location | State |
| --- | --- | --- |
| `IReranker` | `src/Vectorize.Hindsight.Sdk/Reranking/IReranker.cs` | built, live-verified |
| `IRerankerFactory` | `src/Vectorize.Hindsight.Sdk/Reranking/IRerankerFactory.cs` | built |
| `IdentityReranker` | `.../Reranking/Providers/IdentityReranker.cs` | built — **behaviour to change (P7)** |
| `OnnxReranker` + factory | `src/Vectorize.Hindsight.Sdk.Reranking.Onnx/` | built, CPU/GPU verified |
| `TypeSafeChoiceReranker` | `src/Vectorize.Hindsight.Sdk.Reranking.TypeSafe/` | built, live-verified |
| `RecallAcrossBanksAsync` | `.../CrossBank/HindsightClient.CrossBank.cs` | built, live-verified |
| Mental-model operations | `.../Operations/HindsightClient.MentalModels.g.cs` | **built (generated)** |
| Bank classifier | — | **does not exist** |

Mental-model operations are already generated from the spec and need no new transport work:

```
ListMentalModelsAsync(bankId, ListMentalModelsQuery?, ct)   -> MentalModelListResponse
GetMentalModelAsync(bankId, mentalModelId, GetMentalModelQuery?, ct) -> MentalModelResponse
CreateMentalModelAsync / UpdateMentalModelAsync / DeleteMentalModelAsync / ClearMentalModelAsync
RefreshMentalModelAsync(bankId, mentalModelId, ct)          -> AsyncOperationSubmitResponse
GetMentalModelHistoryAsync / DryRunRefreshMentalModelAsync
```

**D1 — The classifier mirrors the reranker seam exactly.** `IBankClassifier` + `IBankClassifierFactory`
in the core, implementations in provider packages, selection by DI registration, no switch statement,
no configuration string. *Recommended*, and consistent with **P15**.

Rationale beyond consistency: the reranker seam is already reviewed, documented, tested, and has a
regression test for a registration-order bug. Reusing its shape means the classifier inherits those
lessons rather than rediscovering them.

---

## 2. The classifier contract

**D2 — The classifier returns a result object, not a bare list.**

```csharp
public interface IBankClassifier
{
    bool IsPassThrough => false;

    Task<BankClassificationResult> ClassifyAsync(
        string input,
        IReadOnlyList<BankClassificationTarget> banks,
        CancellationToken cancellationToken);
}
```

Where each target names the bank and the mental model to use (**P3**):

```csharp
public sealed record BankClassificationTarget(
    string BankId,
    string? MentalModelName,      // by name
    string? MentalModelId);       // or by id; ID wins if both given
```

And the result carries the decisions *and* the reasons (**P4**, **P5**):

```csharp
public sealed class BankClassificationResult
{
    public required IReadOnlyList<string> SelectedBanks { get; init; }
    public required IReadOnlyList<BankClassificationOutcome> Outcomes { get; init; }
    public bool IsPassThrough { get; init; }
    public double Threshold { get; init; }
}

public sealed class BankClassificationOutcome
{
    public required string BankId { get; init; }
    public required bool Selected { get; init; }
    public required BankClassificationReason Reason { get; init; }
    public double? Score { get; init; }          // null when not scored
    public string? MentalModelName { get; init; }
    public string? Failure { get; init; }        // fetch/parse failure, when any
}

public enum BankClassificationReason
{
    PassThrough,          // no classification was performed
    ModelApplies,         // scored at or above threshold
    ModelDoesNotApply,    // scored below threshold
    NoMentalModel,        // no model available -> included per P4
    ModelUnavailable,     // model named but not found/fetchable -> included per P4
}
```

*Recommended.* The reason enum is what makes **P5** real: without it a caller sees "three banks
selected" and cannot tell whether the classifier worked or the fallback fired.

**D3 — The simple classifier is a pass-through, and reports itself as one.**

`IsPassThrough => true`, every bank selected with `Reason = PassThrough`, `Threshold` unused. It
performs no I/O and allocates nothing significant. *Recommended* — **P6**.

Following the reranker precedent, the pipeline **skips a pass-through classifier entirely** rather
than running it and discarding the result, and the result says so, so a caller can distinguish "every
bank was chosen deliberately" from "nothing was classified."

---

## 3. The reranker change

**D4 — `IdentityReranker` is retained; a new `ConfidenceReranker` becomes the simple default.**

The existing `IdentityReranker` is referenced in documentation, tests, and the pipeline's
`IsIdentity` short-circuit. Deleting it would be a breaking change for no benefit. Instead:

- `IdentityReranker` — unchanged, still available, still the explicit "no judgement" choice.
- `ConfidenceReranker` — **new**, the behaviour P7 asks for: filter low-confidence, order by relevance.
- The *client's* default becomes `ConfidenceReranker`.

*Recommended*, and it satisfies **P8** by leaving the existing contract and semantics untouched.

**D5 — The confidence threshold applies only to scores produced by one model over one candidate set.**

This follows directly from the measurement recorded in
[`docs/choosing-a-reranker.md`](../docs/choosing-a-reranker.md): a bank's retrieval score is
meaningful only within that bank's own pipeline, so a threshold on raw retrieval scores across banks
compares quantities that are not comparable.

Concretely:

| Situation | Behaviour |
| --- | --- |
| A real reranker ran (`RerankApplied`, `RerankScore` populated) | threshold on `RerankScore`, drop below cutoff, order by it |
| No reranker ran (identity / pass-through) | **no filtering**; order by interleaved `Rank` |

*Recommended* — this is product open item **O2(c)**.

A caller who wants filtering on a reranker's output gets it; a caller with no reranker gets a
defensible order and no silent deletion of candidates that were never comparable in the first place.

**D6 — The reranker may be given a mental model per bank (P9), but the simple reranker does not use one.**

`ConfidenceReranker` is pure C# with no model and no I/O. The mental-model input is a property of the
*provider* rerankers (ONNX), which already accept options; it is additive there, not in the simple path.
*Recommended.*

---

## 4. Mental-model retrieval

**D7 — By name is list-and-match, because the API has no name filter.**

Verified against the generated operation: `ListMentalModelsAsync` emits only `tags`, `tags_match`,
`detail`, `limit`, `offset`. There is no `name` parameter. `GetMentalModelAsync` takes an ID only.

So the library adds a resolving helper:

```csharp
Task<MentalModelResponse?> ResolveMentalModelAsync(
    string bankId, string? id, string? name, CancellationToken ct);
```

Behaviour (**P10**, **P11**):

1. `id` supplied and present → `GetMentalModelAsync(bankId, id)`.
2. `id` supplied but not found → return null (do **not** silently fall back to name).
3. `name` supplied → `ListMentalModelsAsync(bankId, detail: content)`, match on `Name` with
   **ordinal** comparison, return the **first** match.
4. Not found → null.

*Recommended.* The id-then-name precedence and the no-fallback rule matter: a caller who named a
specific ID and got a different model back by name would have a silent wrong-answer bug.

**D8 — `detail=content` is required for classification and reranking.**

`detail` defaults to `metadata`, which omits `content`. A metadata-only fetch yields a model with
nothing to classify against. All resolving helpers used by the classifier and reranker request
`content`. *Recommended.*

**D9 — Names are matched ordinally, not case-insensitively.**

Ordinal keeps the rule dead simple and predictable, and matches the "first match wins" instruction
(**P11**). Recorded so a future caller is not surprised that `"Coach"` and `"coach"` are different.
*Recommended*; trivially changeable if case-insensitive is preferred.

---

## 5. The mental-model cache

**D10 — One shared cache, keyed by (bankId, modelId) with a name index.**

Shared between classifier and reranker (**P13**). Keying on the resolved **ID** rather than the name
means a caller naming a model by name and another naming it by ID hit the same entry. The name index
is a secondary map for lookup.

*Recommended.*

**D11 — Refresh is bounded by the server's own signals, not a bare timer.**

The API exposes per-model `is_stale` and `last_refreshed_at`, and the model's trigger carries
`min_refresh_interval_seconds` and `refresh_cron`. Measured today, `is_stale` is **true on every model
inspected** (4 of 4 on `502225909`, 21 of 21 on `telegram_rob`), so it cannot be the sole trigger or
the cache would refetch constantly.

Policy (**P12**, product open item **O3**):

- **Minimum interval** — `min_refresh_interval_seconds` when present, else a client default (30 min).
- **Refetch** when the minimum interval has elapsed **and** (`is_stale` is true **or** the client TTL
  has expired).
- **Lazy** — refresh happens on next use, not on a background timer, so an idle cache costs nothing.
- Content is **retained** during a failed refresh: a stale model is better than no model.

*Recommended* — product open item **O3(c)**.

**D12 — Pre-caching at startup is opt-in and never blocks startup.**

Warming every model for every bank is one list call per bank plus one content fetch per model — over
40 models across the current banks. *Recommended*: lazy on first need, with an optional
non-blocking warm (**O4**).

**D13 — The cache is injectable and has a simple default.**

Following the seam pattern: an in-memory implementation in the core, injectable so a host can swap in
a durable or distributed one. No cache is required for the simple classifier or the simple reranker —
only provider classifiers touch it at all. *Recommended*.

---

## 6. The ONNX classifier — model shape

This is the decision with the most technical consequence, and it is driven by a measurement.

**Measured: real mental models are far too large for the reranker's model.**

| Model (bank `502225909`) | Size |
| --- | --- |
| Routines & Schedule | 7,840 chars |
| Coach | 5,346 chars |
| User Profile | 3,890 chars |
| Active Tasks & Commitments | 2,893 chars |

`bge-reranker-v2-m3` is capped at **512 tokens**. Even the smallest model here is many times that, so
feeding model content to a cross-encoder truncates the majority of it — and the discarded tail is
where the distinguishing detail sits. **The ONNX classifier cannot reuse the reranker's model as-is.**

**D14 — The ONNX classifier uses embedding similarity (a bi-encoder), not the reranker's cross-encoder.**

- The mental model is embedded **once** and the vector cached alongside the content.
- The user input is embedded per query.
- Similarity above the caller's threshold (P2) means "this model applies".

*Recommended* — product open item **O1(a)**.

Why this shape fits: the expensive half (the model document) is precomputed and cached, so the
per-query cost is one short embedding plus a dot product; and semantic similarity over a long document
is exactly the question P2 asks, whereas a cross-encoder answers a different question ("does this
passage answer this query?") about a passage that no longer fits.

**D15 — This requires a second local model, because the API has no embedding endpoint.**

Verified: no `embed` operation exists in the 87-operation surface. Embeddings are server-side only.
So the ONNX classifier needs its own small local embedding model — a second download, a second memory
footprint, and a second thing to configure.

Consequence for sizing: this argues for a **small** embedding model rather than a large one. The
classifier's job is coarse ("does this bank's purpose apply to this input"), not fine ranking, and the
existing ONNX reranker already provides the fine-grained judgement downstream. *Recommended*, with the
model choice left to the caller via `ModelPath` exactly as the reranker does it.

**D16 — The descriptor approach (O1b) is the documented fallback.**

If a second model is unacceptable, a short "what this bank is about" descriptor can be extracted from
each mental model and scored with the existing cross-encoder. Recorded as the fallback because it
trades fidelity for one fewer dependency. *Recommended as fallback only.*

---

## 7. Provider packaging

**D17 — `Vectorize.Hindsight.Sdk.Classification.Onnx`, separate from the reranker packages.**

Mirrors the established layout: seam in the core, implementation in a package with its own
dependencies, so a caller who wants neither pays for neither. The classifier package and the reranker
package may share a local embedding model *only* if the caller configures it so; they are independent
by default (**P17**). *Recommended.*

**D18 — Registration helpers live in the DI package, not the provider packages.**

Already established: provider packages register with `TryAddSingleton` directly so they take no
dependency on the DI package. The classifier follows this, including the `DynamicallyAccessedMembers`
annotation a generic registration needs for the DI package's AOT claim. *Recommended.*

**D19 — The classifier is skipped when it is a pass-through.**

Mirrors the reranker's `IsIdentity` short-circuit so the default costs nothing. *Recommended* — **P6**,
**P16**.

---

## 8. Where it plugs into cross-bank recall

**D20 — Bank pre-selection happens before the fan-out, in the existing entry point.**

`RecallAcrossBanksAsync` already takes an explicit bank list. The classifier's output becomes that
list, with no change to the fan-out, merge, or provenance code. *Recommended.*

That keeps the two features independent: a caller can use the classifier and no reranker, a reranker
and no classifier, both, or neither. The pipeline stays:

```
input -> classify -> N banks (concurrent) -> merge with provenance -> rerank -> ordered results
```

**D21 — The classifier's selection is recorded on the result, so a caller can see what happened.**

Following the precedent of `IsPartial` / `RerankApplied` / `Failures`: the recall result exposes which
banks were classified in, which were passed through, and which were left out. *Recommended* — **P5**.

---

## 9. Testing requirements

**D22 — Contract tests mirror the reranker suite's structure.**

- Simple classifier: pass-through returns every bank, reports `IsPassThrough`, performs no I/O.
- Threshold behaviour: above passes, below filters, exact-boundary defined.
- Fallback: no model → included with `Reason = NoMentalModel` (never silently excluded).
- Name resolution: first match wins on duplicates; ID beats name; missing ID does not fall back.
- Cache: second call does not re-fetch; refresh respects the minimum interval; a failed refresh keeps
  the old content.
- Registration: order-independence, including the regression that bit the reranker.

**D23 — The ONNX classifier is verified against the same kind of reference used for the ONNX reranker.**

The reranker was verified by comparing ids and scores against the Hugging Face reference (ids exact,
scores to 7.8e-09). The classifier gets the equivalent: embeddings compared against a reference
implementation, and similarity ordering asserted on known-related and known-unrelated pairs.

**D24 — Provider verification records which provider actually ran.**

Standing lesson from the reranker benchmark, where a silent CPU fallback produced a wrong published
conclusion: any classifier or reranker test that asserts on quality must also assert that the intended
provider executed. *Recommended*, and non-negotiable given the history.

**D25 — Ranking and classification quality are measured, or explicitly declared unmeasured.**

Currently, ranking quality is unmeasured for both existing rerankers because the live tests had one
obviously-correct and one obviously-irrelevant passage. The classifier has the same gap: correctness
of "does this model apply" needs labelled examples. Until then, the docs must say **unmeasured**
rather than implying the ordering is good. *Recommended.*

---

## 10. Decisions summary

| ID | Decision | Status |
| --- | --- | --- |
| D1 | Classifier mirrors the reranker seam; DI-selected, no switch | recommended |
| D2 | Classifier returns a result object with per-bank reasons | recommended |
| D3 | Simple classifier is pass-through and reports itself as one | recommended |
| D4 | Keep `IdentityReranker`; add `ConfidenceReranker` as the simple default | recommended |
| D5 | Threshold only on single-model scores; no filtering without a reranker | recommended (O2c) |
| D6 | Simple reranker uses no mental model; providers may | recommended |
| D7 | Name lookup is list-and-match; ID beats name; no id→name fallback | recommended |
| D8 | `detail=content` always requested for classification | recommended |
| D9 | Name comparison is ordinal | recommended |
| D10 | One shared cache keyed by (bankId, modelId) with a name index | recommended |
| D11 | Refresh bounded by `min_refresh_interval_seconds` + `is_stale`; lazy; keep stale on failure | recommended (O3c) |
| D12 | Pre-caching opt-in, never blocks startup | recommended (O4) |
| D13 | Cache injectable, in-memory default | recommended |
| D14 | ONNX classifier uses embedding similarity, not the reranker's cross-encoder | recommended (O1a) |
| D15 | Requires a second, small local embedding model (no server endpoint exists) | recommended |
| D16 | Descriptor + cross-encoder documented as the fallback | recommended fallback |
| D17 | Classifier provider in its own package | recommended |
| D18 | Registration helpers in the DI package, `TryAdd`, AOT-annotated | recommended |
| D19 | Pass-through classifier is skipped, not run and discarded | recommended |
| D20 | Classification plugs in before the existing fan-out | recommended |
| D21 | Selection recorded on the recall result | recommended |
| D22 | Contract tests mirror the reranker suite | recommended |
| D23 | ONNX classifier verified against a reference implementation | recommended |
| D24 | Tests assert the intended provider actually ran | recommended |
| D25 | Quality is measured or declared unmeasured | recommended |

---

## 11. Prerequisite work on the core library

Before either feature lands, the following must hold for the **bare Hindsight library** — the
generated surface plus the hand-written seams. This is the second half of the request ("make sure the
bare Hindsight library is 100% ready") and it is tracked here because both new features depend on it.

| # | Item | Why it blocks |
| --- | --- | --- |
| C1 | `ResolveMentalModelAsync` (by id or name) does not exist | Both features need it; D7 defines it |
| C2 | `detail=content` is not defaulted anywhere for these paths | Classification has nothing to read (D8) |
| C3 | No mental-model cache or refresh policy | P12, P13 |
| C4 | No `IBankClassifier` seam | D1, D2 |
| C5 | `ConfidenceReranker` does not exist | P7, D4 |
| C6 | No classifier provider package | D17 |
| C7 | Surface-coverage guard counts only spec operations | A new composition must declare itself, as cross-bank recall does |
| C8 | ~~Mental-model operations unexercised~~ — **claim was wrong, corrected** | All 9 are exercised in `AllOperationsTests.cs` (lines 216–224). No gap. Recorded because the claim was made and then disproved by checking |
| C9 | `requirements/` was not in the repo; this document and its companion create it | Documentation completeness |

**Note on C8:** mental models are reachable through the generated client, but no test exercises them
today. The library's coverage claim is about *operations*, and these are operations. They should be
covered like the other 87 before the claim is repeated.
