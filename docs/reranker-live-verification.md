# TypeSafe reranking, verified live

Two separate things were built, and this records what each actually is and how far it was verified.
Written after a live run, because the earlier claim ("verified against the documented answer shape")
was accurate but weaker than it sounded, and the difference mattered.

## What exists

Three rerankers behind one seam (`IReranker`), all in the repository and all unit-tested:

| Implementation | File | Cost | Needs |
| --- | --- | --- | --- |
| `IdentityReranker` | `src/Vectorize.Hindsight.Sdk/Reranking/Providers/IdentityReranker.cs` | free | nothing |
| `OnnxReranker` | `src/Vectorize.Hindsight.Sdk.Reranking.Onnx/OnnxReranker.cs` | free per call | 570 MB model |
| `TypeSafeChoiceReranker` | `src/Vectorize.Hindsight.Sdk.Reranking.TypeSafe/TypeSafeChoiceReranker.cs` | one model call per recall | a TypeSafe key |

Selection is by DI registration (`IRerankerFactory`), with no switch statement over providers.

## The live verification

Run against the local rig (`127.0.0.1:8888`) and the real TypeSafe API, with **no stub anywhere**: a
real `JevClient`, a real `TypeSafeChoiceReranker`, real cross-bank recall over three seeded banks.

Query: *"when did the billing platform migrate to PostgreSQL?"*

```
merge order (identity reranker)          live TypeSafe Choice rerank
  2  ts-c  coffee machine                  0.58  ts-a  billing migrated to PostgreSQL
  3  ts-a  billing migrated to PostgreSQL  0.41  ts-a  billing migrated to PostgreSQL
  4  ts-b  nightly reconciliation          0.01  ts-c  coffee machine
  5  ts-c  Carol Vance, security lead      0.00  ts-b  nightly reconciliation
  ...                                      ...
```

- The correct passage moved from **position 3 to position 1**.
- The irrelevant coffee-machine passage was pushed down to 0.01.
- **One** `JevClient` call ranked all ten candidates — the whole reason Choice beats a per-candidate
  scorer. `JevClient calls: 1`.
- `LastDiagnostics`: mode `Choice`, 10 candidates, 3 carrying probability, `discriminated: True`.
- Provenance and the relevance score both survived into the rendered context.

The response reported `model: jev-1.13.0` for the `jev-latest` alias, and the answer shape matched
the vendor's documentation exactly (`choice`, `probabilities` over every option, `confidence`).

## What this does and does not establish

**Establishes:** the provider works end to end against the live API, the adapter shape in the package
README compiles and runs against the real `Jev.Sdk`, one call ranks the whole pool, and the ranking
moves the right passage to the top on a query with one clearly correct answer.

**Does not establish:** ranking quality on a hard query. This test has one obviously correct passage
and one obviously irrelevant one, so it proves the mechanism rather than the judgement. Whether
Choice orders *near-miss* candidates well is an open question that would need labelled data — and the
same is true of the ONNX reranker, where the ordinal-not-calibrated behaviour was measured but not
scored against ground truth.

**Also not established:** the `Noul` mode's usefulness. Its scores are independent judgements, which
is why it is not the default, and it has only been exercised through a stub.

## Reproducing it

The harness used here is scratch. To rebuild it, reference the three projects and mirror
`JevChoiceAsker` from the package README:

```csharp
var asker = new JevChoiceAsker(jevClient, model: null);
var reranked = await hindsight.RecallAcrossBanksAsync(
    query, [bankA, bankB, bankC],
    new CrossBankRecallOptions { PerBankLimit = 8, Reranker = new TypeSafeChoiceReranker(asker) },
    ct);
```

Requirements: the rig up (`test-infra/up.sh`) and `TYPESAFE_API_KEY` in the environment, which
`JevClient` reads by that exact name (`JevEnvironment.ApiKeyVariable`).

**The key is never committed, never logged, and never placed in a repository.** The live run above
used a mode-600 file outside both repos, and both repos were checked afterwards — including their
git histories — for any trace of it.

## The gap that remains

`score` would likely serve reranking better than `choice`. A Score question returns a position along
ordered levels you define, and its `score` is already a probability-weighted mean, so it is a single
comparable number per candidate without requiring the candidates to be mutually exclusive. With more
than a handful of candidates, a Choice distribution has to spread across all of them, which is why
3 of 10 carried probability in the run above. A Score mode is a contained addition.
