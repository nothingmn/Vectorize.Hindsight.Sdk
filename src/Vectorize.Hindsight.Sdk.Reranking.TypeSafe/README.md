# Vectorize.Hindsight.Sdk.Reranking.TypeSafe

TypeSafe System One **Choice**-based reranking for
[`Vectorize.Hindsight.Sdk`](https://www.nuget.org/packages/Vectorize.Hindsight.Sdk).

It adds the second stage of cross-bank retrieval — ranking the merged candidates against the query —
by asking a TypeSafe model **one Choice question whose options are the candidates**.

```
┌─ Bank A ─ recall ─┐
├─ Bank B ─ recall ─┼─→ merged candidates → one Choice question → probability order
└─ Bank C ─ recall ─┘
```

## Does a Choice question make sense as a reranker?

Yes, and the reason is specific. The
[Choice primitive](https://docs.typesafe.ai/primitives/choice) returns a **probability distribution
over the options you supply** — `probabilities` for every option, plus a `confidence`. Because every
candidate is an option in a single judgement, the candidates *compete* inside one decision and the
probabilities came out of one call, so they are comparable and they rank.

The obvious alternative does not work, which is why it is not the default. Asking "is this passage
relevant?" once per candidate returns **independent** answers — the vendor's documentation is explicit
that one question's answer is not context for another — so twenty unrelated `0.9`s rank nothing.
That mode is available (`TypeSafeRerankMode.Noul`) for pools too large to list as options, and its
scores are documented as not mutually comparable.

There is a second option type worth knowing about. `Score` returns a position along ordered levels you
define, which is a natural fit for **relevance graded in steps** ("directly answers" / "related" /
"unrelated"), and its `score` is a probability-weighted mean so it is already a single comparable
number. It is not implemented here because a Choice question needs no scale design and returns the
distribution directly; adding a `Score` mode is a contained change if graded relevance proves more
useful on your data.

## Install

```bash
dotnet add package Vectorize.Hindsight.Sdk.Reranking.TypeSafe
```

## Wire it up

This package deliberately does **not** reference a TypeSafe client. `Jev.Sdk` — the sibling client in
this family — is not published to nuget.org, and a published package that depends on an unpublished
one cannot be restored by anyone else. So the provider depends on `IChoiceAsker`, and you adapt your
client once:

```csharp
internal sealed class JevChoiceAsker : IChoiceAsker
{
    private readonly JevClient _client;

    public JevChoiceAsker(JevClient client) => _client = client;

    public async Task<IReadOnlyDictionary<string, double>> AskChoiceAsync(
        string instructions,
        string state,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        var question = new ChoiceQuestion
        {
            Instructions = instructions,
            Criteria = options.ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal),
        };

        var response = await _client.SystemOneAsync(
            state,
            new Dictionary<string, Question> { ["rerank"] = question },
            model: null,
            cancellationToken);

        return ((ChoiceAnswer)response.Answers["rerank"]).Probabilities;
    }
}
```

Then register it:

```csharp
services.AddSingleton<JevClient>(...);
services.AddSingleton<IChoiceAsker, JevChoiceAsker>();
services.AddHindsight();
services.AddTypeSafeReranker();
```

Registration order does not matter, and selection is **the registration itself** — nothing compares a
provider name or switches on a type to choose a reranker, so a provider added later needs no change to
this package or to the core.

## Use

```csharp
var result = await client.RecallAcrossBanksAsync(
    "when did the billing platform migrate?",
    ["bank-personal", "bank-team", "bank-archive"],
    new CrossBankRecallOptions { PerBankLimit = 10 },
    cancellationToken);

Console.WriteLine(result.RerankApplied);        // true when the ordering was applied
foreach (var candidate in result.Candidates)    // now in probability order
{
    Console.WriteLine($"{candidate.RerankScore:F4}  {candidate.BankId}  {candidate.Text}");
}
```

The reranker comes from the client when the call does not supply one, so with the container setup
above there is nothing to pass per call.

## Read this before you use the scores

**Scores are probabilities normalized over the candidates, so they are comparable only within one
call.** The same passage scores differently against a different candidate list. Never threshold on
the value across calls, and never compare it between queries — only its order within one result means
anything.

**This is remote, billed, and per call.** Unlike the local ONNX reranker there is no fixed cost, so
this is a quality reranker rather than a latency one. Every recall spends a round trip and tokens.
One call ranks the whole candidate pool, which is the good news: Choice is not one request per
candidate.

## Checking that it actually did something

A remote reranker's likely failure is not an exception. It is a technically successful call whose
answer did not discriminate — all probability on one option, so the resulting order says nothing:

```csharp
if (!reranker.LastRerankDiscriminated())
{
    // The model did not express a preference between candidates. The order is not evidence.
}
```

`LastDiagnostics` carries the mode, the candidate count, how many candidates received probability,
and the model's confidence. This is a **heuristic** — it answers "did the model express a preference",
not "is the ranking correct". No measure on a probability distribution can establish the latter; if
you have labelled data, measure against it.

The vendor documents low `confidence` as the signal that the question did not discriminate rather
than that the answer is wrong, so it is reported for exactly that reason.

## Options worth knowing

| Option | Default | Why you would change it |
| --- | --- | --- |
| `Instructions` | a built-in question | The wording **is** the judgement. The query and passage numbering are appended, so do not repeat them. |
| `Criteria` | none | Extra judging guidance — "prefer policy statements over anecdotes". Kept separate from `Instructions` so guidance can vary without rewriting the question. |
| `Mode` | `Choice` | `Noul` scores each candidate independently for pools too large to list. Its scores are not mutually comparable. |
| `MaxCandidatesPerQuestion` | 255 | The service maximum, because the vendor recommends giving the model the full list. Lower it if latency or tokens matter more; candidates past the limit keep their merge order and follow rather than being dropped. |

## Not affiliated

Independent package, not affiliated with or endorsed by Vectorize or TypeSafe.

## License

MIT.
