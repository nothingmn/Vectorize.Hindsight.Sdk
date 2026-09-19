namespace Vectorize.Hindsight.Sdk.Admin;

/// <summary>
/// A client for the Hindsight admin CLI.
/// </summary>
/// <remarks>
/// Part of <see cref="HindsightAdminClient"/>, split by function: this file holds the commands that
/// concern the database as a whole.
/// </remarks>
public sealed partial class HindsightAdminClient
{
    /// <summary>
    /// Backs up the whole database to a zip file.
    /// </summary>
    /// <remarks>
    /// Copies every table in dependency order. The returned result carries the raw progress output,
    /// and <see cref="AdminResult.BackedUpRows"/> and <see cref="AdminResult.BackedUpTables"/> expose
    /// the parsed totals when the summary was recognised.
    /// </remarks>
    /// <param name="outputPath">Where to write the <c>.zip</c>. Required by the CLI.</param>
    /// <param name="schema">The database schema. Null uses the CLI's default (<c>public</c>).</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>The result.</returns>
    public Task<AdminResult> BackupAsync(
        string outputPath, string? schema, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var command = new AdminCommand
        {
            Subcommand = "backup",
            Arguments = [outputPath],
            Options = SchemaOnly(schema),
        };

        return RunOrThrowAsync(command, cancellationToken);
    }

    /// <summary>
    /// Restores the database from a backup, deleting everything currently in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Destructive.</b> The CLI's own help says "This deletes all existing data."
    /// </para>
    /// <para>
    /// <paramref name="confirm"/> is required and has no default, so this call cannot be reached by
    /// accident. When it is <see langword="false"/> the CLI's confirmation prompt is left in place
    /// and stdin is closed, so the process aborts immediately with exit code 1 and changes nothing.
    /// When it is <see langword="true"/> the CLI's <c>--yes</c> is passed.
    /// </para>
    /// <para>
    /// This does not throw on a declined confirmation, because declining is the expected outcome when
    /// confirmation is withheld. The caller reads <see cref="AdminResult.WasDeclined"/>. It does
    /// throw for a usage error or a failure part-way through, which are genuinely exceptional.
    /// </para>
    /// </remarks>
    /// <param name="inputPath">The <c>.zip</c> to restore from.</param>
    /// <param name="schema">The schema to restore into. Null uses the CLI's default.</param>
    /// <param name="confirm">True to pass <c>--yes</c>. False leaves the prompt in place, which aborts.</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>The result. <see cref="AdminResult.WasDeclined"/> is true when confirmation was absent.</returns>
    public async Task<AdminResult> RestoreAsync(
        string inputPath, string? schema, bool confirm, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        var options = new List<KeyValuePair<string, string>>(SchemaOnly(schema));
        AddConfirmation(options, confirm);

        var command = new AdminCommand
        {
            Subcommand = "restore",
            Arguments = [inputPath],
            Options = options,
        };

        var result = await RunAsync(command, cancellationToken).ConfigureAwait(false);

        // A decline is reported, not raised. Any other non-zero code is a real failure and is raised,
        // so a caller cannot mistake a rejected invocation for a successful no-op.
        if (!result.Succeeded && !result.WasDeclined)
        {
            result.EnsureSuccess();
        }

        return result;
    }

    /// <summary>
    /// Runs database migrations to the latest version.
    /// </summary>
    /// <remarks>
    /// Separate from API startup so a deployment can migrate before rolling out, or from CI. With no
    /// schema it migrates the base schema plus every discovered tenant schema.
    /// </remarks>
    /// <param name="schema">A single schema to migrate. Null migrates all.</param>
    /// <param name="embeddingDimension">
    /// An embedding dimension to enforce after migrating. Null skips the dimension sync.
    /// </param>
    /// <param name="skipExtensionReconcile">
    /// Skip the post-migration index reconcile. Only correct when the vector and text-search
    /// backends are unchanged — a backend change still needs a normal run to reshape the indexes.
    /// </param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>The result.</returns>
    public Task<AdminResult> RunDatabaseMigrationAsync(
        string? schema,
        int? embeddingDimension,
        bool skipExtensionReconcile,
        CancellationToken cancellationToken)
    {
        var options = new List<KeyValuePair<string, string>>(SchemaOnly(schema));

        if (embeddingDimension is { } dimension)
        {
            options.Add(new KeyValuePair<string, string>(
                "--embedding-dimension",
                dimension.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (skipExtensionReconcile)
        {
            options.Add(new KeyValuePair<string, string>("--skip-extension-reconcile", string.Empty));
        }

        var command = new AdminCommand { Subcommand = "run-db-migration", Options = options };
        return RunOrThrowAsync(command, cancellationToken);
    }

    /// <summary>Builds the <c>--schema</c> option, omitting it when no schema was named.</summary>
    /// <param name="schema">The schema, or null.</param>
    /// <returns>The option list.</returns>
    private static List<KeyValuePair<string, string>> SchemaOnly(string? schema) =>
        string.IsNullOrWhiteSpace(schema)
            ? []
            : [new KeyValuePair<string, string>("--schema", schema)];

    /// <summary>
    /// Appends the CLI's confirmation flag, but only when the caller asked for it.
    /// </summary>
    /// <remarks>
    /// Never defaulted to true. Supplying <c>--yes</c> on the caller's behalf would turn an
    /// accidental call into a data-losing one, which is precisely what the prompt exists to prevent.
    /// </remarks>
    /// <param name="options">The option list to append to.</param>
    /// <param name="confirm">Whether the caller confirmed.</param>
    private static void AddConfirmation(List<KeyValuePair<string, string>> options, bool confirm)
    {
        if (confirm)
        {
            options.Add(new KeyValuePair<string, string>("--yes", string.Empty));
        }
    }
}
