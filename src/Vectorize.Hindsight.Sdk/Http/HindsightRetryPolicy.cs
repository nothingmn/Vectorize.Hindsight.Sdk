namespace Vectorize.Hindsight.Sdk.Http;

/// <summary>
/// Decides whether a failed attempt may be retried, and how long to wait.
/// </summary>
/// <remarks>
/// Timing is a pure function of (attempt, server delay, options, jitter) so the
/// policy can be asserted without waiting. <c>TimeProvider</c> and the jitter
/// source are both injectable for the same reason.
/// </remarks>
internal static class HindsightRetryPolicy
{
    /// <summary>
    /// Whether a status may be retried at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>408</c>, <c>429</c> and every <c>5xx</c>. The <c>5xx</c> range is
    /// taken whole rather than naming <c>503</c> and <c>529</c> individually:
    /// retrying only the named ones would leave <c>500</c>, <c>502</c> and
    /// <c>504</c> un-retried, which is how a transient outage becomes a
    /// caller-visible failure.
    /// </para>
    /// <para>
    /// A <c>4xx</c> is never retried. <c>400</c>, <c>401</c>, <c>403</c>,
    /// <c>404</c> and <c>422</c> cannot be improved by repetition: the request
    /// or the credential is wrong, and repeating it only adds traffic.
    /// </para>
    /// </remarks>
    /// <param name="statusCode">The status returned.</param>
    internal static bool IsRetryableStatus(int statusCode) =>
        statusCode is 408 or 429 || statusCode is >= 500 and <= 599;

    /// <summary>
    /// Computes the delay before the next attempt.
    /// </summary>
    /// <param name="attempt">
    /// The attempt number that just failed, counting from 1. The delay returned
    /// is the wait before attempt <c>attempt + 1</c>.
    /// </param>
    /// <param name="retryAfter">The server's requested delay, when it sent one.</param>
    /// <param name="options">The options carrying the bounds.</param>
    /// <param name="jitter">A source of randomness in [0,1). Injectable for determinism.</param>
    /// <returns>The delay to wait.</returns>
    internal static TimeSpan ComputeDelay(
        int attempt,
        TimeSpan? retryAfter,
        Configuration.HindsightClientOptions options,
        Func<double> jitter)
    {
        // A server-supplied delay wins, because the server knows how long its own
        // queue is. It is bounded by MaxRetryAfter: a caller waiting minutes
        // inside one call would rather fail and retry at its own level.
        if (retryAfter is { } serverDelay && serverDelay > TimeSpan.Zero && serverDelay <= options.MaxRetryAfter)
        {
            return ApplyJitter(serverDelay, options, jitter);
        }

        // Exponential from InitialRetryDelay, bounded by MaxRetryDelay.
        var exponent = Math.Max(0, attempt - 1);
        var scaled = options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var bounded = Math.Min(scaled, options.MaxRetryDelay.TotalMilliseconds);

        return ApplyJitter(TimeSpan.FromMilliseconds(bounded), options, jitter);
    }

    /// <summary>
    /// Applies subtractive jitter.
    /// </summary>
    /// <remarks>
    /// Subtractive, not "full" jitter: each delay is reduced by a random
    /// fraction no greater than <c>RetryJitterFraction</c>, so the delay stays
    /// bounded below while still de-synchronising a burst. Full jitter would
    /// replace the delay with a value from zero, which can collapse a
    /// deliberately conservative backoff to no wait at all.
    /// </remarks>
    private static TimeSpan ApplyJitter(
        TimeSpan delay, Configuration.HindsightClientOptions options, Func<double> jitter)
    {
        var fraction = options.RetryJitterFraction;
        if (fraction <= 0)
        {
            return delay;
        }

        var random = Math.Clamp(jitter(), 0, 1);
        var reduction = random * fraction;

        return TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (1 - reduction));
    }

    /// <summary>
    /// Reads a server-supplied delay from the headers it may arrive in.
    /// </summary>
    /// <param name="headers">The response headers.</param>
    /// <returns>The delay, or null when none was sent or it was not readable.</returns>
    internal static TimeSpan? ReadRetryAfter(IReadOnlyDictionary<string, string> headers)
    {
        // The delta-seconds form, which is the one that matters. The HTTP-date
        // form is legal but rare, and the caller's own backoff beats parsing
        // dates against a clock that may differ from the server's.
        if (headers.TryGetValue("Retry-After", out var raw) &&
            double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (headers.TryGetValue("retry-after-ms", out var rawMs) &&
            double.TryParse(rawMs, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var milliseconds) &&
            milliseconds >= 0)
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        return null;
    }
}
