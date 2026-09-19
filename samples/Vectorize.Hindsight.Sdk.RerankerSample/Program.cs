using Microsoft.Extensions.DependencyInjection;
using Vectorize.Hindsight.Sdk;
using Vectorize.Hindsight.Sdk.Configuration;
using Vectorize.Hindsight.Sdk.DependencyInjection;
using Vectorize.Hindsight.Sdk.Reranking;
using Vectorize.Hindsight.Sdk.Reranking.Providers;
using Vectorize.Hindsight.Sdk.Reranking.TypeSafe;

// Choosing a reranker by registration, and what the three choices mean.
//
// This runs with no network and no credentials: the TypeSafe reranker is exercised through its
// IChoiceAsker seam, which a host supplies. The point is the selection mechanism itself — there is
// no switch statement anywhere, and nothing compares a provider name to decide which reranker to
// build. Whichever factory is registered is the one that runs.

Console.WriteLine("=== the three reranker choices ===");
Console.WriteLine();

// 1. The default. No registration, no cost, no dependency, and no claim to have judged anything.
{
    var services = new ServiceCollection();
    services.AddHindsight();

    using var provider = services.BuildServiceProvider();
    var reranker = provider.ResolveReranker(out var reason);

    Console.WriteLine($"1. no registration  -> {reranker.GetType().Name}");
    Console.WriteLine($"   IsIdentity: {reranker.IsIdentity}");
    Console.WriteLine($"   reason    : {reason}");
    Console.WriteLine("");
}

// 2. A provider from a package. Registering it is the entire selection mechanism.
{
    var services = new ServiceCollection();
    services.AddSingleton<IChoiceAsker, DemoChoiceAsker>();
    services.AddHindsight();
    services.AddTypeSafeReranker();

    using var provider = services.BuildServiceProvider();
    var reranker = provider.ResolveReranker();

    Console.WriteLine($"2. TypeSafe registered -> {reranker.GetType().Name}");
    Console.WriteLine($"   IsIdentity: {reranker.IsIdentity}");

    // Registration order does not matter. This was a real bug: an eagerly registered default made
    // TryAdd swallow every later choice, so a host calling AddHindsight() first silently kept the
    // no-op reranker and never ranked anything.
    var reversed = new ServiceCollection();
    reversed.AddSingleton<IChoiceAsker, DemoChoiceAsker>();
    reversed.AddTypeSafeReranker();
    reversed.AddHindsight();

    using var reversedProvider = reversed.BuildServiceProvider();
    Console.WriteLine($"   same result when registered in the other order: "
        + $"{reversedProvider.ResolveReranker().GetType().Name}");

    // And the client the container builds carries it, so no call site wires a reranker by hand.
    using var client = provider.GetRequiredService<HindsightClient>();
    Console.WriteLine($"   client.Reranker: {client.Reranker.GetType().Name}");
    Console.WriteLine("");
}

// 3. A third-party reranker that exists only in this host. Nothing in the library knows it exists.
{
    var services = new ServiceCollection();
    services.AddHindsight();
    services.AddReranker<AlwaysFirstFactory>();

    using var provider = services.BuildServiceProvider();
    Console.WriteLine($"3. host's own factory -> {provider.ResolveReranker().GetType().Name}");
    Console.WriteLine("");
}

// What a Choice distribution does to a candidate order.
Console.WriteLine("=== a Choice question ranking candidates ===");
Console.WriteLine();

var typeSafe = new TypeSafeChoiceReranker(new DemoChoiceAsker());

var candidates = new[]
{
    "The office coffee machine was replaced last week.",
    "The billing platform migrated to PostgreSQL in March 2026.",
    "Alice Nakamura is based in the Vancouver office.",
};

var scores = await typeSafe.RerankAsync(
    "when did the billing platform migrate?", candidates, CancellationToken.None);

Console.WriteLine("relevance order:");
foreach (var (candidate, score) in candidates.Zip(scores).OrderByDescending(pair => pair.Second))
{
    Console.WriteLine($"  {score:F4}  {candidate}");
}

Console.WriteLine();
Console.WriteLine($"diagnostics: candidates={typeSafe.LastDiagnostics!.CandidateCount} "
    + $"withProbability={typeSafe.LastDiagnostics.OptionsWithProbability}");
Console.WriteLine($"discriminated between candidates: {typeSafe.LastRerankDiscriminated()}");
Console.WriteLine();

// The identity default, side by side. It preserves the order rather than judging it.
Console.WriteLine("the same candidates through the identity default (no judgement):");
var identityScores = await IdentityReranker.Instance.RerankAsync(
    "when did the billing platform migrate?", candidates, CancellationToken.None);

foreach (var (candidate, score) in candidates.Zip(identityScores))
{
    Console.WriteLine($"  {score:F0}  {candidate}");
}

Console.WriteLine();
Console.WriteLine("The first is a ranking. The second is the order the merge produced, labelled as such.");

/// <summary>
/// A stand-in for the host's adapter over a real TypeSafe client.
/// </summary>
/// <remarks>
/// In a host this forwards to Jev.Sdk (or the vendor's own SDK) with one Choice question. It is
/// stubbed here only so this sample needs no credentials. Note what it receives: option names that
/// are candidate positions, and the passages themselves in the state.
/// </remarks>
internal sealed class DemoChoiceAsker : IChoiceAsker
{
    public Task<IReadOnlyDictionary<string, double>> AskChoiceAsync(
        string instructions,
        string state,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A real model decides this. The stub ranks by whether the passage mentions the migration,
        // which is enough to show the ordering working.
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        var weight = 0.0;

        foreach (var option in options.Keys)
        {
            // The provider puts the passages in the state, numbered; recover the index from the key.
            var index = int.Parse(option.AsSpan("candidate_".Length));
            var relevant = state.Contains("migrated to PostgreSQL", StringComparison.OrdinalIgnoreCase) && index == 1;
            probabilities[option] = relevant ? 0.85 : 0.075;
            weight += probabilities[option];
        }

        // Normalize, because the provider reads these as a distribution.
        foreach (var key in probabilities.Keys.ToList())
        {
            probabilities[key] /= weight;
        }

        return Task.FromResult<IReadOnlyDictionary<string, double>>(probabilities);
    }
}

/// <summary>A reranker that exists only in this host, to show no registry needs updating.</summary>
internal sealed class AlwaysFirstReranker : IReranker
{
    public Task<IReadOnlyList<double>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<double>>([.. documents.Select((_, index) => (double)(documents.Count - index))]);
}

internal sealed class AlwaysFirstFactory : IRerankerFactory
{
    public string ProviderName => "host-defined";

    public IReranker? TryCreate(IServiceProvider services, out string? reason)
    {
        reason = null;
        return new AlwaysFirstReranker();
    }

    public IReranker GetRequired(IServiceProvider services) => new AlwaysFirstReranker();
}
