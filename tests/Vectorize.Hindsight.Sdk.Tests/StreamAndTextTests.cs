using System.Net;
using System.Text;
using System.Text.Json;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Http;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// The remaining paths: the streamed-download route, the text route, and the
/// argument-validation helpers.
/// </summary>
/// <remarks>
/// A stream is a <see cref="Stream"/>, so a caller will use it the way they use any
/// other: seek it, read it synchronously, read it as a <c>Memory</c>, copy it,
/// flush it, or wrap it in a reader. Every one of those members has to delegate to
/// the underlying stream correctly, and a wrapper that only implemented the
/// asynchronous read would throw on the first synchronous use.
/// </remarks>
public sealed class StreamAndTextTests
{
    private static (HindsightClient Client, StubTransport Transport) Create(
        Action<Configuration.HindsightClientOptions>? configure = null)
    {
        var transport = new StubTransport();
        var options = new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        };
        configure?.Invoke(options);

        var client = new HindsightClient(options)
        {
            TimeProvider = new ImmediateTimeProvider(),
            Jitter = () => 0.0,
        };
        return (client, transport);
    }

    // --- Binary downloads through the pipeline -----------------------------

    [Fact]
    public async Task A_download_returns_the_stream_the_transport_produced()
    {
        var payload = new MemoryStream(Encoding.UTF8.GetBytes("archive-bytes"));
        var (client, transport) = Create();
        using (client)
        {
            transport.StreamToReturn = payload;
            transport.EnqueueStream(200);

            var stream = await client.DownloadFileAsync("some-key", CancellationToken.None);

            Assert.Same(payload, stream);
            using var reader = new StreamReader(stream);
            Assert.Equal("archive-bytes", await reader.ReadToEndAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_download_that_fails_is_mapped_rather_than_returned()
    {
        // A failed download must raise the typed error; handing back an error body as a
        // stream would let a caller write an HTML error page to disk as an archive.
        var (client, transport) = Create();
        using (client)
        {
            transport.EnqueueJson(404, """{"detail":"no such file"}""");

            var exception = await Assert.ThrowsAsync<HindsightNotFoundException>(
                () => client.DownloadFileAsync("missing", CancellationToken.None));

            Assert.Equal(404, exception.StatusCode);
        }
    }

    [Fact]
    public async Task A_download_that_returns_nothing_yields_an_empty_stream_rather_than_null()
    {
        var (client, _) = Create();
        using (client)
        {
            // No Stream set on the response: the contract is a stream, so it degrades to
            // an empty one rather than a null a caller would dereference.
            var (client2, transport2) = Create();
            using (client2)
            {
                transport2.EnqueueEmpty(200);

                var stream = await client2.DownloadFileAsync("k", CancellationToken.None);

                Assert.NotNull(stream);
                Assert.Equal(0, stream.Length);
            }
        }
    }

    [Fact]
    public async Task An_attachment_download_also_streams()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.StreamToReturn = new MemoryStream([1, 2, 3]);
            transport.EnqueueStream(200);

            var stream = await client.GetBankAttachmentAsync("b", "a", CancellationToken.None);

            Assert.NotNull(stream);
        }
    }

    // --- The text route ----------------------------------------------------

    [Fact]
    public async Task A_non_json_success_body_is_readable_as_text()
    {
        // /metrics declares no content type and returns the Prometheus exposition
        // format. Parsing that as JSON threw before the content type was consulted, so
        // this is the regression guard for that defect.
        var (client, transport) = Create();
        using (client)
        {
            var payload = "# HELP hindsight_requests Total\nhindsight_requests 42\n";
            transport.EnqueueResponse(_ => new HindsightResponse
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes(payload),
                ContentType = "text/plain; charset=utf-8",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            });

            var result = await client.MetricsEndpointMetricsGetAsync(CancellationToken.None);

            Assert.Equal(JsonValueKind.String, result.ValueKind);
            Assert.Contains("hindsight_requests 42", result.GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_json_success_body_is_still_parsed_as_json()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.EnqueueJson(200, """{"value":1}""");

            var result = await client.GetBankTemplateSchemaAsync(CancellationToken.None);

            Assert.Equal(JsonValueKind.Object, result.ValueKind);
            Assert.Equal(1, result.GetProperty("value").GetInt32());
        }
    }

    [Fact]
    public async Task A_missing_content_type_is_treated_as_json()
    {
        // A server that omits the header on a JSON endpoint is common, and a parse
        // failure is a clearer signal than silently reading it as text.
        var (client, transport) = Create();
        using (client)
        {
            transport.EnqueueResponse(_ => new HindsightResponse
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes("""{"value":2}"""),
                ContentType = null,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            });

            var result = await client.GetBankTemplateSchemaAsync(CancellationToken.None);

            Assert.Equal(JsonValueKind.Object, result.ValueKind);
        }
    }

    [Fact]
    public async Task An_empty_body_on_a_declared_empty_success_is_not_an_error()
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.EnqueueEmpty(200);

            var result = await client.GetBankTemplateSchemaAsync(CancellationToken.None);

            Assert.Equal(JsonValueKind.Undefined, result.ValueKind);
        }
    }

    // --- Owned stream ------------------------------------------------------

    [Fact]
    public async Task The_owned_stream_wraps_a_real_http_response_and_releases_it_on_dispose()
    {
        // A streamed download against the REAL transport: the caller reads the archive
        // and disposing the stream releases the response. Before the ownership transfer
        // this threw ObjectDisposedException.
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, new ByteArrayContent(Encoding.UTF8.GetBytes("zipdata")), "application/zip");

        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            HttpClient = new HttpClient(handler),
        });

        var stream = await client.DownloadFileAsync("k", CancellationToken.None);

        using (stream)
        {
            Assert.True(stream.CanRead);
            using var reader = new StreamReader(stream);
            Assert.Equal("zipdata", await reader.ReadToEndAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task An_owned_stream_supports_the_members_a_normal_stream_does()
    {
        // A caller holding a Stream will reach for these, and a wrapper that only
        // implemented the async read would throw on the first synchronous use.
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, new ByteArrayContent([1, 2, 3, 4]), "application/octet-stream");

        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            HttpClient = new HttpClient(handler),
        });

        var stream = await client.DownloadFileAsync("k", CancellationToken.None);

        using (stream)
        {
            Assert.True(stream.CanSeek);
            Assert.Equal(4, stream.Length);

            var buffer = new byte[2];
            var read = stream.Read(buffer, 0, 2);
            Assert.Equal(2, read);

            stream.Position = 0;
            Assert.Equal(0, stream.Position);

            stream.Seek(1, SeekOrigin.Begin);
            Assert.Equal(1, stream.Position);

            var memory = new byte[3];
            var memoryRead = await stream.ReadAsync(memory.AsMemory(), CancellationToken.None);
            Assert.True(memoryRead > 0);

            stream.Position = 0;
            using var destination = new MemoryStream();
            await stream.CopyToAsync(destination, CancellationToken.None);
            Assert.Equal(4, destination.Length);
        }
    }

    [Fact]
    public async Task An_owned_stream_disposes_idempotently()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, new ByteArrayContent([1]), "application/octet-stream");

        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            HttpClient = new HttpClient(handler),
        });

        var stream = await client.DownloadFileAsync("k", CancellationToken.None);
        stream.Dispose();
        stream.Dispose();
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task An_owned_stream_disposes_asynchronously()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, new ByteArrayContent([1, 2]), "application/octet-stream");

        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            HttpClient = new HttpClient(handler),
        });

        await using var stream = await client.DownloadFileAsync("k", CancellationToken.None);

        var buffer = new byte[2];
        var read = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None);
        Assert.True(read > 0);
    }

    // --- Body serialization ------------------------------------------------

    [Fact]
    public void A_typed_body_serializes_through_the_source_generated_context()
    {
        var (client, _) = Create();
        using (client)
        {
            var body = new RetainRequest
            {
                Items = [new MemoryItem { Content = HindsightMemoryContent.FromText("hello") }],
                Async = true,
            };

            var bytes = client.SerializeBody(body);

            var json = Encoding.UTF8.GetString(bytes.Span);
            Assert.Contains("\"items\"", json, StringComparison.Ordinal);
            Assert.Contains("\"async\":true", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_null_typed_body_is_rejected_rather_than_serialized()
    {
        // A null body is expressed by omitting it, not by serializing "null" into the
        // request. Reaching this helper with null is a programming error.
        var (client, _) = Create();
        using (client)
        {
            Assert.Throws<ArgumentNullException>(() => client.SerializeBody<RetainRequest>(null!));
        }
    }

    [Fact]
    public void A_raw_json_body_is_returned_as_an_element_and_an_empty_one_as_undefined()
    {
        // The helper that reads a declared-empty success must distinguish "no body"
        // from "a body", because the first is legitimate for several operations.
        var element = HindsightClient.ReadJsonElement("""{"a":1}"""u8.ToArray());

        Assert.Equal(JsonValueKind.Object, element.ValueKind);

        Assert.Equal(JsonValueKind.Undefined, HindsightClient.ReadJsonElement(null).ValueKind);
        Assert.Equal(JsonValueKind.Undefined, HindsightClient.ReadJsonElement([]).ValueKind);
    }
}
