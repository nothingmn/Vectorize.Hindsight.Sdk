using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Vectorize.Hindsight.Sdk.Telemetry;

/// <summary>
/// The library's telemetry surface: BCL only, no exporter.
/// </summary>
/// <remarks>
/// <para>
/// A consumer wires a sink; the library never takes an OpenTelemetry or exporter
/// dependency. Everything here is silent until someone listens.
/// </para>
/// <para>
/// Nothing caller-supplied is ever recorded. The <c>state</c> or content being
/// stored is not logged, not tagged on a span and not used as a metric tag at
/// any level, and no caller-chosen identifier becomes a tag — both because it can
/// be regulated content and because an unbounded tag value explodes cardinality.
/// Bank ids are caller-chosen and therefore also excluded; the route template is
/// recorded instead.
/// </para>
/// </remarks>
internal sealed class HindsightTelemetry : IDisposable
{
    /// <summary>The activity source name.</summary>
    internal const string ActivitySourceName = "Vectorize.Hindsight.Sdk";

    /// <summary>The meter name.</summary>
    internal const string MeterName = "Vectorize.Hindsight.Sdk";

    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _calls;
    private readonly Counter<long> _retries;
    private readonly Histogram<double> _duration;
    private readonly ILogger? _logger;

    internal HindsightTelemetry(ILogger? logger)
    {
        _logger = logger;

        _calls = _meter.CreateCounter<long>(
            "hindsight.client.calls",
            unit: "{call}",
            description: "Number of calls, tagged by outcome and operation.");

        _retries = _meter.CreateCounter<long>(
            "hindsight.client.retries",
            unit: "{retry}",
            description: "Number of retry attempts, tagged by operation and status.");

        _duration = _meter.CreateHistogram<double>(
            "hindsight.client.duration",
            unit: "s",
            description: "Call duration, tagged by outcome and operation.");
    }

    /// <summary>
    /// The optional logger, exposed so the pipeline can emit bounded messages.
    /// </summary>
    /// <remarks>
    /// Never null-guarded at each call site: the pipeline goes through the
    /// <c>Log</c> helpers below, so "silent when absent" is implemented once.
    /// </remarks>
    internal ILogger? Logger => _logger;

    /// <summary>True when a logger is attached.</summary>
    internal bool HasLogger => _logger is not null;

    /// <inheritdoc />
    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }

    /// <summary>Starts a span for an operation.</summary>
    /// <param name="operationName">The route template, e.g. <c>GET /v1/{tenant}/banks/{bank_id}</c>.</param>
    /// <param name="method">The HTTP method.</param>
    internal Activity? StartCall(string operationName, string method)
    {
        var activity = _activitySource.StartActivity(operationName, ActivityKind.Client);
        activity?.SetTag("http.request.method", method);
        activity?.SetTag("hindsight.operation", operationName);
        return activity;
    }

    /// <summary>Completes a span with its outcome.</summary>
    /// <param name="activity">The span.</param>
    /// <param name="operationName">The operation name used as a bounded metric tag.</param>
    /// <param name="statusCode">The status, when the server answered.</param>
    /// <param name="retryCount">How many retries were made.</param>
    /// <param name="elapsed">The total elapsed time.</param>
    /// <param name="succeeded">Whether the call succeeded.</param>
    internal void CompleteCall(
        Activity? activity,
        string operationName,
        int? statusCode,
        int retryCount,
        TimeSpan elapsed,
        bool succeeded)
    {
        if (activity is not null)
        {
            if (statusCode is not null)
            {
                activity.SetTag("http.response.status_code", statusCode);
            }

            activity.SetTag("hindsight.retry_count", retryCount);
            activity.SetStatus(succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        }

        var outcome = succeeded ? "success" : "failure";
        _calls.Add(1, new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("operation", operationName));
        _duration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("operation", operationName));

        if (retryCount > 0)
        {
            _retries.Add(retryCount, new KeyValuePair<string, object?>("operation", operationName),
                new KeyValuePair<string, object?>("status", statusCode?.ToString() ?? "transport"));
        }
    }

    /// <summary>
    /// Records that a retry is being delayed.
    /// </summary>
    /// <param name="operationName">The operation name, a bounded value.</param>
    /// <param name="attempt">The attempt number that failed.</param>
    /// <param name="delay">The delay being applied.</param>
    internal void RetryScheduled(string operationName, int attempt, TimeSpan delay)
    {
        // A structured log at Debug: the operation name is a route template and
        // the delay is a number, so neither is caller content.
        if (_logger is { } logger)
        {
            HindsightLog.RetryScheduled(logger, operationName, attempt, delay.TotalMilliseconds);
        }
    }
}
