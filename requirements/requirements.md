# Vectorize.Hindsight.Sdk — Product Requirements

Status: **draft, awaiting sign-off**
Created: 2026-09-19
Scope: bank pre-selection (classification) and post-query reranking, both driven by Hindsight
mental models.

This document records *what* the library must do and *why*. The companion
[`technical-requirements.md`](technical-requirements.md) records *how*, and the decisions ledger for
both lives at the end of each document. Where a requirement has an unresolved choice, the choice and
its options are stated rather than assumed.

---

## 1. Background

The library already supports **cross-bank recall**: one call fans out to N banks concurrently, merges
the results with provenance, and returns an ordered candidate set
([`docs/choosing-a-reranker.md`](../docs/choosing-a-reranker.md)).

Two gaps remain, and they are at opposite ends of the same pipeline:

```
user input ──[ ? ]──> N banks ──> merged candidates ──[ ? ]──> ordered results
                    ↑                                          ↑
            nothing selects which banks              reranking exists but its
            to query — the caller passes all          default does no ranking
```

1. **Nothing decides which banks to query.** The caller supplies a bank list and every bank is
   queried. That is wasteful (N recall calls), dilutes the candidate pool, and gives no way to say
   "this question is about the vault, not my personal notes."
2. **The default reranker does not rank.** `IdentityReranker` preserves merge order. That is a
   defensible default — it assumes nothing about cross-bank score comparability — but it is not a
   *ranking*, and callers who want one must opt into ONNX or TypeSafe.

This work adds a **classifier** for the inbound side and gives the reranker a **simple ranking
default** for the outbound side, both configurable and both driven by the mental models that already
live in Hindsight.

---

## 2. Scope

**In scope**

- A bank **classifier** abstraction: given a user input and a list of banks, return the subset worth
  querying.
- A reranker **simple default** that drops low-confidence candidates and orders by relevance.
- **ONNX implementations** of both, as optional provider packages.
- **Mental-model retrieval** from Hindsight by ID or by name, as the input to both.
- A **local cache** of mental models with refresh-over-time behaviour.

**Out of scope**

- Changing how banks are stored, authorized, or provisioned.
- Server-side changes to the Hindsight API. (Two limitations found during reconnaissance are recorded
  in §7 as constraints, not requests.)
- Changing recall, retain, or consolidation behaviour.

---

## 3. The classifier

**P1 — The classifier returns a set, not a single bank.**

It takes the user input and a list of candidate banks, and returns **one or more** of them, up to the
full list. It must be able to return all of them (a broad question legitimately spans every bank) and
must be able to return exactly one.

Rationale: a query can genuinely need several banks, so forcing a single choice would be arbitrary.
Returning a set also composes directly with the existing cross-bank fan-out, which already queries N
banks concurrently.

**P2 — Inclusion is threshold-gated on "does this mental model apply to this input".**

For each bank the classifier asks whether that bank's mental model applies to the user input. A bank
is included when the answer is a **significant probability of YES — on the order of 80–90%**, with the
exact threshold caller-configurable.

The threshold is a policy, not a constant: too low and every bank is queried (no benefit); too high
and relevant banks are missed, which is the harder failure to notice.

**P3 — The classifier is driven by a caller-designated mental model per bank.**

The caller supplies, for each bank, **which mental model to use**. The classifier does not guess and
does not require a naming convention.

```
classify(input, [
    { bank: "obsidian_rob",  model: "bank-purpose-and-goals" },
    { bank: "telegram_rob",  model: "overall-mission-vision-values" },
])  ->  ["obsidian_rob"]        // example
```

The two model names above are illustrative. Any model in that bank may be named.

**P4 — Banks with no mental model must not be silently dropped.**

Reconnaissance found that **only 3 of 8 production banks have any mental models at all** (§7). A bank
with no model cannot be classified, and the two ways to handle that are opposite failures:

- **Include it** — safe, but it is queried on every request, which is the inefficiency this work
  exists to remove.
- **Exclude it** — fast, but a relevant bank vanishes with no signal.

**Requirement:** the default is to **include** unclassifiable banks, and the outcome must be
**visible** — the caller can see which banks were included because they were classified positively and
which were included because they could not be classified. Silently excluding a bank is not acceptable.

**P5 — Classification is advisory and measurable.**

The classifier's selection is a hint, not an authorization decision. It cannot grant access to a bank
the caller is not already permitted to query, and it must not be the only thing standing between a
query and a bank.

The result must expose: which banks were selected, which were not, and why (classified yes, classified
no, or no model available). This is what makes a poor result diagnosable — an empty selection points at
bad model labels, not at a weak model.

**P6 — A simple classifier exists and is the default.**

The default classifier performs **no classification**: it passes the caller's bank list through
unchanged, so every bank is queried. This is the current behaviour, and it remains the default because
it requires no mental models, no local inference, and no configuration — it works out of the box.

Naming and shape mirror the reranker seam: a caller-supplied object, or a DI registration, with the
simple implementation as the fallback when nothing is registered.

---

## 4. The reranker

**P7 — A simple reranker drops low-confidence candidates and orders by relevance.**

The existing default (`IdentityReranker`) makes no judgement and preserves merge order. The requested
simple behaviour is different: **filter out low-confidence candidates, then order the survivors by
relevance to the input query.**

This becomes the new simple default. It is pure C#, needs no model, and is the behaviour a caller gets
with no configuration.

**P8 — The existing reranker contract is preserved.**

`IReranker` and the three current implementations (identity, ONNX, TypeSafe) already exist and are
live-verified. This work changes the *simple default's behaviour*, not the seam, and must not break
existing callers or the documented `RerankApplied` / `RerankScore` semantics.

**P9 — The reranker is also driven by a caller-designated mental model per bank.**

Symmetrically with the classifier, the reranker may be given a mental model per bank — for example a
"what relevant material looks like" model — which shapes what it treats as relevant. This is the
mechanism by which reranking quality improves over time without a code change.

---

## 5. Mental models

**P10 — Mental models are retrievable by ID or by name.**

The core library must support both. ID is the precise path. **Name is the ergonomic path**, and
reconnaissance confirmed the API has **no name filter** — the list operation filters on tags, detail,
limit and offset only. Name lookup is therefore implemented as list-and-match, which the library must
do correctly and predictably, including a defined rule for duplicate names.

**P11 — Duplicate names resolve to the first match.**

Stated explicitly so the behaviour is not accidental. Names are not guaranteed unique; IDs are.

**P12 — Mental models are cached locally, and refreshed over time.**

Fetching a mental model on every request would add a round trip to every classification and every
rerank. The library must cache them locally and re-pull on a sensible schedule, so that as a mental
model **grows and mutates over time**, classification and reranking improve without any code change.

This is the central motivation for the whole design: the mental model is the living input, and the
library's job is to use its current state rather than a snapshot frozen at build time.

**P13 — The cache is shared between classifier and reranker.**

Both consume mental models from the same banks. One cache, one refresh policy, one set of fetch
bookkeeping — not two independent caches that can disagree or double-fetch.

---

## 6. Providers and configuration

**P14 — ONNX implementations exist for both classifier and reranker, as optional packages.**

Following the existing reranker precedent: the seam lives in the core, the implementation lives in a
package with its own dependencies, so a caller who does not want local inference takes no dependency
on it. Local inference is the right shape for the classifier because it runs on **every** request,
before any recall — a remote call there would add latency and cost to every query.

**P15 — Both are selected by DI registration, not by configuration string or switch statement.**

Established already for rerankers and to be preserved exactly: no appsettings selector, no provider
enum, no switch. A provider package registers its own factory; the registration *is* the selection.

**P16 — Both have a simple default that requires nothing.**

A caller with no mental models, no local models and no configuration gets working behaviour: all banks
queried, and results ordered by the simple reranker. Every capability added by this work is opt-in.

**P17 — The ONNX classifier and the ONNX reranker are separate models.**

They are different tasks (§7) and must not be assumed to share one model or one file.

---

## 7. Constraints found during reconnaissance

These are observations about the real system, not requirements. They shape the technical design and
are recorded here so the product requirements are not read in isolation.

| Finding | Consequence |
| --- | --- |
| **Only 3 of 8 production banks have mental models.** `502225909` (4), `telegram_rob` (21), `hermes_agentry` (20); `hermes_rob`, `obsidian_rob`, `hermes_john`, `192`, `106` have none. | P4's fallback is not an edge case — it is the majority case today. The feature's value grows as models are authored. |
| **Mental model content is large** — `Routines & Schedule` is 7,840 chars, `Active Tasks & Commitments` 2,893. | Far beyond a 512-token cross-encoder. Drives the model-shape decision in the technical requirements. |
| **No name filter on the list operation.** | P10's name lookup is list-and-match, not a server query. |
| **No embedding operation exists in the Hindsight API.** | An embedding-based classifier needs its own local model — a second model download, not a server call. |
| **Mental models carry `is_stale` and `last_refreshed_at`**, and their triggers carry `min_refresh_interval_seconds` and a `refresh_cron`. | Server-side signals exist for P12's refresh policy — richer than a client-side timer. |
| **Stale is currently the norm** — all four models on `502225909` and all 21 on `telegram_rob` report `is_stale: true`. | `is_stale` alone cannot trigger a re-fetch or the client would refetch constantly. |
| **`refresh_mental_model` is asynchronous** — submit returns an operation ID to poll. | A cache warm-up is not a single call. |

---

## 8. Requirements summary

| ID | Requirement |
| --- | --- |
| P1 | Classifier returns a set (1..N banks, up to the full list) |
| P2 | Inclusion threshold-gated at ~80–90% "does this model apply", caller-configurable |
| P3 | Caller designates a mental model per bank for classification |
| P4 | Banks without a model default to included, and the reason is visible |
| P5 | Classification is advisory and measurable; cannot widen access |
| P6 | Simple classifier (pass-through) is the default |
| P7 | Simple reranker drops low-confidence candidates and orders by relevance |
| P8 | Existing `IReranker` contract and `RerankApplied` semantics preserved |
| P9 | Caller may designate a mental model per bank for reranking |
| P10 | Mental models retrievable by ID or by name |
| P11 | Duplicate names resolve to the first match |
| P12 | Mental models cached locally, refreshed over time |
| P13 | One shared cache for classifier and reranker |
| P14 | ONNX providers for both, in optional packages |
| P15 | Selection by DI registration, never a switch or config string |
| P16 | Simple defaults require no configuration |
| P17 | ONNX classifier and reranker are separate models |

---

## 9. Open items

Each carries a recommendation. None blocks writing the technical requirements; all block
implementation.

**O1 — Model shape for the ONNX classifier.** Mental models are far too long for a 512-token
cross-encoder (§7), so the classifier cannot reuse the reranker's model naively.

- (a) **Embedding similarity** — embed each mental model once and cache the vector; embed the input
  per query; compare. Natural fit for long documents, cheap per query because the expensive half is
  cached. **Needs a second local model** — there is no server-side embedding endpoint.
- (b) **Distilled descriptor** — extract a short "what this bank is about" from each model and reuse
  the cross-encoder. One model for both tasks, but the model actually classifies a summary.
- (c) **Chunk-and-aggregate** — score model chunks, take the max. Highest fidelity, most compute, and
  the aggregate step is arbitrary.

*Recommendation: (a), with (b) as the fallback if the second model's footprint is unacceptable.*

**O2 — Behaviour of the simple reranker's "drop low confidence".**

Cross-bank retrieval scores are **not comparable across banks** — this is the central finding behind
the existing default. So a single global score threshold is suspect.

- (a) **Per-bank threshold** on each bank's own score, then order survivors by interleaved rank.
- (b) **Global threshold** on the raw score across all candidates.
- (c) **Threshold on `RerankScore` only** when a real reranker ran; otherwise order by rank and do not
  filter at all.

*Recommendation: (c) — it keeps the filtering honest, since a threshold is only meaningful on a score
produced by one model over one candidate set.*

**O3 — Cache refresh policy.** `is_stale` is true almost everywhere, so it cannot be the sole trigger.
The API also exposes `min_refresh_interval_seconds` and a `refresh_cron` per model.

- (a) Client TTL (e.g. 30 minutes), refreshed lazily on next use.
- (b) Honour the server's `min_refresh_interval_seconds` as the floor and `is_stale` as the trigger.
- (c) Both, with (b) taking precedence.

*Recommendation: (c).*

**O4 — Pre-caching at startup.** Warming every mental model for every bank means one list call per
bank plus one content fetch per model (45+ models across the current banks), for banks a given query
may never touch.

*Recommendation: lazy on first need, with an optional non-blocking warm after startup. Not blocking
startup.*

---

## 10. Definitions

- **Bank** — an independent Hindsight store, isolated for ownership, permissions, trust, lifecycle or
  source.
- **Classifier** — the inbound component that selects which banks to query.
- **Reranker** — the outbound component that filters and orders the merged candidates.
- **Mental model** — a named, server-refreshed synthesis stored in a bank, retrievable by ID or name,
  whose content grows and mutates over time.
- **Simple** — an implementation requiring no models, no configuration and no network, used as the
  default.
- **Provider** — an implementation in its own package (ONNX, TypeSafe), selected by DI registration.
