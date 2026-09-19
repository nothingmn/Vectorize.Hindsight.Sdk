namespace Vectorize.Hindsight.Sdk;

/// <summary>
/// Marks a client method that composes other operations rather than corresponding to one
/// operation in the API specification.
/// </summary>
/// <remarks>
/// <para>
/// The operation-coverage suite asserts that the number of public async methods on the client
/// equals the number of non-deprecated operations in the spec, which is what makes a newly
/// generated but unexercised method visible. A composition method breaks that count for a good
/// reason — <see cref="HindsightClient.RecallAcrossBanksAsync(string, IReadOnlyList{string}, CrossBank.CrossBankRecallOptions?, CancellationToken)"/>
/// is a fan-out over recall, not an operation of its own — so it declares itself here rather than
/// forcing the guard to be loosened for everything.
/// </para>
/// <para>
/// The distinction matters because the two have different failure modes. A spec operation can
/// drift from the contract; a composition can be wrong in its own logic. The first is guarded by
/// the count, the second by its own tests.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class HindsightCompositionAttribute : Attribute
{
}
