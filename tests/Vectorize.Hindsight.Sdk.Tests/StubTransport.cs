using System.Net;
using System.Text;
using Vectorize.Hindsight.Sdk.Http;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// A hand-written stub of <see cref="IHindsightTransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// No mocking framework. A stub of the library's own seam is smaller than a mock
/// setup, survives refactors that would break a setup string, and needs no
/// dependency the library itself does not have.
/// </para>
/// <para>
/// Every request is recorded, so a test can assert what was actually sent — the
/// path, the query, the bytes — rather than only what came back.
/// </para>
/// </remarks>
internal sealed class StubTransport : IHindsightTransport
{
    private readonly Queue<Func<HindsightRequest, HindsightResponse>> _responses = new();

    /// <summary>Every request this transport was asked to send, in order.</summary>
    internal List<HindsightRequest> Requests { get; } = [];

    /// <summary>The bodies sent, decoded as UTF-8, in order.</summary>
    internal List<string?> RequestBodies { get; } = [];

    /// <summary>How many times <see cref="SendAsync"/> was entered.</summary>
    internal int SendCount { get; private set; }

    /// <summary>Disposal is observed rather than assumed.</summary>
    internal bool Disposed { get; private set; }

    /// <summary>The last stream handed to a caller, for asserting it was returned.</summary>
    internal Stream? StreamToReturn { get; set; }

    /// <summary>Queues a JSON response.</summary>
    /// <param name="statusCode">The status.</param>
    /// <param name="json">The body.</param>
    /// <param name="headers">Optional headers.</param>
    internal StubTransport EnqueueJson(int statusCode, string json, IDictionary<string, string>? headers = null) =>
        EnqueueResponse(_ => new HindsightResponse
        {
            StatusCode = statusCode,
            Body = Encoding.UTF8.GetBytes(json),
            Headers = headers is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            ContentType = "application/json",
        });

    /// <summary>Queues an empty response.</summary>
    /// <param name="statusCode">The status.</param>
    internal StubTransport EnqueueEmpty(int statusCode) =>
        EnqueueResponse(_ => new HindsightResponse
        {
            StatusCode = statusCode,
            Body = null,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        });

    /// <summary>Queues a raw-bytes response.</summary>
    /// <param name="statusCode">The status.</param>
    /// <param name="bytes">The body.</param>
    /// <param name="contentType">The content type.</param>
    internal StubTransport EnqueueBytes(int statusCode, byte[] bytes, string contentType = "application/octet-stream") =>
        EnqueueResponse(_ => new HindsightResponse
        {
            StatusCode = statusCode,
            Body = bytes,
            ContentType = contentType,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        });

    /// <summary>Queues a streamed response, for the binary operations.</summary>
    /// <param name="statusCode">The status.</param>
    internal StubTransport EnqueueStream(int statusCode) =>
        EnqueueResponse(_ => new HindsightResponse
        {
            StatusCode = statusCode,
            Stream = StreamToReturn ?? new MemoryStream([1, 2, 3]),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        });

    /// <summary>Queues a transport-level failure.</summary>
    /// <param name="exception">The exception to throw.</param>
    internal StubTransport EnqueueThrow(Exception exception) =>
        EnqueueResponse(_ => throw exception);

    /// <summary>Queues an arbitrary response factory.</summary>
    /// <param name="factory">Produces the response, or throws.</param>
    internal StubTransport EnqueueResponse(Func<HindsightRequest, HindsightResponse> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    /// <inheritdoc />
    public Task<HindsightResponse> SendAsync(
        HindsightRequest request, bool streamResponse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SendCount++;
        Requests.Add(request);
        RequestBodies.Add(request.Body is null ? null : Encoding.UTF8.GetString(request.Body.Value.Span));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"The stub transport has no queued response for request {SendCount} ({request.Method} {request.Path}).");
        }

        var factory = _responses.Dequeue();
        return Task.FromResult(factory(request));
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}

/// <summary>
/// A stub of <see cref="IApiKeyProvider"/> that records how often it was asked.
/// </summary>
internal sealed class StubApiKeyProvider : Authentication.IApiKeyProvider
{
    private readonly string? _apiKey;

    internal StubApiKeyProvider(string? apiKey) => _apiKey = apiKey;

    internal int CallCount { get; private set; }

    /// <inheritdoc />
    public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return new ValueTask<string?>(_apiKey);
    }
}

/// <summary>
/// A handler that records requests and returns scripted responses, so the real
/// HTTP transport is exercised without a socket.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    /// <summary>Every request the handler saw, in order.</summary>
    internal List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Every request body, read as text.</summary>
    internal List<string?> Bodies { get; } = [];

    /// <summary>Disposal is observed rather than assumed.</summary>
    internal bool Disposed { get; private set; }

    /// <summary>Queues a response.</summary>
    /// <param name="statusCode">The status.</param>
    /// <param name="content">The body content, or null for none.</param>
    /// <param name="contentType">The content type.</param>
    internal StubHttpMessageHandler Enqueue(
        HttpStatusCode statusCode, HttpContent? content = null, string contentType = "application/json")
    {
        _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(statusCode) { Content = content };
            if (content is not null && !string.IsNullOrEmpty(contentType))
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }

            return response;
        });
        return this;
    }

    /// <summary>Queues a JSON response.</summary>
    /// <param name="statusCode">The status.</param>
    /// <param name="json">The body.</param>
    internal StubHttpMessageHandler EnqueueJson(HttpStatusCode statusCode, string json) =>
        Enqueue(statusCode, new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>Queues a response that delays before returning, to exercise timeouts.</summary>
    /// <param name="delay">How long to stall.</param>
    internal StubHttpMessageHandler EnqueueDelay(TimeSpan delay)
    {
        _responses.Enqueue(_ => throw new TaskCanceledException("simulated stall"));
        Delay = delay;
        return this;
    }

    /// <summary>An artificial stall applied before the next response.</summary>
    internal TimeSpan Delay { get; set; }

    /// <summary>Queues a failure.</summary>
    /// <param name="exception">The exception to throw.</param>
    internal StubHttpMessageHandler EnqueueThrow(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Bodies.Add(body);
        }
        else
        {
            Bodies.Add(null);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            Delay = TimeSpan.Zero;
        }

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"The stub handler has no queued response for request {Requests.Count}.");
        }

        return _responses.Dequeue()(request);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> whose delays are immediate, so retry timing can
/// be asserted without the test suite waiting.
/// </summary>
internal sealed class ImmediateTimeProvider : TimeProvider
{
    private long _ticks = TimeProvider.System.GetTimestamp();

    /// <summary>Every delay that was requested, in order.</summary>
    internal List<TimeSpan> Delays { get; } = [];

    /// <inheritdoc />
    public override long GetTimestamp() => _ticks;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Delays.Add(dueTime);
        callback(state);
        return new NullTimer();
    }

    /// <summary>Advances the recorded clock, so elapsed-time assertions are meaningful.</summary>
    /// <param name="delta">How far to advance.</param>
    internal void Advance(TimeSpan delta) => _ticks += (long)(delta.TotalSeconds * TimestampFrequency);

    private sealed class NullTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
