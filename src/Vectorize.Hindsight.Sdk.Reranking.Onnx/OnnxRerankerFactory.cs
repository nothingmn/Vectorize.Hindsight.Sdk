using Microsoft.Extensions.DependencyInjection;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Reranking;

namespace Vectorize.Hindsight.Sdk.Reranking.Onnx;

/// <summary>
/// Creates the local ONNX reranker from the host's configuration, and caches it.
/// </summary>
/// <remarks>
/// <para>
/// Registering this is how a host selects local ONNX reranking. <b>Nothing in the library branches
/// on "onnx" to pick it</b> — the selection is the registration itself, so a host that registers a
/// different <see cref="IRerankerFactory"/> gets that one with no change to this package or to the
/// core.
/// </para>
/// <para>
/// <b>Construction is deferred to first use and then cached.</b> Loading the model reads hundreds of
/// megabytes from disk and initialises a graph; doing that during container construction would fail
/// a host's startup over a feature it may never exercise. One instance is shared afterwards, because
/// the session is the expensive part and it is thread-safe.
/// </para>
/// <para>
/// <b>A missing or broken model does not throw by default.</b> <see cref="TryCreate"/> reports the
/// reason and returns null, and the caller falls back to the identity reranker — losing the ranking
/// is a better outcome than losing cross-bank recall. A host that has decided otherwise sets
/// <see cref="Required"/> and gets a
/// <see cref="HindsightConfigurationException"/> at first use instead.
/// </para>
/// </remarks>
public sealed class OnnxRerankerFactory : IRerankerFactory, IDisposable
{
    private readonly OnnxRerankerOptions _options;
    private readonly Lock _sync = new();
    private OnnxReranker? _instance;
    private string? _failureReason;
    private bool _attempted;

    /// <summary>Creates a factory.</summary>
    /// <param name="options">
    /// The reranker options, including the model and tokenizer paths. Required.
    /// </param>
    public OnnxRerankerFactory(OnnxRerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public string ProviderName => "onnx";

    /// <summary>
    /// Whether a failure to build should throw rather than fall back.
    /// </summary>
    /// <remarks>
    /// Off by default. A host with a hard requirement for local ranking turns it on and fails at
    /// startup, which is the right time to discover that a model file is missing.
    /// </remarks>
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

                try
                {
                    // The logger comes from the container when one is available, so the reranker's
                    // own diagnostics land in the host's pipeline rather than nowhere.
                    var logger = _options.Logger
                        ?? services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                            ?.CreateLogger("Vectorize.Hindsight.Sdk.Reranking.Onnx");

                    _instance = new OnnxReranker(_options with { Logger = logger });
                }
                catch (Exception ex) when (ex is HindsightConfigurationException or HindsightProtocolException)
                {
                    // Recorded once and reused, so a broken model does not re-read the file on every
                    // recall - and so the reason is stable rather than changing between calls.
                    _failureReason = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            reason = _failureReason ?? (_instance is null
                ? "The ONNX reranker could not be created."
                : null);

            if (_instance is null && Required)
            {
                throw new HindsightConfigurationException(
                    $"The ONNX reranker is required but could not be created. {_failureReason}");
            }

            return _instance;
        }
    }

    /// <inheritdoc />
    public IReranker GetRequired(IServiceProvider services)
    {
        var reranker = TryCreate(services, out var reason);

        return reranker ?? throw new HindsightConfigurationException(
            $"The ONNX reranker could not be created. {reason}");
    }

    /// <summary>
    /// Releases the session this factory created, if it created one.
    /// </summary>
    /// <remarks>
    /// The factory owns the instance it caches, so it is the thing that must release it — the
    /// container disposes the factory, and this is where the model's native memory goes back. A
    /// factory that never built one disposes trivially.
    /// </remarks>
    public void Dispose()
    {
        lock (_sync)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }
}

/// <summary>
/// Registration for the local ONNX reranker.
/// </summary>
public static class OnnxRerankerServiceCollectionExtensions
{
    /// <summary>
    /// Registers local ONNX reranking as the container's reranker provider.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The reranker options, including the model and tokenizer paths.</param>
    /// <param name="required">
    /// True to fail at first use when the model cannot be loaded, rather than falling back to the
    /// merge order.
    /// </param>
    /// <remarks>
    /// The model is not loaded here. Everything expensive happens on first resolution, so a host
    /// that registers this and never recalls across banks pays nothing.
    /// </remarks>
    public static IServiceCollection AddOnnxReranker(
        this IServiceCollection services,
        OnnxRerankerOptions options,
        bool required = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // Registered directly rather than through the DI package's helper: this package must not
        // depend on that one, or a host using no container at all - or a different one - would pull
        // it in for nothing. TryAdd keeps the first-registration-wins semantics either way.
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .TryAddSingleton<IRerankerFactory>(
                services,
                new OnnxRerankerFactory(options) { Required = required });

        return services;
    }
}
