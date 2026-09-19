using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Reranking;

namespace Vectorize.Hindsight.Sdk.Reranking.TypeSafe;

/// <summary>
/// Creates the TypeSafe reranker from the host's container.
/// </summary>
/// <remarks>
/// <para>
/// Registering this is how a host selects TypeSafe reranking. Nothing branches on "typesafe" to
/// choose it — the registration is the selection, so a host that registers a different
/// <see cref="IRerankerFactory"/> gets that one with no change here or in the core.
/// </para>
/// <para>
/// The <see cref="IChoiceAsker"/> is resolved from the container rather than supplied, so a host
/// registers its adapted client once and this factory picks it up. That is what keeps the provider
/// independent of any particular TypeSafe client package.
/// </para>
/// </remarks>
public sealed class TypeSafeRerankerFactory : IRerankerFactory
{
    private readonly TypeSafeRerankerOptions _options;
    private readonly Lock _sync = new();
    private TypeSafeChoiceReranker? _instance;
    private string? _failureReason;
    private bool _attempted;

    /// <summary>Creates a factory.</summary>
    /// <param name="options">The options. Null means the defaults.</param>
    public TypeSafeRerankerFactory(TypeSafeRerankerOptions? options = null) => _options = options ?? new();

    /// <inheritdoc />
    public string ProviderName => "typesafe";

    /// <summary>Whether a missing client should throw rather than fall back.</summary>
    public bool Required { get; init; }

    /// <inheritdoc />
    public IReranker? TryCreate(IServiceProvider services, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(services);

        lock (_sync)
        {
            if (!_attempted)
            {
                _attempted = true;

                var asker = services.GetService<IChoiceAsker>();
                if (asker is null)
                {
                    // The common misconfiguration, and the reason is worth being specific about:
                    // this package cannot ship a client adapter, so the host must supply one.
                    _failureReason =
                        "No IChoiceAsker is registered. Register an adapter over your TypeSafe client "
                        + "(for example Jev.Sdk) with services.AddSingleton<IChoiceAsker, ...> before "
                        + "registering this reranker.";
                }
                else
                {
                    try
                    {
                        _instance = new TypeSafeChoiceReranker(asker, _options);
                    }
                    catch (HindsightConfigurationException ex)
                    {
                        _failureReason = $"{ex.GetType().Name}: {ex.Message}";
                    }
                }
            }

            reason = _failureReason;

            if (_instance is null && Required)
            {
                throw new HindsightConfigurationException(
                    $"The TypeSafe reranker is required but could not be created. {_failureReason}");
            }

            return _instance;
        }
    }

    /// <inheritdoc />
    public IReranker GetRequired(IServiceProvider services)
    {
        var reranker = TryCreate(services, out var reason);

        return reranker ?? throw new HindsightConfigurationException(
            $"The TypeSafe reranker could not be created. {reason}");
    }
}

/// <summary>Registration for the TypeSafe reranker.</summary>
public static class TypeSafeRerankerServiceCollectionExtensions
{
    /// <summary>
    /// Registers TypeSafe Choice-based reranking as the container's reranker provider.
    /// </summary>
    /// <remarks>
    /// Requires an <see cref="IChoiceAsker"/> to already be registered. This package deliberately
    /// does not reference a TypeSafe client, because <c>Jev.Sdk</c> is not published and a published
    /// package cannot depend on an unpublished one; adapting the client is a few lines and belongs
    /// to the host.
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <param name="options">The options. Null means the defaults.</param>
    /// <param name="required">
    /// True to fail at first use when the client is missing, rather than falling back to merge order.
    /// </param>
    public static IServiceCollection AddTypeSafeReranker(
        this IServiceCollection services,
        TypeSafeRerankerOptions? options = null,
        bool required = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered directly rather than through the DI package's helper: this package must not
        // depend on that one, so a host with no container, or a different one, does not pull it in.
        ServiceCollectionDescriptorExtensions.TryAddSingleton<IRerankerFactory>(
            services,
            new TypeSafeRerankerFactory(options) { Required = required });

        return services;
    }
}
