namespace Vectorize.Hindsight.Sdk.Admin;

/// <summary>
/// A client for the Hindsight admin CLI.
/// </summary>
/// <remarks>
/// <para>
/// This drives <c>hindsight-admin</c>, which connects to PostgreSQL directly rather than over HTTP.
/// That is the whole reason this is a separate package: the shape of a call is different. There is
/// no request and typed response; there is an invocation, an exit code, and two text streams.
/// </para>
/// <para>
/// <b>Nothing here runs automatically.</b> No command is issued at construction, on a schedule, or
/// as a side effect of another call. Several of these commands are destructive — <c>restore</c>
/// deletes all existing data, <c>decommission-*</c> releases work from live workers — so every one
/// is an explicit call by the caller.
/// </para>
/// <para>
/// <b>Destructive commands need explicit confirmation.</b> The CLI prompts for three of them. This
/// client does not pass the CLI's <c>--yes</c> flag unless the caller asks for it, so the default
/// path is the safe one and an accidental call aborts rather than destroying data.
/// </para>
/// <para>
/// Every method is asynchronous and honours its <see cref="CancellationToken"/>. There is no
/// synchronous surface, matching the HTTP client.
/// </para>
/// </remarks>
public sealed partial class HindsightAdminClient : IDisposable
{
    private readonly HindsightAdminOptions _options;
    private readonly IAdminProcessRunner _runner;
    private readonly bool _ownsRunner;
    private bool _disposed;

    /// <summary>Creates a client with default options, invoking <c>hindsight-admin</c> on the PATH.</summary>
    public HindsightAdminClient()
        : this(new HindsightAdminOptions())
    {
    }

    /// <summary>Creates a client over an executable.</summary>
    /// <param name="executable">
    /// The executable to invoke. A bare name is resolved on the PATH; a full path is used directly.
    /// </param>
    public HindsightAdminClient(string executable)
        : this(new HindsightAdminOptions { Executable = executable })
    {
    }

    /// <summary>Creates a client.</summary>
    /// <param name="options">The options. Null means defaults.</param>
    public HindsightAdminClient(HindsightAdminOptions? options = null)
    {
        _options = options ?? new HindsightAdminOptions();
        HindsightAdminOptionsValidator.Validate(_options);

        if (_options.Runner is not null)
        {
            _runner = _options.Runner;
            _ownsRunner = false;
        }
        else
        {
            _runner = new SystemAdminProcessRunner(_options, _options.Logger);
            _ownsRunner = true;
        }
    }

    /// <summary>The options this client was built with.</summary>
    public HindsightAdminOptions Options => _options;

    /// <summary>
    /// The invocation prefix a command will be run with, for diagnosis.
    /// </summary>
    /// <remarks>
    /// Lets an operator confirm what the bridge will actually execute — particularly the
    /// <c>docker exec</c> or <c>kubectl exec</c> prefix, which is the part most likely to be wrong.
    /// </remarks>
    public string DescribeInvocationPrefix() =>
        string.Join(' ', new[] { _options.Executable }.Concat(_options.LauncherPrefixArguments));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Only dispose a runner this client created. A caller-supplied runner belongs to the caller.
        if (_ownsRunner && _runner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>Runs an arbitrary command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>The result, unchecked.</returns>
    /// <remarks>
    /// The escape hatch. Every typed method below is a thin wrapper over this, and it exists so a
    /// caller is never blocked by this library's option model lagging a CLI release.
    /// </remarks>
    public async Task<AdminResult> RunAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);

        var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);

        if (result.IsUsageError && _options.Logger is { } logger)
        {
            Telemetry.HindsightAdminLog.UsageError(logger, command.Subcommand, result.ExitCode);
        }

        return result;
    }

    /// <summary>Runs a command and raises when it does not succeed.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Honoured through the whole run.</param>
    /// <returns>The successful result.</returns>
    /// <exception cref="HindsightAdminException">The command did not exit zero.</exception>
    public async Task<AdminResult> RunOrThrowAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        var result = await RunAsync(command, cancellationToken).ConfigureAwait(false);
        return result.EnsureSuccess();
    }

    /// <summary>Builds a command, without running it.</summary>
    /// <param name="subcommand">The subcommand.</param>
    /// <param name="arguments">Positional arguments, in order.</param>
    public static AdminCommand Command(string subcommand, params string[] arguments) =>
        new() { Subcommand = subcommand, Arguments = arguments };
}

/// <summary>
/// Validates options at construction, so a contradiction fails where it can still be handled.
/// </summary>
internal static class HindsightAdminOptionsValidator
{
    internal static void Validate(HindsightAdminOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Executable))
        {
            throw new Errors.HindsightConfigurationException(
                "Executable is empty. The default is "
                + $"'{HindsightAdminOptions.DefaultExecutable}', which must be on the PATH; set a full path "
                + "when it is not.");
        }

        if (options.Timeout < TimeSpan.Zero)
        {
            throw new Errors.HindsightConfigurationException(
                "Timeout cannot be negative. Use TimeSpan.Zero for no deadline.");
        }

        foreach (var prefix in options.LauncherPrefixArguments)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                throw new Errors.HindsightConfigurationException(
                    "LauncherPrefixArguments contains an empty entry, which would be read as a missing argument.");
            }
        }
    }
}
