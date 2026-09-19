using Vectorize.Hindsight.Sdk.Models;

namespace Vectorize.Hindsight.Sdk;

/// <summary>
/// The typed bank-configuration surface.
/// </summary>
/// <remarks>
/// <para>
/// The API models a configuration update as an open map of wire keys to JSON values, which is
/// what lets a newer server accept a field this client does not know about. That shape is the
/// right <i>transport</i> and the wrong <i>interface</i>: it makes a caller learn the server's
/// property names, spell them by hand, and build <c>JsonElement</c> values for typed settings.
/// </para>
/// <para>
/// <see cref="BankConfigurationPatch"/> is the typed alternative, and this overload is what makes
/// it reachable: a caller sets named, typed properties and never sees
/// <c>"enable_auto_consolidation"</c>. A member left unset is omitted; a member set to
/// <c>BankConfigValue&lt;T&gt;.Clear()</c> sends an explicit null, which the server treats as
/// "remove this override".
/// </para>
/// <para>
/// The map-based <see cref="UpdateBankConfigAsync(string, BankConfigUpdate, System.Threading.CancellationToken)"/>
/// remains the escape hatch for a field added to the server after this client shipped.
/// </para>
/// </remarks>
public sealed partial class HindsightClient
{
    /// <summary>
    /// Updates a bank's configuration from a typed patch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only members that were explicitly set are sent. The server validates each one against the
    /// field's declared type, rejects an unknown field, and — where an update is gated by
    /// <c>HINDSIGHT_API_ENABLE_BANK_CONFIG_API</c> — refuses the write. Reads are never gated.
    /// </para>
    /// <para>
    /// A typed patch cannot express a field this SDK version does not model. Use the
    /// <see cref="BankConfigUpdate"/> overload when you need one.
    /// </para>
    /// </remarks>
    /// <param name="bankId">The bank whose overrides to update.</param>
    /// <param name="patch">The typed overrides. At least one member should be set.</param>
    /// <param name="cancellationToken">Honoured through every await.</param>
    /// <returns>The bank's configuration as the server resolved it after the write.</returns>
    [HindsightComposition]
    public async Task<BankConfigResponse> UpdateBankConfigAsync(
        string bankId,
        BankConfigurationPatch patch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);

        return await UpdateBankConfigAsync(bankId, patch.ToUpdate(), cancellationToken)
            .ConfigureAwait(false);
    }
}
