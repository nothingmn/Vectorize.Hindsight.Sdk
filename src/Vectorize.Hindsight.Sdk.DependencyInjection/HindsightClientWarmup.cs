using Microsoft.Extensions.DependencyInjection;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Models;

namespace Vectorize.Hindsight.Sdk.DependencyInjection;

/// <summary>
/// An opt-in connectivity check for a host that has already registered the
/// client.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this package calls this for you. The client's constructor performs
/// no I/O by design, so a host that wants to know its endpoint is reachable —
/// and to fail fast, before serving traffic — calls
/// <see cref="WarmAsync(IServiceProvider, CancellationToken)"/> from its own
/// startup path, where it can await and decide what a failure means.
/// </para>
/// <para>
/// Failure is reported, never swallowed: a cancelled token throws
/// <see cref="OperationCanceledException"/>, and a transport failure, non-success
/// status or unparseable body throws the library's own exception for that
/// condition. A catch block here would deny the host the distinction between
/// "the server is not up yet" and "the key is wrong", which is the only reason
/// the check is worth making.
/// </para>
/// <para>
/// The request is <c>GET /version</c>: unauthenticated in the specification,
/// idempotent, and it returns a fixed payload, so it is the cheapest call that
/// still proves a connection, a route and a deserialization round trip.
/// </para>
/// </remarks>
public static class HindsightClientWarmup
{
    /// <summary>
    /// Resolves the registered client and makes one real <c>GET /version</c> call.
    /// </summary>
    /// <param name="services">The container the client was registered in.</param>
    /// <param name="cancellationToken">Honoured through the call; no default, so a deadline is a deliberate choice.</param>
    /// <returns>The version the deployment reported.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="HindsightConfigurationException">
    /// No <see cref="HindsightClient"/> is registered, which means
    /// <c>AddHindsight</c> was not called on this container.
    /// </exception>
    public static async Task<VersionResponse> WarmAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        var client = services.GetService<HindsightClient>()
            ?? throw new HindsightConfigurationException(
                "No HindsightClient is registered in this container. Call "
                + "AddHindsight or AddHindsightFromConfiguration before warming.");

        return await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
    }
}
