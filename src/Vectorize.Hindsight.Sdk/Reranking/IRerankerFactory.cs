using Vectorize.Hindsight.Sdk.Reranking.Providers;

namespace Vectorize.Hindsight.Sdk.Reranking;

/// <summary>
/// Creates the reranker a host has chosen, at the moment it is first needed.
/// </summary>
/// <remarks>
/// <para>
/// The factory exists so that <b>selecting a reranker is a registration, not a branch</b>. A host
/// resolves this interface and gets whichever implementation it registered; the library never
/// switches on a string, an enum or a type name to decide which reranker to build. Adding a
/// provider is adding a package and a line of registration, and nothing in the core changes.
/// </para>
/// <para>
/// <b>Deliberately lazy and cached.</b> A reranker is expensive to construct — the local ONNX one
/// reads a model from disk and initialises a graph, and every provider needs the host's
/// configuration resolved first. Creating one eagerly during container construction would make a
/// host's startup fail for a feature it might never use, and would do that work even for a request
/// that never recalls across banks.
/// </para>
/// <para>
/// <b>Failure is deferred, and reported on use.</b> <see cref="TryCreate"/> returns null rather
/// than throwing when a provider cannot be built, because a host that wants cross-bank recall
/// without a reranker should still start. The reason is exposed so the host can log it, and a host
/// that treats the absence as fatal can say so itself with
/// <see cref="GetRequired(IServiceProvider)"/>.
/// </para>
/// </remarks>
public interface IRerankerFactory
{
    /// <summary>
    /// The name of the provider this factory creates, for diagnostics.
    /// </summary>
    /// <remarks>
    /// For a log line or a health-check response. Never used for dispatch — if a host is choosing
    /// between rerankers by comparing names, the selection has leaked out of the container.
    /// </remarks>
    string ProviderName { get; }

    /// <summary>
    /// Creates the reranker, or returns null when none is configured or it cannot be built.
    /// </summary>
    /// <param name="services">The container, for a provider that needs a collaborator.</param>
    /// <param name="reason">Why creation returned null, when it did.</param>
    /// <returns>The reranker, or null.</returns>
    IReranker? TryCreate(IServiceProvider services, out string? reason);

    /// <summary>
    /// Creates the reranker, throwing when it cannot be built.
    /// </summary>
    /// <remarks>
    /// For a host that has decided reranking is required and would rather fail at startup than
    /// silently retrieve unranked. The exception is a
    /// <see cref="Errors.HindsightConfigurationException"/>, because an unusable reranker is a
    /// configuration outcome rather than a runtime one.
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <exception cref="Errors.HindsightConfigurationException">The reranker could not be built.</exception>
    IReranker GetRequired(IServiceProvider services);
}

/// <summary>
/// The factory for <see cref="IdentityReranker"/>: the default when a host registers nothing.
/// </summary>
/// <remarks>
/// Registered by every <c>AddHindsight…</c> call, so resolving a reranker always succeeds and a
/// call site never needs a null check. It exists as a factory rather than a bare singleton so that
/// a host can replace it by registering a different <see cref="IRerankerFactory"/> with no change
/// to the core or to any call site.
/// </remarks>
public sealed class IdentityRerankerFactory : IRerankerFactory
{
    /// <summary>A shared instance. The type is stateless.</summary>
    public static readonly IdentityRerankerFactory Instance = new();

    /// <inheritdoc />
    public string ProviderName => "identity";

    /// <inheritdoc />
    public IReranker? TryCreate(IServiceProvider services, out string? reason)
    {
        reason = "No reranker provider is registered, so the merge order is preserved.";
        return IdentityReranker.Instance;
    }

    /// <inheritdoc />
    public IReranker GetRequired(IServiceProvider services) => IdentityReranker.Instance;
}
