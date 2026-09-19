using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Vectorize.Hindsight.Sdk.Admin.Telemetry;

namespace Vectorize.Hindsight.Sdk.Admin;

/// <summary>
/// Runs <c>hindsight-admin</c> as a child process.
/// </summary>
/// <remarks>
/// <para>
/// Three things here are deliberate, and each one is a defect avoided rather than a preference.
/// </para>
/// <para>
/// <b>Stdin is closed.</b> The CLI prompts for confirmation on <c>restore</c>,
/// <c>decommission-worker</c> and <c>decommission-workers</c>. A prompt with a terminal attached
/// would block a service forever. Verified: with stdin closed the prompt reads end-of-file and the
/// process exits <c>1</c> immediately, so a missing confirmation becomes a fast, legible "declined"
/// rather than a hang.
/// </para>
/// <para>
/// <b>Both streams are read concurrently.</b> Reading one to completion before the other deadlocks
/// as soon as the unread pipe fills, and <c>backup</c> prints twenty-three progress lines. The
/// reads start before the wait, not after it.
/// </para>
/// <para>
/// <b>The timeout kills the process tree.</b> These commands can have started something
/// long-running; killing only the parent would leave an orphan holding a database connection.
/// </para>
/// </remarks>
internal sealed class SystemAdminProcessRunner : IAdminProcessRunner
{
    private readonly HindsightAdminOptions _options;
    private readonly ILogger? _logger;

    internal SystemAdminProcessRunner(HindsightAdminOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AdminResult> RunAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var startInfo = BuildStartInfo(command);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                throw new HindsightAdminProcessException(
                    $"The admin executable '{_options.Executable}' could not be started.",
                    command);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A missing executable, a path that is not executable, or a permission problem. Start()
            // throws a platform exception for these, so without this a caller receives a raw
            // Win32Exception whose message names the working directory rather than the option that
            // was actually wrong.
            throw new HindsightAdminProcessException(
                $"The admin executable '{_options.Executable}' could not be started. Check "
                + "HindsightAdminOptions.Executable; the CLI is not always on the host PATH and may "
                + "need a container prefix.",
                command,
                ex);
        }

        // Close stdin immediately. This is what turns a confirmation prompt into a fast "declined"
        // instead of an indefinite hang: the CLI reads end-of-file and aborts.
        CloseStandardInput(process);

        // Both reads begin before the wait. A pipe buffer is finite, so draining one stream to the
        // end while the other fills deadlocks - and `backup` writes twenty-three progress lines.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Zero or negative means no timeout, leaving the caller's own token in effect.
        var hasTimeout = _options.Timeout > TimeSpan.Zero;
        if (hasTimeout)
        {
            timeoutSource.CancelAfter(_options.Timeout);
        }

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled. Kill the child so it does not outlive the request.
            KillTree(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            // The deadline fired. Kill the tree: killing only the parent would orphan anything it
            // spawned, and an orphaned admin command can hold a database connection open.
            KillTree(process);

            throw new HindsightAdminProcessException(
                $"hindsight-admin {command.Subcommand} exceeded the {_options.Timeout} timeout and was terminated.",
                command,
                wasTimeout: true);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();

        var result = new AdminResult
        {
            Command = command,
            ExitCode = process.ExitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            Elapsed = stopwatch.Elapsed,
        };

        // Guarded rather than passed through: a LoggerMessage-generated method dereferences its
        // logger, so "silent when absent" has to be implemented here.
        if (_logger is { } logger)
        {
            HindsightAdminLog.CommandCompleted(logger, command.Subcommand, result.ExitCode, stopwatch.Elapsed.TotalMilliseconds);
        }

        return result;
    }

    private ProcessStartInfo BuildStartInfo(AdminCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Executable,

            // Redirected so the bridge owns both streams; stdin is deliberately not fed.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,

            // Never create a window. On a headless host this is the difference between running and
            // failing, and on a desktop it stops a console flashing up per call.
            CreateNoWindow = true,
        };

        foreach (var prefix in _options.LauncherPrefixArguments)
        {
            startInfo.ArgumentList.Add(prefix);
        }

        startInfo.ArgumentList.Add(command.Subcommand);

        foreach (var option in command.Options)
        {
            startInfo.ArgumentList.Add(option.Key);
            if (!string.IsNullOrEmpty(option.Value))
            {
                startInfo.ArgumentList.Add(option.Value);
            }
        }

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in _options.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        return startInfo;
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill. Nothing to do.
        }
        catch (NotSupportedException)
        {
            // A platform where killing the tree is unavailable. The parent dies regardless.
        }
    }

    /// <summary>
    /// Closes stdin so a confirmation prompt cannot block.
    /// </summary>
    /// <remarks>
    /// Called after the process has started. Without this a prompted command waits on a stream
    /// nobody will write to, which inside a service is an indefinite hang.
    /// </remarks>
    internal static void CloseStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
            // The process had already exited and the stream was never open.
        }
    }
}

/// <summary>A failure to run or complete an admin process.</summary>
public sealed class HindsightAdminProcessException : Errors.HindsightException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="command">The command that failed.</param>
    /// <param name="wasTimeout">Whether a deadline was the cause.</param>
    public HindsightAdminProcessException(string message, AdminCommand command, bool wasTimeout = false)
        : base(message)
    {
        Command = command;
        WasTimeout = wasTimeout;
    }

    /// <summary>Creates the exception with the platform failure that caused it.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="command">The command that failed.</param>
    /// <param name="innerException">The underlying failure, e.g. a missing executable.</param>
    public HindsightAdminProcessException(string message, AdminCommand command, Exception innerException)
        : base(message, innerException)
    {
        Command = command;
    }

    /// <summary>The command that failed.</summary>
    public AdminCommand Command { get; }

    /// <summary>True when the failure was the configured deadline rather than a spawn error.</summary>
    public bool WasTimeout { get; }
}
