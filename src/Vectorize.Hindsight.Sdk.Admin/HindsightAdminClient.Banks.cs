namespace Vectorize.Hindsight.Sdk.Admin;

/// <summary>
/// A client for the Hindsight admin CLI.
/// </summary>
/// <remarks>
/// Part of <see cref="HindsightAdminClient"/>, split by function: this file holds the per-bank
/// commands.
/// </remarks>
public sealed partial class HindsightAdminClient
{
    /// <summary>
    /// Reconciles per-(bank, fact_type) vector index coverage.
    /// </summary>
    /// <remarks>
    /// Idempotent and safe to re-run. Reach for it when convergence is wanted without waiting for a
    /// write: after a restore or an upgrade, after a backend switch, or to shed indexes in bulk on a
    /// deployment recovering from lock-table exhaustion. All its DDL runs concurrently, so it does
    /// not block the live fleet.
    /// </remarks>
    /// <param name="bankId">One bank to repair. Mutually exclusive with <paramref name="allBanks"/>.</param>
    /// <param name="allBanks">Repair every bank in the base schema and all discovered tenant schemas.</param>
    /// <param name="schema">Limit to a single schema. Null uses the base plus discovered tenant schemas.</param>
    /// <param name="dryRun">Report what would change without creating or dropping any index.</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>The result. <see cref="AdminResult.RepairTotals"/> carries the parsed counts when recognised.</returns>
    public Task<AdminResult> RepairBankAsync(
        string? bankId,
        bool allBanks,
        string? schema,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        // The CLI rejects both together with a usage error, so the mistake is caught here where the
        // caller can see which argument was wrong, rather than as an exit code 2 from a child process.
        var hasBank = !string.IsNullOrWhiteSpace(bankId);
        if (hasBank == allBanks)
        {
            throw new Errors.HindsightValidationException(
                "RepairBankAsync needs exactly one of bankId or allBanks. The CLI treats them as "
                + "mutually exclusive and answers a usage error when both or neither are given.")
            {
                ParamName = hasBank ? nameof(allBanks) : nameof(bankId),
            };
        }

        var options = new List<KeyValuePair<string, string>>(SchemaOnly(schema));

        if (hasBank)
        {
            options.Add(new KeyValuePair<string, string>("--bank", bankId!));
        }
        else
        {
            options.Add(new KeyValuePair<string, string>("--all", string.Empty));
        }

        if (dryRun)
        {
            options.Add(new KeyValuePair<string, string>("--dry-run", string.Empty));
        }

        var command = new AdminCommand { Subcommand = "repair-bank", Options = options };
        return RunOrThrowAsync(command, cancellationToken);
    }

    /// <summary>
    /// Exports a whole bank to a portable zip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carries documents, facts, observations, bank config, mental models, directives and webhooks,
    /// but <b>no embeddings</b> — they are regenerated on import. That is what makes the archive
    /// portable across instances with a different embedding model, vector or text-search backend.
    /// </para>
    /// <para>
    /// Overlaps the HTTP <c>transfer/export</c> operation, and is not redundant: this one connects to
    /// the database directly, so it works when the API is not serving.
    /// </para>
    /// </remarks>
    /// <param name="bankId">The bank to export.</param>
    /// <param name="outputPath">Where to write the <c>.zip</c>.</param>
    /// <param name="schema">The schema the bank lives in. Null uses the configured base schema.</param>
    /// <param name="includeHistory">Also export operational history (audit log, LLM requests).</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>The result.</returns>
    public Task<AdminResult> ExportBankAsync(
        string bankId,
        string outputPath,
        string? schema,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var options = new List<KeyValuePair<string, string>>
        {
            new("--bank", bankId),
            new("--output", outputPath),
        };

        options.AddRange(SchemaOnly(schema));

        if (includeHistory)
        {
            options.Add(new KeyValuePair<string, string>("--include-history", string.Empty));
        }

        var command = new AdminCommand { Subcommand = "export-bank", Options = options };
        return RunOrThrowAsync(command, cancellationToken);
    }

    /// <summary>
    /// Restores a whole bank from an <c>export-bank</c> archive.
    /// </summary>
    /// <remarks>
    /// Re-embeds facts with this instance's configured embedding model and rebuilds links and
    /// indexes — the import half of a cross-instance migration. Run it against an instance
    /// configured with the desired backend.
    /// </remarks>
    /// <param name="archivePath">The <c>.zip</c> produced by <see cref="ExportBankAsync"/>.</param>
    /// <param name="schema">The target schema. Null uses the configured base schema.</param>
    /// <param name="targetBankId">Override the bank id instead of using the archive's source id.</param>
    /// <param name="includeHistory">Also restore operational history when the archive carries it.</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>The result.</returns>
    /// <exception cref="Errors.HindsightValidationException">
    /// The archive path is blank, which the CLI would reject as a usage error.
    /// </exception>
    public Task<AdminResult> ImportBankAsync(
        string archivePath,
        string? schema,
        string? targetBankId,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var options = new List<KeyValuePair<string, string>>
        {
            new("--archive", archivePath),
        };

        options.AddRange(SchemaOnly(schema));

        if (!string.IsNullOrWhiteSpace(targetBankId))
        {
            options.Add(new KeyValuePair<string, string>("--target-bank", targetBankId));
        }

        if (includeHistory)
        {
            options.Add(new KeyValuePair<string, string>("--include-history", string.Empty));
        }

        var command = new AdminCommand { Subcommand = "import-bank", Options = options };
        return RunOrThrowAsync(command, cancellationToken);
    }
}
