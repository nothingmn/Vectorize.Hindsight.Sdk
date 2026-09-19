using Vectorize.Hindsight.Sdk.Authentication;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vectorize.Hindsight.Sdk.Configuration;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Http;
using Vectorize.Hindsight.Sdk.Telemetry;

namespace Vectorize.Hindsight.Sdk;

/// <summary>
/// A client for the Hindsight HTTP API.
/// </summary>
/// <remarks>
/// <para>
/// The client is a thin, deterministic layer over one transport. It is safe to
/// share across threads; the underlying <see cref="HttpClient"/> is.
/// </para>
/// <para>
/// The constructor performs <b>no I/O</b> and cannot fail on a network problem,
/// a bad key or a rate limit. There is nothing to await in a constructor, so
/// anything needing I/O — a key from a vault, configuration from disk — is
/// resolved through <see cref="IApiKeyProvider"/> at request time or loaded by
/// the host before construction.
/// </para>
/// <para>
/// Every operation on this type is asynchronous and honours its
/// <see cref="CancellationToken"/>. There is no synchronous surface.
/// </para>
/// </remarks>
public sealed partial class HindsightClient : IDisposable
{
    private readonly HindsightClientOptions _options;
    private readonly IHindsightTransport _transport;
    private readonly HindsightJson _json;
    private readonly HindsightTelemetry _telemetry;
    private readonly bool _ownsTransport;
    private bool _disposed;

    /// <summary>Creates a client with default options.</summary>
    public HindsightClient()
        : this(new HindsightClientOptions())
    {
    }

    /// <summary>Creates a client that points at a base address.</summary>
    /// <param name="baseAddress">The base address, e.g. <c>http://localhost:8888</c>.</param>
    public HindsightClient(string baseAddress)
        : this(new HindsightClientOptions { BaseAddress = baseAddress })
    {
    }

    /// <summary>Creates a client.</summary>
    /// <param name="options">The options. Null means defaults.</param>
    /// <param name="logger">An optional logger. Absent means silent.</param>
    public HindsightClient(HindsightClientOptions? options = null, ILogger<HindsightClient>? logger = null)
    {
        _options = options ?? new HindsightClientOptions();
        HindsightClientOptionsValidator.Validate(_options);

        _json = new HindsightJson(_options.ConfigureJson);

        if (_options.Transport is not null)
        {
            _transport = _options.Transport;
            _ownsTransport = false;
        }
        else
        {
            _transport = new HttpHindsightTransport(_options);
            _ownsTransport = true;
        }

        _telemetry = new HindsightTelemetry(logger);
    }

    /// <summary>The options this client was built with.</summary>
    public HindsightClientOptions Options => _options;

    /// <summary>The base address requests are sent to.</summary>
    public string BaseAddress => _options.BaseAddress;

    /// <summary>
    /// Builds the <c>/v1/{tenant}</c> prefix every bank-scoped route shares.
    /// </summary>
    /// <remarks>
    /// The specification hardcodes the literal <c>default</c>; it is configurable
    /// here so a non-default <c>TenantExtension</c> deployment does not require
    /// patching 85 call sites.
    /// </remarks>
    internal string TenantPrefix => $"/v1/{Uri.EscapeDataString(_options.Tenant)}";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsTransport)
        {
            _transport.Dispose();
        }

        _telemetry.Dispose();
    }
}

/// <summary>
/// Validates options at construction, so a contradiction fails where it can
/// still be handled rather than at the first request.
/// </summary>
internal static class HindsightClientOptionsValidator
{
    internal static void Validate(HindsightClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseAddress))
        {
            throw new HindsightConfigurationException(
                "BaseAddress is empty. HindsightClientOptions.DefaultBaseAddress is "
                + $"'{HindsightClientOptions.DefaultBaseAddress}'.");
        }

        if (!Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri))
        {
            throw new HindsightConfigurationException(
                $"BaseAddress '{options.BaseAddress}' is not an absolute URI.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new HindsightConfigurationException(
                $"BaseAddress '{options.BaseAddress}' must be http or https, not '{uri.Scheme}'.");
        }

        if (string.IsNullOrWhiteSpace(options.Tenant))
        {
            throw new HindsightConfigurationException("Tenant is empty.");
        }

        if (options.MaxAttempts < 1)
        {
            throw new HindsightConfigurationException(
                $"MaxAttempts must be at least 1, not {options.MaxAttempts}. Use 1 to disable retries.");
        }

        if (options.InitialRetryDelay < TimeSpan.Zero)
        {
            throw new HindsightConfigurationException("InitialRetryDelay cannot be negative.");
        }

        if (options.MaxRetryDelay < options.InitialRetryDelay)
        {
            throw new HindsightConfigurationException(
                "MaxRetryDelay must be at least InitialRetryDelay.");
        }

        if (options.RetryJitterFraction is < 0 or > 1)
        {
            throw new HindsightConfigurationException(
                $"RetryJitterFraction must be between 0 and 1, not {options.RetryJitterFraction}.");
        }

        if (options.Headers.ContainsKey("Authorization"))
        {
            throw new HindsightConfigurationException(
                "Authorization cannot be supplied through Headers. Set ApiKey or ApiKeyProvider instead, "
                + "so the credential is never handled as an ordinary string.");
        }
    }
}
