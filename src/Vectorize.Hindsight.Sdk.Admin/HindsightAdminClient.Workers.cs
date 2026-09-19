namespace Vectorize.Hindsight.Sdk.Admin;

/// <summary>
/// A client for the Hindsight admin CLI.
/// </summary>
/// <remarks>
/// Part of <see cref="HindsightAdminClient"/>, split by function: this file holds worker management —
/// the commands an operator reaches for when a worker has died and left tasks claimed.
/// </remarks>
public sealed partial class HindsightAdminClient
{
    /// <summary>
    /// Lists the tasks each worker is currently processing.
    /// </summary>
    /// <remarks>
    /// The diagnostic that precedes a decommission: it shows each worker's active tasks with
    /// operation type, bank, how long the task has run, and when it last moved, which is how a dead
    /// worker holding orphaned tasks is identified. Read-only, so it needs no confirmation.
    /// </remarks>
    /// <param name="schema">The database schema. Null uses the CLI's default (<c>public</c>).</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>
    /// The result. <see cref="AdminResult.ReportedNoProcessingTasks"/> is true when nothing is
    /// running, which is distinguishable from an unparsed output.
    /// </returns>
    public Task<AdminResult> GetWorkerStatusAsync(string? schema, CancellationToken cancellationToken)
    {
        var command = new AdminCommand
        {
            Subcommand = "worker-status",
            Options = SchemaOnly(schema),
        };

        return RunOrThrowAsync(command, cancellationToken);
    }

    /// <summary>
    /// Releases every task one worker owns back to the queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a worker that crashed or was removed without a graceful shutdown: its claimed tasks are set
    /// back to pending so another worker can take them. Without this they stay claimed by an id that
    /// no longer exists.
    /// </para>
    /// <para>
    /// Like <c>restore</c>, <paramref name="confirm"/> is required with no default, and withholding it
    /// leaves the CLI's prompt in place to abort rather than acting.
    /// </para>
    /// </remarks>
    /// <param name="workerId">The worker whose tasks to release.</param>
    /// <param name="schema">The database schema. Null uses the CLI's default.</param>
    /// <param name="confirm">True to pass <c>--yes</c>. False leaves the prompt in place, which aborts.</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>The result. <see cref="AdminResult.WasDeclined"/> is true when confirmation was absent.</returns>
    public async Task<AdminResult> DecommissionWorkerAsync(
        string workerId, string? schema, bool confirm, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        var options = new List<KeyValuePair<string, string>>(SchemaOnly(schema));
        AddConfirmation(options, confirm);

        var command = new AdminCommand
        {
            Subcommand = "decommission-worker",
            Arguments = [workerId],
            Options = options,
        };

        return await RunAllowingDeclineAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases every processing task from every worker back to the queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The blunter recovery: every task currently in <c>processing</c> is released regardless of which
    /// worker owns it. Use it when more than one worker has died, and prefer the single-worker form
    /// when only one has.
    /// </para>
    /// <para>
    /// <paramref name="confirm"/> is required with no default, for the same reason as
    /// <c>restore</c>: the CLI warns "This will release ALL processing tasks from ALL workers".
    /// </para>
    /// </remarks>
    /// <param name="schema">The database schema. Null uses the CLI's default.</param>
    /// <param name="confirm">True to pass <c>--yes</c>. False leaves the prompt in place, which aborts.</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>The result. <see cref="AdminResult.WasDeclined"/> is true when confirmation was absent.</returns>
    public async Task<AdminResult> DecommissionWorkersAsync(
        string? schema, bool confirm, CancellationToken cancellationToken)
    {
        var options = new List<KeyValuePair<string, string>>(SchemaOnly(schema));
        AddConfirmation(options, confirm);

        var command = new AdminCommand { Subcommand = "decommission-workers", Options = options };

        return await RunAllowingDeclineAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a command where a declined confirmation is an outcome rather than a failure.
    /// </summary>
    /// <remarks>
    /// The three prompted commands share this: withholding confirmation is a legitimate way to ask
    /// "would this have gone ahead?", and the answer arrives as exit code 1 with the CLI's own
    /// prompt text. Raising on it would force every caller to catch an exception for a question they
    /// asked. Every other non-zero code is still raised, so a genuine failure cannot be mistaken for
    /// a deliberate decline.
    /// </remarks>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    private async Task<AdminResult> RunAllowingDeclineAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        var result = await RunAsync(command, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded && !result.WasDeclined)
        {
            result.EnsureSuccess();
        }

        return result;
    }
}
