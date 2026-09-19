using Vectorize.Hindsight.Sdk.Authentication;
using Vectorize.Hindsight.Sdk.Http;

namespace Vectorize.Hindsight.Sdk.Configuration;

/// <summary>
/// Options that shape a <see cref="HindsightClient"/>.
/// </summary>
/// <remarks>
/// Everything here has a working default. A caller that passes nothing gets a
/// client pointed at the documented local server with the documented retry
/// behaviour, and no key — which is correct for a single-tenant deployment
/// where the <c>authorization</c> header is optional.
/// </remarks>
public sealed class HindsightClientOptions
{
    /// <summary>
    /// The base address, without a trailing slash.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>http://localhost:8888</c>, which is what the published
    /// quick start uses and what the local test rig serves. The specification
    /// declares no <c>servers</c> block, so this default comes from the
    /// documentation rather than from the contract.
    /// </remarks>
    public string BaseAddress { get; set; } = DefaultBaseAddress;

    /// <summary>The documented default base address.</summary>
    public const string DefaultBaseAddress = "http://localhost:8888";

    /// <summary>
    /// The <c>/v1/{tenant}</c> path prefix.
    /// </summary>
    /// <remarks>
    /// The specification hardcodes the literal <c>default</c> in all 85 bank
    /// paths. A deployment running a non-default <c>TenantExtension</c> changes
    /// it, so it is configurable here rather than baked into 87 call sites.
    /// </remarks>
    public string Tenant { get; set; } = DefaultTenant;

    /// <summary>The tenant segment the specification hardcodes.</summary>
    public const string DefaultTenant = "default";

    /// <summary>
    /// The API key, when the deployment requires one.
    /// </summary>
    /// <remarks>
    /// Optional. The specification marks <c>authorization</c> as not required,
    /// and a default deployment accepts a request without it. When set, it is
    /// sent as <c>Bearer &lt;key&gt;</c>. Never logged.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Supplies the API key asynchronously, taking precedence over
    /// <see cref="ApiKey"/> when set.
    /// </summary>
    /// <remarks>
    /// Async from the outset even though the built-in implementations have
    /// nothing to await, so a vault-backed or managed-identity provider can be
    /// dropped in without a breaking change.
    /// </remarks>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>Additional headers sent with every request.</summary>
    /// <remarks>
    /// Applied after the library's own headers, so a caller can override a
    /// default such as <c>Accept</c>. <c>Authorization</c> cannot be set this
    /// way: attempting it is a configuration error, because a credential must
    /// arrive through <see cref="ApiKey"/> or <see cref="ApiKeyProvider"/> rather
    /// than as an ordinary string that could be logged.
    /// </remarks>
    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum attempts for a retryable call, including the first.</summary>
    /// <remarks>
    /// Retries apply only to statuses a repeat can improve, and only to
    /// idempotent operations. A synchronous retain is never retried, because its
    /// <c>operation_id</c> is ignored on the synchronous path and a retry could
    /// duplicate a write.
    /// </remarks>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>The first backoff delay.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The backoff ceiling.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest server-supplied <c>Retry-After</c> that will be honoured.
    /// </summary>
    /// <remarks>
    /// A server delay longer than this is discarded and the computed backoff
    /// used instead: a caller waiting minutes inside one call would rather fail
    /// and retry at its own level.
    /// </remarks>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The fraction by which each computed delay is reduced, as subtractive
    /// jitter.
    /// </summary>
    /// <remarks>
    /// Subtractive rather than "full" jitter: each delay is reduced by a random
    /// fraction no greater than this, so the delay stays bounded below while
    /// still spreading a synchronised burst.
    /// </remarks>
    public double RetryJitterFraction { get; set; } = 0.25;

    /// <summary>
    /// The per-attempt timeout.
    /// </summary>
    /// <remarks>
    /// Enforced with a linked <see cref="CancellationTokenSource"/> per attempt
    /// rather than by assigning <c>HttpClient.Timeout</c>, because that property
    /// is immutable once a client has served a request and callers legitimately
    /// pass pooled or factory-managed clients. Zero or negative means no
    /// per-attempt deadline, leaving the client's own timeout in effect.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// The transport to use. Defaults to the built-in HTTP transport.
    /// </summary>
    /// <remarks>
    /// The only external dependency, and the seam that makes every code path
    /// reachable from a test with no network.
    /// </remarks>
    public IHindsightTransport? Transport { get; set; }

    /// <summary>An <see cref="HttpClient"/> to use, when the caller supplies one.</summary>
    /// <remarks>
    /// Never mutated by the library. A pooled or factory-managed client is
    /// accepted, including one that has already served a request.
    /// </remarks>
    public HttpClient? HttpClient { get; set; }

    /// <summary>The <c>User-Agent</c> header.</summary>
    /// <remarks>
    /// Integrations should set this to identify themselves. Defaults to
    /// <c>vectorize-hindsight-dotnet/&lt;version&gt;</c>.
    /// </remarks>
    public string UserAgent { get; set; } = HindsightDefaults.UserAgent;

    /// <summary>
    /// A hook applied to a private copy of the serializer options at
    /// construction, then frozen.
    /// </summary>
    /// <remarks>
    /// Customisation is available but off by default: the client works with no
    /// hook at all, and the options are never exposed or mutated per call.
    /// </remarks>
    public Action<System.Text.Json.JsonSerializerOptions>? ConfigureJson { get; set; }
}
