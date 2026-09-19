using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vectorize.Hindsight.Sdk.Reranking;
using Vectorize.Hindsight.Sdk.Reranking.Providers;

namespace Vectorize.Hindsight.Sdk.DependencyInjection;

/// <summary>
/// Registration and resolution of rerankers, so a provider registers itself and the core never
/// branches on which one to build.
/// </summary>
/// <remarks>
/// <para>
/// These helpers live here rather than in the core library because the core deliberately takes no
/// dependency on a container: it is usable from a console program, a test or an AOT-hosted service
/// with no DI at all. The seam — <see cref="IReranker"/> and <see cref="IRerankerFactory"/> — is in
/// the core; only the registration is here.
/// </para>
/// <para>
/// <b>Selection is a registration, not a switch.</b> A reranker package adds an
/// <c>Add…Reranker</c> extension that registers its own <see cref="IRerankerFactory"/>, and a host
/// either calls one of those or registers its own. Nothing in the library compares provider names or
/// branches on a type to decide which reranker to construct, so adding a provider requires no change
/// to this package or to the core.
/// </para>
/// <para>
/// Every registration uses <c>TryAdd</c>, so a host's own choice is never overwritten by a package
/// default and a package default is never overwritten by a second call.
/// </para>
/// </remarks>
public static class RerankerServiceCollectionExtensions
{
    /// <summary>
    /// Registers a reranker factory as the container's provider.
    /// </summary>
    /// <typeparam name="TFactory">The factory type.</typeparam>
    /// <param name="services">The container.</param>
    /// <remarks>
    /// <para>
    /// <c>TryAdd</c> semantics: the first registration wins. Registering twice is a no-op rather
    /// than an error, which is what lets a package's default and a host's explicit choice be called
    /// in either order without a surprise.
    /// </para>
    /// <para>
    /// The <see cref="DynamicallyAccessedMembersAttribute"/> on <typeparamref name="TFactory"/> is
    /// not decoration: this package declares itself AOT compatible, and the container constructs
    /// the type by reflection. The same annotation the container's own generic overloads carry is
    /// what keeps that promise checkable rather than merely asserted, so the trim analyzer accepts
    /// the registration instead of the claim being quietly false.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddReranker<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactory>(
        this IServiceCollection services)
        where TFactory : class, IRerankerFactory
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRerankerFactory, TFactory>();
        return services;
    }

    /// <summary>
    /// Registers a factory instance as the container's provider.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="factory">The factory. Null registers the identity default.</param>
    public static IServiceCollection AddReranker(this IServiceCollection services, IRerankerFactory? factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(factory ?? IdentityRerankerFactory.Instance);
        return services;
    }

    /// <summary>
    /// Registers the identity reranker: no judgement, merge order preserved.
    /// </summary>
    /// <remarks>
    /// The default, and stated explicitly here so a host can read its own configuration and mean
    /// "deliberately no reranking" rather than relying on the absence of a registration.
    /// </remarks>
    /// <param name="services">The container.</param>
    public static IServiceCollection AddIdentityReranker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRerankerFactory>(IdentityRerankerFactory.Instance);
        return services;
    }

    /// <summary>
    /// Resolves the configured reranker, or the identity default when nothing is registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never returns null, so a call site never has to handle "no reranker" — the identity default
    /// carries that meaning, and <see cref="IReranker.IsIdentity"/> lets the caller tell a no-op
    /// from a reranker that judged relevance.
    /// </para>
    /// <para>
    /// A registered provider that cannot be built also falls back to the identity default, because
    /// losing cross-bank recall entirely is a worse outcome than losing its ranking. A host that
    /// disagrees calls <see cref="IRerankerFactory.GetRequired"/> and fails at startup instead.
    /// </para>
    /// </remarks>
    /// <param name="services">The container.</param>
    public static IReranker ResolveReranker(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var factory = services.GetService<IRerankerFactory>();
        if (factory is null)
        {
            return IdentityReranker.Instance;
        }

        return factory.TryCreate(services, out _) ?? IdentityReranker.Instance;
    }

    /// <summary>
    /// Resolves the configured reranker, or the identity default, and reports why.
    /// </summary>
    /// <remarks>
    /// For a host that logs its wiring, or exposes it on a health endpoint. The reason is the
    /// factory's own explanation, so a health check says "no provider is registered" rather than
    /// only "identity".
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <param name="reason">Why the reranker is what it is.</param>
    public static IReranker ResolveReranker(this IServiceProvider services, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(services);

        var factory = services.GetService<IRerankerFactory>();
        if (factory is null)
        {
            reason = "No reranker provider is registered; the identity reranker preserves merge order.";
            return IdentityReranker.Instance;
        }

        var reranker = factory.TryCreate(services, out var factoryReason);
        if (reranker is null)
        {
            reason = factoryReason;
            return IdentityReranker.Instance;
        }

        reason = factoryReason;
        return reranker;
    }
}
