# How to choose your reranking for cross-bank recalls

This document covers what cross-bank recall is and why it is hard, what the reranking choices are,
when each is right, how to obtain and configure each one, what they cost in time and memory, and
**what is not yet measured**. Every number here was measured on this repository's own hardware; where
a figure is an estimate or a single sample, it says so.

---

## What cross-bank recall is

Hindsight stores memories in **banks**. A bank is an independent store with its own content,
its own configuration, and its own retrieval pipeline:

```
query ──┬─ Bank A ─ recall ─┐
        ├─ Bank B ─ recall ─┼─→ candidates → merge → ordered context
        └─ Bank C ─ recall ─┘
```

Banks are usually separated for a reason:

- **Ownership** — one bank per person, per team, per tenant.
- **Permissions** — a bank you may read but not write.
- **Trust level** — canonical records in one bank, rough notes in another.
- **Lifecycle** — current work in one bank, an archive in another.
- **Source** — a vault index, a chat transcript, a document store.

The point of keeping them separate is that **isolation is the storage and permission boundary**.
Nothing is merged at write time, no bank has to know about another, and a mistake in one cannot
corrupt another.

But a question often spans them. *"What do we know about the billing migration?"* may be answered in
part by your personal bank, in part by a team bank, and in part by an archive. Answering it means
querying each and combining what comes back — which is a different operation from recall, with its own
policy and its own failure modes.

### Why it is hard

**Retrieval scores are not comparable across banks.** This is the whole difficulty, and it is not
obvious. Each bank's score is produced by that bank's own pipeline, over its own content
distribution, with its own settings. A 0.93 from a small curated bank is not the same quantity as a
0.93 from a large noisy one.

So the tempting thing — concatenate every bank's results and sort by score — asserts something usually
false. Bank C's 0.93 outranking Bank A's 0.91 tells you nothing about which passage better answers
the question; it tells you that two different pipelines happened to emit similar-looking numbers.

**A large bank crowds out a small one.** Without a per-bank cap, a bank with 10,000 documents fills
the candidate pool and the small authoritative bank never gets a look.

**The same fact can live in several banks.** A document indexed into two banks yields two candidates
for one piece of evidence, which wastes context and makes one fact look like three.

**One bank being unreachable is not a total failure.** A bank can be missing, unauthorized, or down.
Discarding the banks that answered turns one outage into a complete retrieval failure.

### What this library does about it

| Problem | Treatment |
| --- | --- |
| Scores not comparable | Default order **interleaves by each bank's own rank** (A1, B1, C1, A2…), which assumes nothing. `ScoreDescending` and `BankOrder` are explicit opt-ins. |
| One bank dominating | `PerBankLimit` caps each bank after its own ranking; `MaxPerBank` bounds a bank's share of the final set; `MinPerBank` guarantees coverage. |
| Duplicates | Identity-only deduplication, with every bank recorded on the survivor. |
| Partial failure | Default is partial: the banks that answered still return, and `IsPartial` plus `Failures` make the loss explicit. |
| Cross-bank relevance | **This is what reranking adds** — the second stage, below. |

The two stages, in the design's own terms:

```
first stage:   "What looks relevant inside this bank?"      <- the bank's native recall
second stage:  "Of everything retrieved from every bank,
                what is most useful for this query?"        <- the reranker
```

Only the second stage compares candidates against each other under one query and one model, which is
why it is the only comparison that is meaningful across banks.

### Pros and cons

**Pros**

- **Isolation stays intact.** Banks remain separate for storage, permissions and lifecycle; only the
  query-time view is combined. Nothing is copied, merged, or migrated.
- **No write-time coupling.** Adding a bank requires no coordination with the others and no
  reindexing.
- **Partial answers degrade gracefully.** Losing a bank narrows the answer rather than breaking it.
- **Provenance survives.** Every candidate knows which bank it came from, so an answer can be traced,
  conflicts resolved, and permissions enforced downstream.

**Cons**

- **Quality depends on the reranker.** Without one, the merged order is a defensible policy
  (interleaved by rank) but not a relevance ranking. It is honest, not smart.
- **Cost scales with the number of banks.** N banks means N recall calls, and a remote reranker adds a
  call on top.
- **Latency is the slowest bank**, not the average — the fan-out waits for all of them (or reports the
  ones that failed).
- **A reranker sees only what recall returned.** If a bank's own retrieval buried the right passage,
  no cross-bank ordering recovers it.
- **More moving parts to reason about**: per-bank caps, deduplication, partial failure, and the
  ordering policy all interact.

### When not to use it

If your memories live in one bank, use `RecallMemoriesAsync` directly. The fan-out and merge add
nothing when there is nothing to merge — the library rejects a single-bank call with that message
rather than pretending otherwise.

---

## The short answer

| Situation | Choose |
| --- | --- |
| You want the safest default and no new dependency | **Identity** (nothing to do — it is already the default) |
| High recall volume, low latency wanted, you control the deployment | **ONNX** (local cross-encoder) |
| You cannot ship model files or spend RAM, and can pay per call | **TypeSafe** (remote Choice) |
| You need to know each provider is available before committing | Start with Identity and add a provider later — the seam does not change |

The honest one-line summary: **ONNX is free per call but needs ~1 GB resident and a one-time 570 MB
download. TypeSafe needs neither but bills roughly 0.7k tokens per recall and adds a network dependency.
Identity costs nothing and ranks nothing** — it preserves the interleaved merge order, which is a
defensible policy rather than a relevance ranking.

Remember that a reranker only reorders what recall returned. It cannot recover a passage that a bank's
own retrieval buried, so a weak reranker is not a substitute for a well-configured bank.

---

## Configuration: three ways to supply a reranker

The library does not configure a reranker for you, and there is no appsettings key that selects one.
**Selection is your code** — either an object you pass, or a registration in your container. There is
deliberately no switch statement over provider names and no configuration string to typo.

### 1. In the constructor (client-wide default)

```csharp
using Vectorize.Hindsight.Sdk.Reranking.TypeSafe;

var reranker = new TypeSafeChoiceReranker(asker);
using var client = new HindsightClient(reranker);

// Every RecallAcrossBanksAsync on this client now reranks.
```

Or with the options overload:

```csharp
using var client = new HindsightClient(options, logger, reranker);
```

Passing `null` is valid and means the identity reranker — the client **always** has one, so no call
site ever null-checks it.

### 2. Per call (highest precedence)

```csharp
var result = await client.RecallAcrossBanksAsync(
    query, banks,
    new CrossBankRecallOptions { Reranker = reranker },
    cancellationToken);
```

Useful for reranking some queries and not others — a cheap factual lookup needs no reranker, a broad
synthesis question does.

### 3. Through dependency injection

```csharp
services.AddHindsight();

// Local ONNX — the model is loaded lazily on first use, not at startup.
services.AddOnnxReranker(new OnnxRerankerOptions
{
    ModelPath = "artifacts/reranker/model_int8.onnx",
    TokenizerJsonPath = "artifacts/reranker/tokenizer.json",
    SentencePieceModelPath = "artifacts/reranker/sentencepiece.bpe.model",
});

// or TypeSafe — requires an IChoiceAsker adapter, see that package's README.
services.AddTypeSafeReranker();

// or explicitly nothing, to mean it rather than rely on the absence.
services.AddIdentityReranker();
```

`HindsightClient` resolved from the container carries whichever provider was registered.

**Precedence, precisely** (`options.Reranker ?? Reranker`):

```
per-call on CrossBankRecallOptions   wins
  └─ else the client's constructor reranker
       └─ else the identity default
```

**Registration order does not matter.** An earlier revision had `AddHindsight` register the identity
factory eagerly, and since every registration uses `TryAdd` the first won — so `AddHindsight()` before
`AddTypeSafeReranker()` silently kept the no-op reranker and never ranked anything. That bug is fixed
and has a regression test asserting both orders agree.

### Telling "no reranking happened" from "reranking happened"

```csharp
result.RerankApplied          // true only when a non-identity reranker actually ordered the results
result.RerankFailure          // non-null when a reranker was supplied but threw
reranker.IsIdentity           // true for the default, which makes no judgement
```

A reranker that fails **does not fail the recall**. The candidates come back in pre-rerank order with
the degradation disclosed in `ToContext()`, because a ranking problem should not cost you the memories
you successfully retrieved.

---

## ONNX: the local cross-encoder

### What it is

A BGE-style cross-encoder running in-process through ONNX Runtime. Tested against
[`BAAI/bge-reranker-v2-m3`](https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX)
(Apache-2.0) — a multilingual reranker designed for exactly the "query + passage → relevance" step.

### Getting the model

**Nothing downloads automatically.** `ModelPath` is a required option and the constructor throws if
the file is absent:

```
HindsightConfigurationException: The ONNX model was not found: <path>
```

That is deliberate: a library that pulled 570 MB off the network in a constructor would surprise you,
would need its own retry/resume/proxy handling, and would fail in air-gapped deployments.

Fetch it with the script in this repository:

```bash
bash test-infra/fetch-reranker-model.sh int8   # 570 MB  (recommended)
bash test-infra/fetch-reranker-model.sh fp32   # 2.3 GB
```

It downloads to `artifacts/reranker/` (gitignored) and **verifies every file against the size the
source repository reports**, so a truncated download fails there rather than later as an unexplained
ranking difference. Set `HINDSIGHT_RERANKER_DIR` to override the location.

You need **all three files**. This is the part that surprises people:

| File | Size | Role |
| --- | --- | --- |
| `model_int8.onnx` | 570 MB | the model |
| `tokenizer.json` | 17 MB | the **vocabulary and special-token ids** |
| `sentencepiece.bpe.model` | 5 MB | the **segmentation model** |

Both tokenizer files are required because they disagree: the Hugging Face vocabulary inserts a
`<pad>` at index 1, shifting every content piece by one, and orders the special tokens differently
from SentencePiece. Segmentation comes from `sentencepiece.bpe.model` and each piece is mapped through
the vocabulary in `tokenizer.json`, so the mapping is derived from the model's own artifacts rather
than hardcoded. This was verified against the reference tokenizer — the ids match **exactly**, and the
scores match to 7.8e-09 (float32 rounding).

To fetch from elsewhere, the same files exist in the [BAAI repository](https://huggingface.co/BAAI/bge-reranker-v2-m3):
`tokenizer.json` and `sentencepiece.bpe.model`. For the fp32 export the graph and weights are split —
`model.onnx` plus `model.onnx_data` — and both must sit in the same directory.

### Configuring it

```csharp
services.AddOnnxReranker(new OnnxRerankerOptions
{
    ModelPath = "artifacts/reranker/model_int8.onnx",
    TokenizerJsonPath = "artifacts/reranker/tokenizer.json",
    SentencePieceModelPath = "artifacts/reranker/sentencepiece.bpe.model",

    ExecutionProvider = OnnxExecutionProvider.Cuda,   // CPU by default
    BatchSize = 16,
    MaxSequenceLength = 512,
});
```

| Option | Default | Notes |
| --- | --- | --- |
| `MaxSequenceLength` | 512 | The model's limit. Truncation keeps the query whole and trims the passage — a query cut in half scores everything wrongly. |
| `BatchSize` | 16 | Batching matters more for latency than anything else. Candidates are grouped by length first, so short passages are not padded to the pool's longest. |
| `ExecutionProvider` | `Cpu` | `Cuda` or `Auto` for GPU. See below. |
| `ApplySigmoid` | `true` | `false` gives the raw logit, which is linear and additive — better if you combine it with other terms. A monotonic transform does not change the ordering. |
| `MaxConcurrentSessions` | 1 | Raise only if measurements show the session idle. This also bounds memory. |
| `IntraOpThreads` | 0 (runtime chooses) | Pin it when several sessions share a machine. |
| `ValidateGraphAtStartup` | `true` | Runs one probe pair at construction, so a bad model or option fails at startup rather than on your first real query. |

### Measured cost

**Memory** — peak RSS, measured with `/usr/bin/time -v`, because the weights are **native memory held
by ONNX Runtime, not the .NET heap** (managed heap stays flat at 161 MB either way, so a GC-based
reading badly under-reports):

| Precision | File | Peak RSS | Load time |
| --- | --- | --- | --- |
| INT8 | 570 MB | **1,038 MB** (~1.0 GB) | 1.9 s |
| FP32 | 2.3 GB | **1,447 MB** (~1.4 GB) | 2.1 s |

FP32's footprint is well under its file size because the external-weights file is memory-mapped rather
than fully resident. `Dispose()` releases the session; the memory returns to the allocator, which may
not hand it straight back to the OS.

**Latency** — warm, single-threaded, CPU:

| Candidates | 1 | 5 | 10 | 20 | 50 |
| --- | --- | --- | --- | --- | --- |
| INT8 | 18 ms | 62 ms | 133 ms | 234 ms | 580 ms |
| FP32 | 44 ms | 178 ms | 370 ms | 679 ms | 1,623 ms |

First call after construction was 111 ms versus a 117 ms warm median, so there is no significant lazy
warmup penalty once loaded.

### GPU — verified, and slower than the CPU here

This path has now actually been run. It works, and **on this machine it is slower than the CPU**, which
is worth knowing before you spend a day configuring it.

Setting it up requires four things, and the middle two are where people get stuck:

**1. Reference the GPU package in your application.**

```xml
<PackageReference Include="Microsoft.ML.OnnxRuntime.Gpu" Version="1.23.2" />
```

**2. Pin the CPU package to the same version.** The reranker package pins
`Microsoft.ML.OnnxRuntime` (CPU), and NuGet resolves the shared `Microsoft.ML.OnnxRuntime.Managed`
assembly to the higher version. That managed assembly then loads the CPU native library even though
GPU natives sit right beside it — so CUDA never registers and the fallback is silent. Both sides must
be the same version, which for a consuming application means a direct reference:

```xml
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.23.2" ExcludeAssets="native" />
```

`ExcludeAssets="native"` is the other half: both packages ship a `libonnxruntime.so` and both would be
copied, with the CPU one winning. Excluding its native assets leaves the GPU build's.

**3. Match the CUDA major version to the ONNX Runtime version.** This is the trap that costs the most
time:

| ONNX Runtime | Needs | Available via pip? |
| --- | --- | --- |
| 1.30.x (what this library pins) | **CUDA 13** (`libcublas.so.13`) | **no** — `-cu13` wheels on PyPI are deprecated stubs |
| 1.23.x | **CUDA 12** (`libcublas.so.12`) | yes — `nvidia-cublas-cu12`, `nvidia-cudnn-cu12` |

So GPU work needs **1.23.x or older** unless you install a CUDA 13 toolkit some other way.

**4. Install the CUDA libraries** (cuDNN and cuBLAS — not part of the NuGet package):

```bash
uv pip install nvidia-cuda-runtime-cu12 nvidia-cublas-cu12 nvidia-cudnn-cu12 \
               nvidia-cufft-cu12 nvidia-curand-cu12
# then point the loader at them
export LD_LIBRARY_PATH="$(find ~/.venv -name '*.so*' -printf '%h\n' | sort -u | tr '\n' ':')$LD_LIBRARY_PATH"
```

Verify with `ldd libonnxruntime_providers_cuda.so | grep 'not found'` — an empty result means it is
ready. Then confirm the provider actually attached:

```csharp
OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider")
reranker.ProviderInUse       // must name the CUDA provider, not CPU
reranker.ProviderWarning     // null when the request was honoured
```

**A missing GPU falls back to CPU with a warning rather than failing.** Provider availability is read
from the runtime (`OrtEnv.GetAvailableProviders()`) rather than inferred from whether appending threw,
because appending a provider the runtime lacks can succeed and quietly produce a CPU session.

#### Measured: GPU is slower at every size tested

Intel RTX A5000 Laptop (16 GB), ONNX Runtime 1.23.2, INT8 model, CUDA 12 libraries, warm calls:

| Candidates | Batch | CPU | GPU (CUDA active) | GPU / CPU |
| --- | --- | --- | --- | --- |
| 10 | 1 | 227 ms | 272 ms | 0.83 |
| 10 | 32 | 138 ms | 240 ms | 0.57 |
| 50 | 1 | 1,442 ms | 1,579 ms | 0.91 |
| 100 | 1 | 3,362 ms | 2,732 ms | **1.23** |
| 200 | 1 | 4,767 ms | 5,605 ms | 0.85 |
| 400 | 1 | 8,985 ms | 12,698 ms | 0.71 |

**The GPU lost in 12 of 13 configurations**, and the one win (100 candidates, batch 1) is within the
run-to-run spread — these are single-run timings on a shared machine, so treat that row as noise rather
than a crossover.

The likely reason is the workload, not the setup. ONNX Runtime's own CUDA provider warns on this graph:

> `342 Memcpy nodes are added to the graph main_graph for CUDAExecutionProvider. It might have negative
> impact on performance (including unable to run CUDA graph).`

A cross-encoder scores query-passage pairs, and at these sizes the host→device→host transfer per call
can exceed the compute it saves. A GPU is the right answer for large batched throughput, not for
reranking 20–50 candidates per recall.

**Cost of the GPU session itself:** ~**721 MiB** of VRAM per CUDA session, peaking at ~1,438 MiB with
two sessions alive in one process. So it consumes VRAM *and* is slower here.

**Recommendation:** for cross-bank reranking at typical sizes, use the CPU. Revisit the GPU only if you
measure your own workload at a much larger batch and see a real win — and check
`ProviderInUse` when you do, because a silent fallback will make any comparison meaningless.

### Scores are ordinal, not calibrated

This is the most important caveat. Measured on this repository's candidates with the INT8 export:

```
0.291587  The billing platform migrated to PostgreSQL in March 2026.    <- the right answer
0.001811  Billing runs a nightly reconciliation job at 02:00 UTC.
0.000405  Alice Nakamura is the principal engineer on the billing platform.
0.000017  ...
0.000016  ...                                        <- two least relevant differ in the 6th decimal
```

The **order** was right and the magnitudes were close to meaningless — normal for a quantized
cross-encoder. Use the score to order. Do not threshold on it, and do not compare it across calls.

Measured on the same candidates, INT8 and FP32 produced the **same ordering** with near-identical
scores at a fifth of the file size, which is why INT8 is the default.

---

## TypeSafe: the remote Choice reranker

### What it is, and why a Choice question works as a reranker

A [Choice](https://docs.typesafe.ai/primitives/choice) question returns a **probability distribution
over the options you supply**. Put every candidate in as an option and they compete inside one
judgement, so the probabilities rank — and they are comparable because they came out of one call.

The obvious alternative does not work, which is why it is not the default: asking "is this passage
relevant?" once per candidate (`TypeSafeRerankMode.Noul`) returns **independent** answers — the
vendor's documentation is explicit that one question's answer is not context for another — so twenty
unrelated 0.9s rank nothing. That mode exists for pools too large to list as options and its scores
are documented as not mutually comparable.

### Configuring it

This package deliberately does **not** reference a TypeSafe client. `Jev.Sdk` is not published to
nuget.org, and a published package depending on an unpublished one cannot be restored by anyone else.
You adapt your client once:

```csharp
internal sealed class JevChoiceAsker : IChoiceAsker
{
    private readonly JevClient _client;

    public JevChoiceAsker(JevClient client) => _client = client;

    public async Task<IReadOnlyDictionary<string, double>> AskChoiceAsync(
        string instructions, string state,
        IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var question = new ChoiceQuestion
        {
            Instructions = instructions,
            Criteria = options.ToDictionary(o => o.Key, o => (StructuredValue?)o.Value, StringComparer.Ordinal),
        };

        var response = await _client.SystemOneAsync(
            state,
            new Dictionary<string, Question> { ["rerank"] = question },
            model: null, ct);

        return response.AnswersOrEmpty["rerank"].AsChoice()?.Probabilities
            ?? throw new InvalidOperationException("Expected a choice answer.");
    }
}
```

Then:

```csharp
services.AddSingleton<JevClient>(...);
services.AddSingleton<IChoiceAsker, JevChoiceAsker>();
services.AddHindsight();
services.AddTypeSafeReranker();
```

`JevClient` reads the key from `TYPESAFE_API_KEY` by default (`JevEnvironment.ApiKeyVariable`).

| Option | Default | Notes |
| --- | --- | --- |
| `Instructions` | a built-in question | The wording **is** the judgement. The query and passage numbering are appended, so do not repeat them. |
| `Criteria` | none | Extra judging guidance — "prefer policy statements over anecdotes". Separate from `Instructions` so guidance can vary without rewriting the question. |
| `Mode` | `Choice` | `Noul` scores candidates independently; for pools too large to list. |
| `MaxCandidatesPerQuestion` | 255 | The service maximum. Lower it if latency or tokens matter more; candidates past the limit keep their merge order and **follow rather than being dropped**. |

### Measured cost

Live against the real API through the real `Jev.Sdk` client:

| | Wall clock | Input tokens | Output tokens |
| --- | --- | --- | --- |
| One small Choice question (docs example) | 405 ms | 359 | 38 |
| **One Choice over 10 candidates** | **284 ms** | **634** | **110** |

**One call ranks the whole pool** — that is the advantage over a per-candidate scorer. `JevClient
calls: 1` for ten candidates.

The SDK exposes **token counts, never a price** — no pricing table, no currency. Multiply by your own
rate. Output tokens are currently free; input is what you pay for.

Timing is **not on the response object**. It is available as the `jev.call.duration` histogram
(seconds), a duration on the `systemone.call` span, and an `ElapsedMs` log message. A caller wanting it
inline measures it.

### The failure that does not raise

A Choice call can succeed while putting all probability on one option, producing an order that carries
no ranking information. Check it:

```csharp
if (!reranker.LastRerankDiscriminated())
{
    // The model expressed no preference between candidates. The order is not evidence.
}
```

`LastDiagnostics` carries the mode, candidate count, how many candidates received probability, and the
model's confidence. This is a **heuristic for "did the model express a preference"**, never for "is the
ranking correct" — no measure on a probability distribution can establish the latter.

---

## Side-by-side, for a 10-candidate rerank

| | Identity | ONNX INT8 | TypeSafe Choice |
| --- | --- | --- | --- |
| Cost per recall | **$0** | **$0** | ~634 input tokens |
| Latency | ~0 ms (skipped) | **~117 ms** warm | ~284 ms |
| Resident RAM | none | ~1.0 GB | none |
| One-time setup | none | 570 MB download | API key |
| Network dependency | no | **no** | yes |
| Works air-gapped | yes | **yes** | no |
| Ranks anything | **no** | yes | yes |
| Score meaning | n/a | ordinal, not calibrated | probability, comparable within one call |

---

## What is not measured

Stated plainly, because these gaps matter more than the numbers above.

- **Ranking quality is not measured for either reranker.** The live runs used a query with one
  obviously correct passage and one obviously irrelevant one, so they prove the mechanism and the
  direction of the order — not the judgement. Whether either provider orders **near-miss** candidates
  well needs labelled data, and neither has been scored against ground truth.
- **`Noul` mode has only been exercised through a stub.**
- **GPU timing is single-run, not benchmarked.** The GPU path now *works* and was measured, but the
  timings above are one run each on a shared machine with no repetition, so the numbers show a clear
  direction (the GPU loses) without being a rigorous benchmark. The one configuration where the GPU
  won sits inside the spread and should not be read as a crossover.
- **Ranking quality is still unmeasured on GPU**, as on CPU — same caveat as below.
- **Latency figures are single-run, quiet-machine measurements**, not benchmarks under load. The test
  suite deliberately does not assert on elapsed time, because that flakes under parallel execution.
- **`score` may suit reranking better than `choice`.** A Score returns a position along ordered levels
  you define, and its `score` is already a probability-weighted mean — one comparable number per
  candidate, without requiring candidates to be mutually exclusive. With 10+ candidates a Choice
  distribution must spread one unit of probability across all of them (one live run put 1.0000 on a
  single candidate and 0.0000 on the other nine). A Score mode is a contained addition and is the
  obvious next step.

---

## See also

- [`docs/reranker-live-verification.md`](reranker-live-verification.md) — the live TypeSafe run, its
  output, and what it establishes.
- [`test-infra/README.md`](../test-infra/README.md) — the model fetch script and the local rig.
- [`test-infra/fetch-reranker-model.sh`](../test-infra/fetch-reranker-model.sh) — the verified
  downloader.
- [TypeSafe Choice primitive](https://docs.typesafe.ai/primitives/choice) and
  [Score primitive](https://docs.typesafe.ai/primitives/score) — the vendor's own documentation.
