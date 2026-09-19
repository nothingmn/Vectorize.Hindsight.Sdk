using System.Globalization;
using System.Text;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Reranking;

namespace Vectorize.Hindsight.Sdk.Reranking.TypeSafe;

/// <summary>
/// A reranker that asks a TypeSafe System One model which candidate best answers the query.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Choice question is a reranker.</b> A cross-encoder scores one passage at a time and the
/// scores are comparable because one model produced them under one query. A Choice question does the
/// same thing differently: it puts every candidate into a single judgement as the answer options and
/// returns a probability distribution over them. The candidates therefore <i>compete</i> inside one
/// decision, and the probabilities are comparable because they came out of one call. That is a
/// ranking.
/// </para>
/// <para>
/// The contrast matters, because the obvious alternative does not work. Asking "is this passage
/// relevant?" once per candidate yields independent answers — the vendor's documentation is explicit
/// that one question's answer is not context for another — so a set of unrelated 0.9s ranks nothing.
/// That mode is available (<see cref="TypeSafeRerankMode.Noul"/>) for pools too large to list, but it
/// is not the default and its scores are documented as not mutually comparable.
/// </para>
/// <para>
/// <b>This is remote, billed, and per-call.</b> Unlike the local ONNX reranker there is no fixed
/// cost, so it is a quality reranker rather than a latency one: expect a network round trip and
/// token spend on every recall. <see cref="TypeSafeRerankerOptions.MaxCandidatesPerQuestion"/> bounds the options; candidates
/// past it keep their merge order and follow, rather than being dropped.
/// </para>
/// <para>
/// <b>Scores are probabilities, which are comparable only within one call.</b> A Choice distribution
/// is normalized over the options supplied, so the same passage scores differently against a
/// different candidate list. Never threshold on the value across calls or compare it between
/// queries — only its order within one result means anything.
/// </para>
/// </remarks>
public sealed class TypeSafeChoiceReranker : IReranker
{
    /// <summary>
    /// The most options a single Choice question accepts, per the vendor's documentation.
    /// </summary>
    /// <remarks>
    /// A hard service limit rather than a preference. Candidates beyond it must be handled by
    /// chunking, by the Noul mode, or by a smaller per-bank limit upstream — the provider will not
    /// silently drop them.
    /// </remarks>
    public const int MaxChoiceOptions = 255;

    private const string DefaultInstructions =
        "Which of the passages best answers the query? Judge only how useful each passage is for "
        + "answering the query, not its length or how it is written.";

    private readonly IChoiceAsker _asker;
    private readonly TypeSafeRerankerOptions _options;

    /// <summary>Creates the reranker.</summary>
    /// <param name="asker">The seam that performs the question.</param>
    /// <param name="options">The options. Null means the defaults.</param>
    public TypeSafeChoiceReranker(IChoiceAsker asker, TypeSafeRerankerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(asker);
        _asker = asker;
        _options = options ?? new TypeSafeRerankerOptions();

        if (_options.MaxCandidatesPerQuestion <= 0)
        {
            throw new HindsightConfigurationException(
                "MaxCandidatesPerQuestion must be positive. Leave it null to use the service maximum.");
        }

        if (_options.MaxCandidatesPerQuestion > MaxChoiceOptions)
        {
            throw new HindsightConfigurationException(
                $"MaxCandidatesPerQuestion is {_options.MaxCandidatesPerQuestion}, above the service "
                + $"maximum of {MaxChoiceOptions} options for a single Choice question.");
        }
    }

    /// <summary>Diagnostics from the most recent rerank.</summary>
    /// <remarks>
    /// Per-instance and read after a call. A remote reranker's likely failure is a successful call
    /// whose answer did not discriminate, and this is where that is visible.
    /// </remarks>
    public TypeSafeRerankDiagnostics? LastDiagnostics { get; private set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<double>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();

        if (documents.Count == 0)
        {
            return [];
        }

        return _options.Mode switch
        {
            TypeSafeRerankMode.Noul => await RerankByNoulAsync(query, documents, cancellationToken)
                .ConfigureAwait(false),
            _ => await RerankByChoiceAsync(query, documents, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// One Choice question over every candidate, with the model's distribution as the scores.
    /// </summary>
    /// <remarks>
    /// The option keys are positions, not candidate text. That is deliberate: option names are sent
    /// to the model and a long passage as a key would be counted as part of the question, while the
    /// candidate itself belongs in the state. Position keys also keep the mapping back to documents
    /// exact, which is the thing that must not go wrong.
    /// </remarks>
    private async Task<IReadOnlyList<double>> RerankByChoiceAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        var limit = _options.MaxCandidatesPerQuestion;
        var considered = documents.Count <= limit ? documents.Count : limit;

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var state = new StringBuilder();

        for (var index = 0; index < considered; index++)
        {
            var key = "candidate_" + index.ToString(CultureInfo.InvariantCulture);
            options[key] = $"passage {index.ToString(CultureInfo.InvariantCulture)} below";

            state.Append('[')
                .Append(index.ToString(CultureInfo.InvariantCulture))
                .AppendLine("]")
                .AppendLine(documents[index] ?? string.Empty);
        }

        var instructions = BuildInstructions(query, considered);

        var probabilities = await _asker
            .AskChoiceAsync(instructions, state.ToString(), options, cancellationToken)
            .ConfigureAwait(false);

        var scores = new double[documents.Count];
        var withProbability = 0;

        for (var index = 0; index < considered; index++)
        {
            var key = "candidate_" + index.ToString(CultureInfo.InvariantCulture);

            // A missing key is treated as zero probability rather than an error: the service may
            // legitimately omit an option it judged impossible, and that is a real answer. An
            // out-of-range value would break the ordering's meaning, so it is clamped.
            if (probabilities.TryGetValue(key, out var probability))
            {
                if (probability > 0)
                {
                    withProbability++;
                }

                scores[index] = probability < 0 ? 0 : probability;
                continue;
            }

            scores[index] = 0;
        }

        // Candidates past the question's limit keep their merge order and follow the ranked ones.
        // They are scored below everything the model actually considered, and they are not dropped:
        // silently truncating a caller's candidate set is not this provider's job.
        for (var index = considered; index < documents.Count; index++)
        {
            scores[index] = -1;
        }

        LastDiagnostics = new TypeSafeRerankDiagnostics
        {
            Mode = TypeSafeRerankMode.Choice,
            CandidateCount = considered,
            OptionsWithProbability = withProbability,
        };

        return scores;
    }

    /// <summary>One independent yes/no question per candidate.</summary>
    private async Task<IReadOnlyList<double>> RerankByNoulAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        var scores = new double[documents.Count];

        for (var index = 0; index < documents.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Two options with names that are the judgement, so the probability of "relevant" is
            // directly the score. This is the mode's whole idea: the answers are independent, so
            // they are read as independent scores.
            var options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["relevant"] = "The passage helps answer the query.",
                ["irrelevant"] = "The passage does not help answer the query.",
            };

            var probabilities = await _asker
                .AskChoiceAsync(
                    $"{DefaultInstructions} Query: {query}",
                    documents[index] ?? string.Empty,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);

            scores[index] = probabilities.TryGetValue("relevant", out var value) && value > 0 ? value : 0;
        }

        LastDiagnostics = new TypeSafeRerankDiagnostics
        {
            Mode = TypeSafeRerankMode.Noul,
            CandidateCount = documents.Count,
            OptionsWithProbability = scores.Count(score => score > 0),
        };

        return scores;
    }

    /// <summary>Builds the question, including the query and the caller's own wording when given.</summary>
    private string BuildInstructions(string query, int candidateCount)
    {
        var builder = new StringBuilder();

        builder.AppendLine(_options.Instructions ?? DefaultInstructions);
        builder.AppendLine();
        builder.Append("The query is: ").AppendLine(query);
        builder.AppendLine();
        builder.Append("There are ")
            .Append(candidateCount.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" passages, each labelled with its number in square brackets.");

        if (_options.Criteria is { Count: > 0 } criteria)
        {
            builder.AppendLine();
            builder.AppendLine("Additional guidance for judging:");
            foreach (var entry in criteria)
            {
                builder.Append("- ").AppendLine(entry);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether the last rerank's answer discriminated between candidates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure worth checking. A Choice call can succeed while putting all probability on one
    /// option — a ranking with no information in it — or spreading it so evenly that the order is
    /// noise. Neither raises, so a caller that cares calls this and decides, rather than discovering
    /// later that the reranker was quietly not doing anything.
    /// </para>
    /// <para>
    /// <b>This is a heuristic, and it is deliberately stated as one.</b> It answers "did the model
    /// express a preference", not "is the ranking correct" — no measure on a probability
    /// distribution can establish the latter. A caller with real labels should measure against them
    /// instead of trusting this.
    /// </para>
    /// <para>
    /// The test is whether two candidates received different probabilities, which is the minimum
    /// for the result to be a ranking at all. It deliberately does <i>not</i> require an unused
    /// option: a model that puts 0.6/0.3/0.1 across three candidates has discriminated perfectly
    /// well, and an earlier version of this method wrongly reported that as failure.
    /// </para>
    /// </remarks>
    public bool LastRerankDiscriminated()
    {
        var diagnostics = LastDiagnostics;
        if (diagnostics is null || diagnostics.Mode != TypeSafeRerankMode.Choice)
        {
            return false;
        }

        // Fewer than two candidates carried probability, so at most one was preferred: the order is
        // a statement about one candidate, not a ranking.
        return diagnostics.OptionsWithProbability >= 2;
    }
}

/// <summary>How the TypeSafe reranker asks its question.</summary>
public sealed record TypeSafeRerankerOptions
{
    /// <summary>
    /// The question to ask, replacing the built-in wording.
    /// </summary>
    /// <remarks>
    /// The wording is the judgement, so this is the main quality lever — the vendor recommends
    /// stating the judgement as a specific question and describing situations rather than degrees.
    /// The query and the passage numbering are appended to whatever is set here, so do not repeat
    /// them.
    /// </remarks>
    public string? Instructions { get; init; }

    /// <summary>Extra guidance appended to the question.</summary>
    /// <remarks>
    /// For caller-specific judging criteria — "prefer passages from the current year", "treat a
    /// policy statement as more useful than an anecdote". Kept separate from
    /// <see cref="Instructions"/> so a caller can vary the guidance without rewriting the question.
    /// </remarks>
    public IReadOnlyList<string>? Criteria { get; init; }

    /// <summary>Which question shape to use. Choice by default.</summary>
    public TypeSafeRerankMode Mode { get; init; } = TypeSafeRerankMode.Choice;

    /// <summary>
    /// How many candidates to put into one question.
    /// </summary>
    /// <remarks>
    /// Defaults to the service maximum of 255, because the vendor's guidance is to give the model
    /// the full list rather than a shortlist. Lower it if latency or token cost matters more than
    /// completeness for a particular workload: candidates past the limit keep their merge order
    /// rather than being dropped.
    /// </remarks>
    public int MaxCandidatesPerQuestion { get; init; } = TypeSafeChoiceReranker.MaxChoiceOptions;
}
