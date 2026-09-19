using System.Net;
using System.Text;
using Vectorize.Hindsight.Sdk.Authentication;
using Vectorize.Hindsight.Sdk.Configuration;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Http;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// The real HTTP transport, exercised through a stub <see cref="HttpMessageHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline tests use a stub of the library's own seam, which proves the
/// client's logic but never touches <c>HttpClient</c>. These tests drive the actual
/// transport over a stub handler, so the code that builds a URI, sets headers,
/// converts a timeout and reads a body is executed rather than only compiled.
/// </para>
/// <para>
/// No socket is opened. The handler returns scripted responses, so the tests are
/// fast and need no server.
/// </para>
/// </remarks>
public sealed class HttpTransportTests
{
    private static (HttpHindsightTransport Transport, StubHttpMessageHandler Handler) Create(
        Action<HindsightClientOptions>? configure = null)
    {
        var handler = new StubHttpMessageHandler();
        var options = new HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            HttpClient = new HttpClient(handler),
        };
        configure?.Invoke(options);

        return (new HttpHindsightTransport(options), handler);
    }

    private static HindsightRequest Get(string path, IReadOnlyList<KeyValuePair<string, string>>? query = null) =>
        new()
        {
            Method = HttpMethod.Get,
            Path = path,
            PathTemplate = $"GET {path}",
            Query = query ?? [],
        };

    [Fact]
    public async Task A_request_is_sent_and_the_body_is_returned()
    {
        var (transport, handler) = Create();
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, """{"ok":true}""");

            var response = await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            Assert.Equal(200, response.StatusCode);
            Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(response.Body!));
            Assert.Single(handler.Requests);
        }
    }

    [Fact]
    public async Task The_base_address_and_path_are_combined_into_the_uri()
    {
        var (transport, handler) = Create(o => o.BaseAddress = "https://example.com/");
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            // A trailing slash on the base address must not double up.
            Assert.Equal("https://example.com/v1/default/banks", handler.Requests[0].RequestUri!.ToString());
        }
    }

    [Fact]
    public async Task Query_parameters_are_appended_and_encoded()
    {
        var (transport, handler) = Create();
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            await transport.SendAsync(
                Get("/v1/default/banks", [new("q", "a b&c"), new("limit", "10")]),
                false,
                CancellationToken.None);

            // The Uri class renders a space as a literal once the string is built, so
            // each parameter is asserted on its own rather than the whole query being
            // compared as one string.
            var query = handler.Requests[0].RequestUri!.Query.TrimStart('?');
            var parameters = query.Split('&');

            Assert.Equal(2, parameters.Length);
            Assert.Equal("q=a b&c", Uri.UnescapeDataString(parameters[0]));
            Assert.Equal("limit=10", parameters[1]);
        }
    }

    [Fact]
    public async Task A_get_carries_no_body_at_all()
    {
        // A null body assigned to a nullable slot would become an empty one, putting a
        // zero-length body on the wire where the request should have none.
        var (transport, handler) = Create();
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            Assert.Null(handler.Requests[0].Content);
            Assert.Null(handler.Bodies[0]);
        }
    }

    [Fact]
    public async Task A_json_body_is_sent_with_its_content_type()
    {
        var (transport, handler) = Create();
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            var request = new HindsightRequest
            {
                Method = HttpMethod.Post,
                Path = "/v1/default/banks/b/memories",
                PathTemplate = "POST /v1/default/banks/{bank_id}/memories",
                Body = Encoding.UTF8.GetBytes("""{"items":[]}"""),
                ContentType = "application/json",
            };

            await transport.SendAsync(request, false, CancellationToken.None);

            Assert.Equal("""{"items":[]}""", handler.Bodies[0]);
            Assert.Equal("application/json", handler.Requests[0].Content!.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task An_api_key_is_sent_as_a_bearer_token()
    {
        var (transport, handler) = Create(o => o.ApiKey = "secret-key");
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            var auth = handler.Requests[0].Headers.Authorization;
            Assert.NotNull(auth);
            Assert.Equal("Bearer", auth!.Scheme);
            Assert.Equal("secret-key", auth.Parameter);
        }
    }

    [Fact]
    public async Task No_authorization_header_is_sent_when_there_is_no_key()
    {
        // The specification marks authorization optional, so a keyless deployment must
        // send no header rather than an empty one.
        var (transport, handler) = Create();
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            Assert.Null(handler.Requests[0].Headers.Authorization);
        }
    }

    [Fact]
    public async Task An_async_key_provider_is_consulted()
    {
        var provider = new StubApiKeyProvider("from-provider");
        var (transport, handler) = Create(o => o.ApiKeyProvider = provider);
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            Assert.True(provider.CallCount > 0);
            Assert.Equal("from-provider", handler.Requests[0].Headers.Authorization!.Parameter);
        }
    }

    [Fact]
    public async Task Caller_headers_are_applied_and_can_override_a_default()
    {
        var (transport, handler) = Create(o =>
        {
            o.Headers["X-Correlation"] = "abc";
            o.Headers["Accept"] = "application/vnd.custom+json";
        });
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            Assert.Equal("abc", Assert.Single(handler.Requests[0].Headers.GetValues("X-Correlation")));
        }
    }

    [Fact]
    public async Task A_user_agent_is_sent_and_identifies_the_library()
    {
        var (transport, handler) = Create();
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            var agent = string.Join(" ", handler.Requests[0].Headers.GetValues("User-Agent"));
            Assert.Contains("vectorize-hindsight-dotnet", agent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_invalid_user_agent_does_not_stop_the_call()
    {
        // A malformed User-Agent is the caller's mistake, but failing the call over it
        // would be a worse outcome than sending it unvalidated.
        var (transport, handler) = Create(o => o.UserAgent = "bad\u0001agent");
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            var response = await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            Assert.Equal(200, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_non_success_response_body_is_still_read_for_diagnosis()
    {
        var (transport, handler) = Create();
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.NotFound, """{"detail":"Bank 'x' not found"}""");

            var response = await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            Assert.Equal(404, response.StatusCode);
            Assert.Contains("not found", Encoding.UTF8.GetString(response.Body!), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Response_headers_are_surfaced()
    {
        var (transport, handler) = Create();
        using (transport)
        {
            handler.Enqueue(
                HttpStatusCode.TooManyRequests,
                new StringContent("{}", Encoding.UTF8, "application/json"),
                responseHeaders: new Dictionary<string, string> { ["Retry-After"] = "7" });

            var response = await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            Assert.Equal("7", response.Headers["Retry-After"]);
        }
    }

    [Fact]
    public async Task A_streamed_response_is_returned_unbuffered()
    {
        var (transport, handler) = Create();
        using (transport)
        {
            handler.Enqueue(
                HttpStatusCode.OK,
                new ByteArrayContent([1, 2, 3, 4]),
                "application/octet-stream");

            var response = await transport.SendAsync(Get("/v1/default/files/download/k"), true, CancellationToken.None);

            Assert.Null(response.Body);
            Assert.NotNull(response.Stream);
            using var reader = new MemoryStream();
            await response.Stream!.CopyToAsync(reader, CancellationToken.None);
            Assert.Equal(4, reader.Length);
        }
    }

    [Fact]
    public async Task A_transport_failure_becomes_a_connection_exception()
    {
        var (transport, handler) = Create();
        using (transport)
        {
            handler.EnqueueThrow(new HttpRequestException("connection refused"));

            var exception = await Assert.ThrowsAsync<HindsightConnectionException>(
                () => transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None));

            Assert.False(exception.WasTimeout);
            Assert.IsType<HttpRequestException>(exception.InnerException);
        }
    }

    [Fact]
    public async Task A_per_attempt_timeout_is_reported_as_a_timeout_not_a_cancellation()
    {
        // The two must stay distinguishable: a caller that cancelled must not be told it
        // hit a network failure, and vice versa.
        var (transport, handler) = Create(o => o.Timeout = TimeSpan.FromMilliseconds(50));
        using (transport)
        {
            handler.EnqueueDelay(TimeSpan.FromSeconds(5));

            var exception = await Assert.ThrowsAsync<HindsightConnectionException>(
                () => transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None));

            Assert.True(exception.WasTimeout);
            Assert.Contains("per-attempt timeout", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_a_cancellation()
    {
        var (transport, handler) = Create(o => o.Timeout = TimeSpan.FromSeconds(30));
        using (transport)
        {
            handler.EnqueueDelay(TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => transport.SendAsync(Get("/v1/default/banks"), false, cts.Token));
        }
    }

    [Fact]
    public async Task A_zero_timeout_means_no_per_attempt_deadline()
    {
        // Zero must mean "no deadline", not "a token that cancels immediately and fails
        // every call".
        var (transport, handler) = Create(o => o.Timeout = TimeSpan.Zero);
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, "{}");

            var response = await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

            Assert.Equal(200, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_caller_supplied_http_client_already_used_is_accepted()
    {
        // HttpClient.Timeout cannot be assigned once a client has served a request, and
        // a pooled or factory-managed client has often already served one. The transport
        // must not touch that property.
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.OK, "{}");
        var shared = new HttpClient(handler);
        await shared.GetAsync("http://localhost:8888/warmup", CancellationToken.None);

        var options = new HindsightClientOptions { BaseAddress = "http://localhost:8888", HttpClient = shared };
        using var transport = new HttpHindsightTransport(options);

        handler.EnqueueJson(HttpStatusCode.OK, "{}");
        var response = await transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Disposing_the_transport_leaves_a_caller_supplied_client_alone()
    {
        var handler = new StubHttpMessageHandler();
        var shared = new HttpClient(handler);
        var options = new HindsightClientOptions { BaseAddress = "http://localhost:8888", HttpClient = shared };

        var transport = new HttpHindsightTransport(options);
        transport.Dispose();

        // Still usable: disposing it would break a DI-managed client the caller shares.
        handler.EnqueueJson(HttpStatusCode.OK, "{}");
        var response = await shared.GetAsync("http://localhost:8888/x", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Disposing_an_owned_transport_is_idempotent()
    {
        var (transport, _) = Create();
        transport.Dispose();
        transport.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transport.SendAsync(Get("/v1/default/banks"), false, CancellationToken.None));
    }

    [Fact]
    public async Task A_multipart_payload_is_sent_as_form_data()
    {
        var (transport, handler) = Create();
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, """{"success":true}""");

            var request = new HindsightRequest
            {
                Method = HttpMethod.Post,
                Path = "/v1/default/banks/b/files/retain",
                PathTemplate = "POST /v1/default/banks/{bank_id}/files/retain",
                Multipart = new MultipartPayload
                {
                    Parts =
                    [
                        new MultipartPart
                        {
                            Name = "request",
                            Content = Encoding.UTF8.GetBytes("{}"),
                            ContentType = "text/plain",
                        },
                        new MultipartPart
                        {
                            Name = "files",
                            FileName = "note.txt",
                            Content = Encoding.UTF8.GetBytes("hello"),
                            ContentType = "text/plain",
                        },
                    ],
                },
            };

            var response = await transport.SendAsync(request, false, CancellationToken.None);

            Assert.Equal(200, response.StatusCode);

            // The body is read by the handler, which is what materialises the multipart
            // content, so the assertion is on the recorded bytes. The filename is what
            // the server uses to pick a parser, so its presence is the point of this
            // assertion - it was silently absent before the adapter passed it through
            // the Add overload that emits Content-Disposition.
            var body = handler.Bodies[0] ?? string.Empty;
            Assert.Contains("name=request", body, StringComparison.Ordinal);
            Assert.Contains("name=files", body, StringComparison.Ordinal);
            Assert.Contains("filename=note.txt", body, StringComparison.Ordinal);
            Assert.Contains("hello", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_stream_backed_multipart_part_is_sent_by_filename()
    {
        var (transport, handler) = Create();
        using (transport)
        {
            handler.EnqueueJson(HttpStatusCode.OK, """{"success":true}""");

            var request = new HindsightRequest
            {
                Method = HttpMethod.Post,
                Path = "/v1/default/banks/b/transfer/import",
                PathTemplate = "POST /v1/default/banks/{bank_id}/transfer/import",
                Multipart = new MultipartPayload
                {
                    Parts =
                    [
                        MultipartPart.FromStream(
                            "file",
                            "bank.zip",
                            _ => Task.FromResult<Stream>(new MemoryStream([9, 9, 9])),
                            "application/zip"),
                    ],
                },
            };

            var response = await transport.SendAsync(request, false, CancellationToken.None);

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("bank.zip", handler.Bodies[0], StringComparison.Ordinal);
        }
    }
}
