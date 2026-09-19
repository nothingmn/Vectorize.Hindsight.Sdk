# Vectorize.Hindsight.Sdk.Reranking.Onnx

Local ONNX cross-encoder reranking for
[`Vectorize.Hindsight.Sdk`](https://www.nuget.org/packages/Vectorize.Hindsight.Sdk).

Use it to add the **second stage** of cross-bank retrieval: the first stage asks "what looks
relevant inside this bank?", and this one asks "of everything retrieved from every bank, what is
most useful for this query?". Nothing leaves the process — no service, no API key, no per-query
cost.

```
┌─ Bank A ─ recall ─┐
├─ Bank B ─ recall ─┼─→ merged candidates → reranker → relevance order
└─ Bank C ─ recall ─┘
```

## Install

```bash
dotnet add package Vectorize.Hindsight.Sdk.Reranking.Onnx
```

## Model assets

The model files are **not** in this package — they are hundreds of megabytes and belong to the
model's own repository, not to a NuGet package. Fetch them once:

```bash
# INT8 (570 MB, the default and the recommended starting point)
bash test-infra/fetch-reranker-model.sh int8

# FP32 (2.3 GB, split graph + external weights)
bash test-infra/fetch-reranker-model.sh fp32
```

Both downloads come from the ONNX community export of
[`BAAI/bge-reranker-v2-m3`](https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX)
(Apache-2.0) plus its tokenizer assets from the
[BAAI repository](https://huggingface.co/BAAI/bge-reranker-v2-m3). You need **all three** files:

| File | Why |
| --- | --- |
| `model_int8.onnx` | The cross-encoder itself |
| `tokenizer.json` | The vocabulary and the special-token ids |
| `sentencepiece.bpe.model` | The segmentation model |

## Use

```csharp
using var reranker = new OnnxReranker(new OnnxRerankerOptions
{
    ModelPath = "artifacts/reranker/model_int8.onnx",
    TokenizerJsonPath = "artifacts/reranker/tokenizer.json",
    SentencePieceModelPath = "artifacts/reranker/sentencepiece.bpe.model",
});

var result = await client.RecallAcrossBanksAsync(
    "when did the billing platform migrate?",
    ["bank-personal", "bank-team", "bank-archive"],
    new CrossBankRecallOptions
    {
        PerBankLimit = 10,
        Reranker = reranker,
    },
    cancellationToken);

Console.WriteLine(result.RerankApplied);          // true when the ordering was applied
foreach (var candidate in result.Candidates)       // now in relevance order
{
    Console.WriteLine($"{candidate.RerankScore:F4}  {candidate.BankId}  {candidate.Text}");
}
```

`OnnxReranker` is a long-lived collaborating service: construct it once during host startup and
share it. Loading reads the model from disk and initialises the graph, so it does not belong in a
request path. It is safe for concurrent use, and `Dispose` is safe while calls are in flight.

## Read this before you use the scores

**Scores are ordinal, not calibrated.** Higher means more relevant to *this* query, and scores
within one call are comparable with each other. Do not compare a score across calls, and do not
threshold on it.

This is measured, not theoretical. Against the INT8 export with a query about a billing migration,
the one right answer scored `0.29` while every other candidate landed between `0.000016` and
`0.0018` — the two least relevant were separated only in the sixth decimal. The **order** was
right; the absolute numbers were close to meaningless. Order by the score, and let a model read
the passages.

Measured on the same candidates, INT8 and FP32 produced the **same ordering** with near-identical
scores at a fifth of the file size, which is why INT8 is the default. If you need a specific
threshold to mean something, measure FP32 on your own data rather than assuming either one is
calibrated.

## CPU and GPU

CPU by default, and CPU is the right starting point: for a handful of 20–50 candidate reranks per
request it is entirely sufficient. Measured on this project's own candidate sets, a 7-document
rerank was well under a tenth of a second.

For the GPU, three things are required:

1. Reference `Microsoft.ML.OnnxRuntime.Gpu` **in your application** instead of the CPU package this
   package brings transitively. Both expose the same API, so no code changes.
2. Install the CUDA libraries the provider needs (cuDNN and cuBLAS). They are not part of the
   NuGet package.
3. Set `ExecutionProvider = OnnxExecutionProvider.Cuda`.

**A missing GPU does not fail.** It falls back to CPU with a warning, because turning an optional
ranking improvement into a total failure is not a trade worth making. Read `reranker.ProviderInUse`
to see what actually happened, and `reranker.ProviderWarning` for why:

```csharp
Console.WriteLine(reranker.ProviderInUse);      // "CPUExecutionProvider" or the CUDA provider
if (reranker.ProviderWarning is { } warning) Console.WriteLine(warning);
```

`OnnxExecutionProvider.Auto` prefers an accelerated provider when the loaded runtime reports one,
and is silent when there is none.

## How the tokenizer is made exact

This is the part that goes wrong quietly. ONNX Runtime runs happily on wrong input and returns
numbers that look like scores, so a tokenization error does not announce itself — it surfaces as a
reranker that ranks badly for no visible reason.

An XLM-R derived reranker needs **two** assets because they disagree in a specific way: the
Hugging Face vocabulary inserts a `<pad>` at index 1, which shifts every content piece by one, and
it orders the special tokens differently (`<s> <pad> </s> <unk>` against SentencePiece's
`<unk> <s> </s>`).

So segmentation comes from `sentencepiece.bpe.model`, and each resulting piece is mapped to its id
by looking it up in the vocabulary from `tokenizer.json`. The mapping is **derived from the model's
own artifacts** rather than hardcoded as an offset, and it is verified against the reference
implementation: the ids match the Hugging Face tokenizer exactly on English and multilingual input.
The test suite asserts those exact ids, so a regression fails a test rather than quietly degrading
the ranking.

If the two assets ever came from different models, pieces would fall back to `<unk>` — which would
otherwise show up only as unexplained poor quality. `reranker.UnmappedPieceCount` counts those, and
the suite asserts it stays at zero across English, German, French, Japanese and emoji.

## Options worth knowing

| Option | Default | Why you would change it |
| --- | --- | --- |
| `MaxSequenceLength` | 512 | The model's own limit. Truncation keeps the query whole and trims the passage, because a query cut in half scores every passage wrongly. |
| `BatchSize` | 16 | Batching matters more for latency than almost anything else. Candidates are grouped by length first, so a short passage is not padded up to the pool's longest. |
| `ApplySigmoid` | true | Set false for the raw logit, which is what you want if you will combine it with other terms — it stays linear and additive. |
| `MaxConcurrentSessions` | 1 | Raise only if measurements show the session idle. Batching beats interleaving, and serialising bounds memory. |
| `IntraOpThreads` | 0 (runtime chooses) | Pin it when several sessions share a machine. |

## Not affiliated

This is an independent package, not affiliated with or endorsed by Vectorize or BAAI. Model
licensing is the model's own: `bge-reranker-v2-m3` is Apache-2.0. Note that some alternative
multilingual rerankers are **not** permissively licensed — `jinaai/jina-reranker-v2-base-multilingual`
is CC BY-NC 4.0, which is why it is not the default here.

## License

MIT.
