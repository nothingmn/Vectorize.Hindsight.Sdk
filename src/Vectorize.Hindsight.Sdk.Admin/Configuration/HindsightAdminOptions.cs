namespace Vectorize.Hindsight.Sdk.Admin;

/// <summary>
/// Options for the admin bridge.
/// </summary>
/// <remarks>
/// The admin CLI talks to PostgreSQL directly rather than over HTTP, so there is no base address
/// here — there is an executable. Everything else about the shape follows from that: a run yields
/// an exit code and two text streams, not a typed response body.
/// </remarks>
public sealed class HindsightAdminOptions
{
    /// <summary>The executable name the vendor ships.</summary>
    public const string DefaultExecutable = "hindsight-admin";

    /// <summary>
    /// The executable to run. Defaults to <c>hindsight-admin</c> on the PATH.
    /// </summary>
    /// <remarks>
    /// The CLI is not guaranteed to be on the host. Verified: it is absent from this host's PATH
    /// and lives inside the API container at <c>/app/api/.venv/bin/hindsight-admin</c>, which is
    /// the documented deployment shape — the vendor's own instructions use <c>docker exec</c>,
    /// <c>kubectl exec</c>, or the host that owns <c>pg0</c>. So this is a bare name or a full
    /// path, and <see cref="LauncherPrefixArguments"/> carries whatever must precede the
    /// subcommand.
    /// </remarks>
    public string Executable { get; set; } = DefaultExecutable;

    /// <summary>
    /// Arguments placed between the executable and the subcommand.
    /// </summary>
    /// <remarks>
    /// This is what makes every deployment shape expressible without special cases:
    /// <list type="bullet">
    /// <item><c>docker exec -i hindsight-api</c> for a container</item>
    /// <item><c>kubectl exec deploy/hindsight-api --</c> for a cluster</item>
    /// </list>
    /// Empty for the bare-metal case, where the executable is invoked directly.
    /// <para>
    /// Note the <c>-i</c>: the CLI prompts for confirmation on its destructive commands, and a
    /// container command without a stdin attached would fail differently. The bridge closes stdin
    /// deliberately (see <see cref="IAdminProcessRunner"/>), so a missing confirmation aborts
    /// rather than hanging.
    /// </para>
    /// </remarks>
    public IList<string> LauncherPrefixArguments { get; } = new List<string>();

    /// <summary>Environment variables set for the process.</summary>
    /// <remarks>
    /// The CLI reads the same configuration as the API service, so
    /// <c>HINDSIGHT_API_DATABASE_URL</c> here is what points it at a particular database.
    /// Explicit rather than inherited by accident: a stray variable in the ambient environment
    /// should not decide which database a destructive command runs against.
    /// </remarks>
    public IDictionary<string, string> Environment { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The per-command timeout.
    /// </summary>
    /// <remarks>
    /// Generous by default, because these commands are not quick: <c>backup</c> copies every table,
    /// <c>run-db-migration</c> alters schemas, and <c>repair-bank --all</c> issues concurrent DDL
    /// per bank. A caller can raise it for a large deployment, or lower it where a hung command is
    /// worse than a failed one.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A runner to use instead of the real process launcher.
    /// </summary>
    /// <remarks>
    /// The seam that makes every code path reachable from a test with no process spawned —
    /// the same role <c>IHindsightTransport</c> plays on the HTTP side.
    /// </remarks>
    public IAdminProcessRunner? Runner { get; set; }

    /// <summary>
    /// An optional logger. Absent means silent.
    /// </summary>
    /// <remarks>
    /// A process runner has no natural logger of its own, so it is carried here rather than
    /// injected into the client, which has no generic type parameter to bind one to.
    /// </remarks>
    public Microsoft.Extensions.Logging.ILogger? Logger { get; set; }
}

/// <summary>One command to run.</summary>
public sealed class AdminCommand
{
    /// <summary>The subcommand, e.g. <c>worker-status</c>.</summary>
    public required string Subcommand { get; init; }

    /// <summary>Positional arguments, in the order the CLI expects them.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Options, each already prefixed with its dashes.</summary>
    /// <remarks>
    /// Literal pairs rather than a typed model per command. The CLI's option set differs per
    /// subcommand and grows with releases, so a typed model per command would be a large surface to
    /// keep in step with a binary this library does not control. The typed methods on
    /// <see cref="HindsightAdminClient"/> cover the common calls; this is the layer beneath them.
    /// </remarks>
    public IReadOnlyList<KeyValuePair<string, string>> Options { get; init; } = [];

    /// <summary>
    /// The invocation as it will be passed to the runner, for diagnosis and assertion.
    /// </summary>
    /// <remarks>
    /// Nothing is redacted because an admin command carries no credential: the connection string
    /// arrives through the environment, not the command line. That is worth stating, since a
    /// password on a command line would be visible to every process on the host — and it is a
    /// reason not to add one.
    /// </remarks>
    public string Describe()
    {
        var builder = new System.Text.StringBuilder(Subcommand);

        foreach (var option in Options)
        {
            builder.Append(' ').Append(option.Key);
            if (!string.IsNullOrEmpty(option.Value))
            {
                builder.Append(' ').Append(Quote(option.Value));
            }
        }

        foreach (var argument in Arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
