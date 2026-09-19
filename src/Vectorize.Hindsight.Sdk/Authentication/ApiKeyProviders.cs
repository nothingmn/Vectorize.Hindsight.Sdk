namespace Vectorize.Hindsight.Sdk.Authentication;

/// <summary>
/// Supplies the API key for requests.
/// </summary>
/// <remarks>
/// Asynchronous even though the built-in implementations have nothing to await,
/// so a vault-backed, managed-identity, or refreshing implementation can be
/// substituted without a breaking change. A key that arrives from a network
/// source must not be fetched synchronously.
/// </remarks>
public interface IApiKeyProvider
{
    /// <summary>
    /// Resolves the key, or <see langword="null"/> when the deployment needs none.
    /// </summary>
    /// <param name="cancellationToken">Honoured through every await.</param>
    /// <returns>The key, or null. Never logged by the library.</returns>
    ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken);
}

/// <summary>Returns a fixed key.</summary>
public sealed class StaticApiKeyProvider : IApiKeyProvider
{
    private readonly string? _apiKey;

    /// <summary>Creates a provider over a fixed key.</summary>
    /// <param name="apiKey">The key, or null for a deployment that needs none.</param>
    public StaticApiKeyProvider(string? apiKey) => _apiKey = apiKey;

    /// <inheritdoc />
    public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken) => new(_apiKey);
}

/// <summary>
/// Resolves a key through a sequence of providers, using the first non-empty
/// result.
/// </summary>
/// <remarks>
/// The priority ladder is expressed as a chain so the precedence is testable
/// without touching the process environment.
/// </remarks>
public sealed class ChainedApiKeyProvider : IApiKeyProvider
{
    private readonly IReadOnlyList<IApiKeyProvider> _providers;

    /// <summary>Creates a chain.</summary>
    /// <param name="providers">Consulted in order; the first non-empty result wins.</param>
    public ChainedApiKeyProvider(params IReadOnlyList<IApiKeyProvider> providers) =>
        _providers = providers ?? [];

    /// <inheritdoc />
    public async ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            var key = await provider.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(key))
            {
                return key;
            }
        }

        return null;
    }
}

/// <summary>Resolves a key from an environment variable.</summary>
public sealed class EnvironmentApiKeyProvider : IApiKeyProvider
{
    /// <summary>The variable the vendor's other clients also honour.</summary>
    public const string VariableName = "HINDSIGHT_API_KEY";

    private readonly string _variableName;
    private readonly Func<string, string?> _read;

    /// <summary>Creates a provider over <see cref="VariableName"/>.</summary>
    public EnvironmentApiKeyProvider()
        : this(VariableName, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Creates a provider over a specific variable, with an injectable reader.</summary>
    /// <param name="variableName">The variable to read.</param>
    /// <param name="read">The reader; injectable so precedence is testable.</param>
    public EnvironmentApiKeyProvider(string variableName, Func<string, string?> read)
    {
        _variableName = variableName;
        _read = read;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A resolved value is trimmed, matching <c>ConfigurationApiKeyProvider</c>. A
    /// padded environment variable — a trailing newline from a mounted secret, a
    /// stray space from a copy-paste — otherwise fails authentication with a message
    /// that gives no hint the whitespace is the cause.
    /// </remarks>
    public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var value = _read(_variableName);
        return new ValueTask<string?>(string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }
}
