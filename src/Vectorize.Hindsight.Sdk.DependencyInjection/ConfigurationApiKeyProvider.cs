using Microsoft.Extensions.Configuration;
using Vectorize.Hindsight.Sdk.Authentication;

namespace Vectorize.Hindsight.Sdk.DependencyInjection;

/// <summary>
/// Resolves the API key from an <see cref="IConfiguration"/>, so the host decides
/// where the secret comes from — user-secrets during development, an environment
/// variable in a container, a secret store in production.
/// </summary>
/// <remarks>
/// <para>
/// The read itself is an in-memory lookup on a provider the host has already
/// built: there is no I/O and nothing is blocked. The asynchronous signature is
/// the library's <see cref="IApiKeyProvider"/> contract, and is kept here so a
/// provider that does perform I/O — a vault or a managed identity — can be
/// substituted without a breaking change or a sync-over-async bridge.
/// </para>
/// <para>
/// The key is never logged and never copied into
/// <see cref="Configuration.HindsightClientOptions"/>, where an options dump
/// could render it.
/// </para>
/// </remarks>
public sealed class ConfigurationApiKeyProvider : IApiKeyProvider
{
    /// <summary>
    /// The key <see cref="ConfigurationApiKeyProvider(IConfiguration)"/> reads,
    /// relative to the configuration root.
    /// </summary>
    public const string DefaultConfigurationKey = "Hindsight:ApiKey";

    private readonly IConfiguration _configuration;
    private readonly string _configurationKey;

    /// <summary>Reads the key from <see cref="DefaultConfigurationKey"/>.</summary>
    /// <param name="configuration">The host's configuration.</param>
    public ConfigurationApiKeyProvider(IConfiguration configuration)
        : this(configuration, DefaultConfigurationKey)
    {
    }

    /// <summary>Reads the key from a specific configuration path.</summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="configurationKey">
    /// The path to read, e.g. <c>Hindsight:ApiKey</c>. A blank value is rejected
    /// rather than silently treated as the default.
    /// </param>
    public ConfigurationApiKeyProvider(IConfiguration configuration, string configurationKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);

        _configuration = configuration;
        _configurationKey = configurationKey;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A missing, empty or whitespace-only value is reported as
    /// <see langword="null"/> — the deployment needs no key — rather than as an
    /// empty string that would put a bare <c>Bearer </c> on the wire.
    /// </remarks>
    public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var value = _configuration[_configurationKey];

        return new ValueTask<string?>(string.IsNullOrWhiteSpace(value) ? null : value);
    }
}
