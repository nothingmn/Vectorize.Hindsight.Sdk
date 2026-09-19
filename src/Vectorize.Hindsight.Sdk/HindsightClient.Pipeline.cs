using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Http;
using Vectorize.Hindsight.Sdk.Telemetry;

namespace Vectorize.Hindsight.Sdk;

/// <summary>
/// The request pipeline: send, retry, map, deserialize, record.
/// </summary>
/// <remarks>
/// Every operation on the client funnels through here, so retry semantics, error
/// mapping, telemetry and cancellation are implemented once rather than 87 times.
/// </remarks>
public sealed partial class HindsightClient
{
    /// <summary>A jitter source. Replaced in tests to make delays deterministic.</summary>
    internal Func<double> Jitter { get; set; } = Random.Shared.NextDouble;

    /// <summary>A time source. Replaced in tests to avoid real waits.</summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Sends a request and deserializes the success body, retrying where allowed.
    /// </summary>
    /// <typeparam name="TResponse">The declared success shape.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Honoured through every await, including retry delays.</param>
    internal async Task<TResponse> SendAsync<TResponse>(
        HindsightRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // PathTemplate already carries the method, so adding it again produced
        // "GET GET /v1/..." - and this string is the span name and the metric tag,
        // so the defect reached every telemetry surface.
        var operationName = request.PathTemplate;
        using var activity = _telemetry.StartCall(operationName, request.Method.Method);
        var started = TimeProvider.GetTimestamp();

        var attempt = 0;
        var lastStatus = 0;

        while (true)
        {
            attempt++;
            HindsightResponse response;

            try
            {
                response = await _transport
                    .SendAsync(request, streamResponse: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HindsightConnectionException ex)
            {
                // A transport failure is retryable — the server may not have seen
                // the request — but only when the operation is idempotent and
                // attempts remain.
                if (CanRetry(request, attempt))
                {
                    await DelayBeforeRetryAsync(operationName, attempt, null, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                _telemetry.CompleteCall(activity, operationName, null, attempt - 1, Elapsed(started), false);
                LogUnreachable(operationName, ex.Message);
                throw;
            }

            lastStatus = response.StatusCode;

            if (IsSuccess(response.StatusCode))
            {
                _telemetry.CompleteCall(activity, operationName, response.StatusCode, attempt - 1, Elapsed(started), true);
                LogSucceeded(operationName, attempt);
                return Deserialize<TResponse>(response, operationName);
            }

            var retryAfter = HindsightRetryPolicy.ReadRetryAfter(response.Headers);

            if (CanRetry(request, attempt) && HindsightRetryPolicy.IsRetryableStatus(response.StatusCode))
            {
                await DelayBeforeRetryAsync(operationName, attempt, retryAfter, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            _telemetry.CompleteCall(activity, operationName, response.StatusCode, attempt - 1, Elapsed(started), false);
            LogFailed(operationName, attempt, response.StatusCode);
            throw MapError(request, response);
        }
    }

    /// <summary>
    /// Sends a request whose success response is streamed rather than buffered.
    /// </summary>
    /// <remarks>
    /// Used by the two binary operations. Streaming is never retried once the
    /// body has started arriving, because a partially consumed stream cannot be
    /// resumed and re-issuing the request would duplicate a download.
    /// </remarks>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Honoured through every await.</param>
    internal async Task<Stream> SendForStreamAsync(HindsightRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // PathTemplate already carries the method, so adding it again produced
        // "GET GET /v1/..." - and this string is the span name and the metric tag,
        // so the defect reached every telemetry surface.
        var operationName = request.PathTemplate;
        using var activity = _telemetry.StartCall(operationName, request.Method.Method);
        var started = TimeProvider.GetTimestamp();

        var response = await _transport
            .SendAsync(request, streamResponse: true, cancellationToken)
            .ConfigureAwait(false);

        if (!IsSuccess(response.StatusCode))
        {
            // The failure body is small and was read for diagnosis; the transport
            // buffered it because a non-success response is not streamed.
            _telemetry.CompleteCall(activity, operationName, response.StatusCode, 0, Elapsed(started), false);
            throw MapError(request, response);
        }

        _telemetry.CompleteCall(activity, operationName, response.StatusCode, 0, Elapsed(started), true);
        return response.Stream ?? Stream.Null;
    }

    /// <summary>
    /// Sends a request whose success has no body to deserialize.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Honoured through every await.</param>
    internal async Task SendVoidAsync(HindsightRequest request, CancellationToken cancellationToken)
    {
        await SendAsync<Unit>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request and returns the raw body, for the declared-empty responses.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Honoured through every await.</param>
    internal async Task<JsonElement> SendForJsonAsync(HindsightRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // PathTemplate already carries the method, so adding it again produced
        // "GET GET /v1/..." - and this string is the span name and the metric tag,
        // so the defect reached every telemetry surface.
        var operationName = request.PathTemplate;
        using var activity = _telemetry.StartCall(operationName, request.Method.Method);
        var started = TimeProvider.GetTimestamp();

        var attempt = 0;
        while (true)
        {
            attempt++;
            HindsightResponse response;

            try
            {
                response = await _transport
                    .SendAsync(request, streamResponse: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HindsightConnectionException)
            {
                if (CanRetry(request, attempt))
                {
                    await DelayBeforeRetryAsync(operationName, attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _telemetry.CompleteCall(activity, operationName, null, attempt - 1, Elapsed(started), false);
                throw;
            }

            if (IsSuccess(response.StatusCode))
            {
                _telemetry.CompleteCall(activity, operationName, response.StatusCode, attempt - 1, Elapsed(started), true);
                if (response.Body is null || response.Body.Length == 0)
                {
                    // A declared-empty success body is legitimate: several operations
                    // document 200 with no schema. An explicit Undefined value keeps
                    // "no body" distinguishable from "body present and null" without
                    // inventing a shape the API does not define.
                    return default;
                }

                return JsonDocument.Parse(response.Body).RootElement.Clone();
            }

            var retryAfter = HindsightRetryPolicy.ReadRetryAfter(response.Headers);
            if (CanRetry(request, attempt) && HindsightRetryPolicy.IsRetryableStatus(response.StatusCode))
            {
                await DelayBeforeRetryAsync(operationName, attempt, retryAfter, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            _telemetry.CompleteCall(activity, operationName, response.StatusCode, attempt - 1, Elapsed(started), false);
            throw MapError(request, response);
        }
    }

    private bool CanRetry(HindsightRequest request, int attempt) =>
        request.IsIdempotent && attempt < _options.MaxAttempts;

    private async Task DelayBeforeRetryAsync(
        string operationName, int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var delay = HindsightRetryPolicy.ComputeDelay(attempt, retryAfter, _options, Jitter);

        _telemetry.RetryScheduled(operationName, attempt, delay);

        // Task.Delay with the TimeProvider and the caller's token: the wait is
        // cancellable, so a caller that gives up does not sit out a backoff.
        await Task.Delay(delay, TimeProvider, cancellationToken).ConfigureAwait(false);
    }

    private TResponse Deserialize<TResponse>(HindsightResponse response, string operationName)
    {
        // A void-shaped success: nothing to read, and nothing to invent.
        if (typeof(TResponse) == typeof(Unit))
        {
            return (TResponse)(object)Unit.Value;
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            // A success that promised a body and sent none is a protocol
            // violation, not a validation failure: retrying would not help.
            throw new HindsightProtocolException(
                $"{operationName} returned {response.StatusCode} with an empty body, but the operation declares a response shape.",
                operationName);
        }

        try
        {
            var result = JsonSerializer.Deserialize(response.Body, _json.Info<TResponse>());
            if (result is null)
            {
                throw new HindsightProtocolException(
                    $"{operationName} returned {response.StatusCode} with a body that deserialized to null.",
                    operationName);
            }

            return result;
        }
        catch (JsonException ex)
        {
            LogProtocolViolation(operationName, ex);
            throw new HindsightProtocolException(
                $"{operationName} returned {response.StatusCode} with a body that could not be parsed into "
                + $"{typeof(TResponse).Name}.",
                operationName,
                ex);
        }
    }

    /// <summary>The elapsed time since a start timestamp.</summary>
    private TimeSpan Elapsed(long startedTimestamp) => TimeProvider.GetElapsedTime(startedTimestamp);

    private ILogger? Logger => _telemetry.Logger;

    private void LogUnreachable(string operationName, string reason)
    {
        if (Logger is { } logger)
        {
            HindsightLog.CallUnreachable(logger, operationName, reason);
        }
    }

    private void LogSucceeded(string operationName, int attempt)
    {
        if (Logger is { } logger)
        {
            HindsightLog.CallSucceeded(logger, operationName, attempt);
        }
    }

    private void LogFailed(string operationName, int attempt, int statusCode)
    {
        if (Logger is { } logger)
        {
            HindsightLog.CallFailed(logger, operationName, attempt, statusCode);
        }
    }

    private void LogProtocolViolation(string operationName, Exception exception)
    {
        if (Logger is { } logger)
        {
            HindsightLog.ProtocolViolation(logger, operationName, exception);
        }
    }

    private static bool IsSuccess(int statusCode) => statusCode is >= 200 and <= 299;

    /// <summary>Maps a non-success response onto a typed exception.</summary>
    internal static HindsightApiException MapError(HindsightRequest request, HindsightResponse response)
    {
        // PathTemplate already carries the method, which is what makes it readable
        // in a log without the caller having to reconstruct it. Prepending the method
        // again produced "GET GET /v1/...".
        var endpoint = request.PathTemplate;
        var body = response.Body is null ? null : System.Text.Encoding.UTF8.GetString(response.Body);
        var detail = ExtractDetail(body);

        var message = detail is null
            ? $"{endpoint} failed with {response.StatusCode}."
            : $"{endpoint} failed with {response.StatusCode}: {detail}";

        return response.StatusCode switch
        {
            400 => new HindsightBadRequestException(message, endpoint, body),
            401 => new HindsightAuthenticationException(message, 401, endpoint, body),
            403 => new HindsightPermissionDeniedException(message, endpoint, body),
            404 => new HindsightNotFoundException(message, endpoint, body),
            409 => new HindsightConflictException(message, endpoint, body),
            422 => new HindsightRequestValidationException(
                message, endpoint, body, ParseValidationErrors(body)),
            429 => new HindsightRateLimitException(
                message, endpoint, body, HindsightRetryPolicy.ReadRetryAfter(response.Headers)),
            >= 500 and <= 599 => new HindsightServerException(message, response.StatusCode, endpoint, body),

            // An unmapped status - 405 and 3xx redirects are both real and declared
            // nowhere - keeps its true status rather than being reported as a 400.
            _ => new HindsightUnexpectedStatusException(message, response.StatusCode, endpoint, body),
        };
    }

    /// <summary>
    /// Extracts a readable detail from a body.
    /// </summary>
    /// <remarks>
    /// <c>detail</c> is a string on a 404 and an array on a 422 — the same key,
    /// two different types, observed on a live server. Both are handled, and
    /// anything else falls through rather than throwing: an error path that
    /// itself throws is worse than one that reports a slightly less pretty
    /// message.
    /// </remarks>
    private static string? ExtractDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("detail", out var detail))
            {
                return null;
            }

            return detail.ValueKind switch
            {
                JsonValueKind.String => detail.GetString(),
                JsonValueKind.Array => string.Join("; ", detail.EnumerateArray().Select(DescribeValidationError)),
                _ => detail.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeValidationError(JsonElement element)
    {
        var message = element.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String
            ? msg.GetString()
            : null;
        var location = element.TryGetProperty("loc", out var loc)
            ? string.Join('.', loc.EnumerateArray().Select(segment => segment.GetRawText().Trim('"')))
            : null;

        return (location, message) switch
        {
            ({ } l, { } m) => $"{l}: {m}",
            (null, { } m) => m,
            ({ } l, null) => l,
            _ => element.GetRawText(),
        };
    }

    /// <summary>Parses the 422 <c>detail</c> array into typed errors.</summary>
    internal static IReadOnlyList<ValidationErrorDetail>? ParseValidationErrors(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("detail", out var detail) ||
                detail.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var errors = new List<ValidationErrorDetail>();
            foreach (var element in detail.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                errors.Add(new ValidationErrorDetail
                {
                    Location = element.TryGetProperty("loc", out var loc) ? loc.Clone() : default,
                    Message = element.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String
                        ? msg.GetString()
                        : null,
                    ErrorType = element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                        ? type.GetString()
                        : null,
                    Input = element.TryGetProperty("input", out var input) ? input.Clone() : null,
                });
            }

            return errors.Count == 0 ? null : errors;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A stand-in for a success response with no body.
    /// </summary>
    /// <remarks>
    /// A private marker rather than <c>bool</c>, so a void call cannot be confused
    /// with an operation that genuinely returns a boolean.
    /// </remarks>
    internal readonly struct Unit
    {
        internal static Unit Value => default;
    }
}
