using System.Text;
using System.Text.Json;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// The hand-written upload operations, including their validation and the shape of
/// the multipart body they build.
/// </summary>
/// <remarks>
/// These three are hand-written because a file upload's parameters are streams and
/// filenames rather than a JSON body, so they carry their own validation and are not
/// covered by the generated-operation suite. Their validation runs before any
/// request, which is what a caller wants: a missing filename must fail at the call
/// site rather than at the server.
/// </remarks>
public sealed class UploadTests
{
    private static (HindsightClient Client, StubTransport Transport) Create()
    {
        var transport = new StubTransport().EnqueueJson(200, """{"success":true,"bank_id":"b"}""");
        var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Transport = transport,
        })
        {
            TimeProvider = new ImmediateTimeProvider(),
            Jitter = () => 0.0,
        };
        return (client, transport);
    }

    [Fact]
    public async Task File_retain_sends_a_multipart_request_with_the_files_and_request_fields()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.FileRetainAsync(
                "b",
                [HindsightFileUpload.FromBytes("note.txt", new byte[] { 104, 101, 108, 108, 111 })],
                new FileRetainRequest { Context = "a context" },
                CancellationToken.None);

            var request = transport.Requests[0];
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/default/banks/b/files/retain", request.Path);
            Assert.NotNull(request.Multipart);

            var names = request.Multipart!.Parts.Select(p => p.Name).ToList();
            Assert.Equal(["request", "files"], names);
        }
    }

    [Fact]
    public async Task File_retain_is_not_retried_because_it_writes_memories()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.FileRetainAsync(
                "b", [HindsightFileUpload.FromBytes("n.txt", new byte[] { 1 })], null, CancellationToken.None);

            Assert.False(transport.Requests[0].IsIdempotent);
        }
    }

    [Fact]
    public async Task File_retain_rejects_an_empty_file_list_before_sending()
    {
        var (client, transport) = Create();
        using (client)
        {
            var exception = await Assert.ThrowsAsync<HindsightValidationException>(
                () => client.FileRetainAsync("b", [], null, CancellationToken.None));

            Assert.Equal("files", exception.ParamName);
            Assert.Empty(transport.Requests);
        }
    }

    [Fact]
    public async Task File_retain_rejects_a_blank_bank_id()
    {
        var (client, transport) = Create();
        using (client)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.FileRetainAsync("  ", [HindsightFileUpload.FromBytes("n.txt", new byte[] { 1 })], null, CancellationToken.None));

            Assert.Empty(transport.Requests);
        }
    }

    [Fact]
    public async Task File_retain_rejects_a_file_with_no_filename()
    {
        // The server uses the filename to choose a parser, so an upload without one
        // cannot be served meaningfully.
        var (client, transport) = Create();
        using (client)
        {
            var nameless = HindsightFileUpload.FromBytes(string.Empty, new byte[] { 1 }, "text/plain");

            var exception = await Assert.ThrowsAsync<HindsightValidationException>(
                () => client.FileRetainAsync("b", [nameless], null, CancellationToken.None));

            Assert.Contains("filename", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(transport.Requests);
        }
    }

    [Fact]
    public async Task A_file_upload_carries_its_filename_and_content_type()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.FileRetainAsync(
                "b",
                [HindsightFileUpload.FromBytes("report.pdf", new byte[] { 1, 2 }, "application/pdf")],
                null,
                CancellationToken.None);

            var filePart = transport.Requests[0].Multipart!.Parts.Single(p => p.Name == "files");
            Assert.Equal("report.pdf", filePart.FileName);
            Assert.Equal("application/pdf", filePart.ContentType);
            Assert.Equal(2, filePart.Content!.Value.Length);
        }
    }

    [Fact]
    public async Task A_stream_backed_upload_exposes_a_factory_rather_than_eager_content()
    {
        // The upload carries a factory, not materialised bytes, so the bytes are not
        // held in memory by the caller. The factory is the only route by which this
        // library reaches the file system, and the library itself performs no I/O.
        var opened = 0;
        var (client, transport) = Create();
        using (client)
        {
            var upload = HindsightFileUpload.FromStream(
                "note.txt",
                _ =>
                {
                    opened++;
                    return Task.FromResult<Stream>(new MemoryStream("content"u8.ToArray()));
                },
                "text/plain");

            await client.FileRetainAsync("b", [upload], null, CancellationToken.None);

            // The multipart part carries the factory; it is not pre-read into a buffer.
            var part = transport.Requests[0].Multipart!.Parts.Single(p => p.Name == "files");
            Assert.Null(part.Content);
            Assert.NotNull(part.ContentSource);
            Assert.Equal("note.txt", part.FileName);
            Assert.Equal(0, opened);
            Assert.False(upload.Content.HasValue);
        }
    }

    [Fact]
    public async Task A_stream_backed_upload_is_opened_by_the_real_transport()
    {
        // The counterpart: against the real transport the factory IS invoked, because
        // the content has to be serialized to go on the wire. Together with the test
        // above this pins down that the factory is deferred past construction and
        // consumed exactly when the request is sent.
        var opened = 0;
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(System.Net.HttpStatusCode.OK, """{"success":true}""");

        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            HttpClient = new HttpClient(handler),
        });

        var upload = HindsightFileUpload.FromStream(
            "note.txt",
            _ =>
            {
                opened++;
                return Task.FromResult<Stream>(new MemoryStream("payload"u8.ToArray()));
            },
            "text/plain");

        await client.FileRetainAsync("b", [upload], null, CancellationToken.None);

        Assert.Equal(1, opened);
        Assert.Contains("payload", handler.Bodies[0] ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void An_upload_from_a_path_names_the_file_after_the_path()
    {
        var upload = HindsightFileUpload.FromPath("/tmp/some/report.pdf", "application/pdf");

        Assert.Equal("report.pdf", upload.FileName);
        Assert.Equal("application/pdf", upload.ContentType);
        Assert.NotNull(upload.ContentSource);
    }

    [Fact]
    public async Task An_upload_from_a_path_reads_the_real_file()
    {
        // The library performs no file I/O itself; the caller's factory does. This
        // proves the factory it hands back actually opens the file.
        var path = Path.Combine(Path.GetTempPath(), $"hindsight-upload-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "on disk", CancellationToken.None);

        try
        {
            var upload = HindsightFileUpload.FromPath(path);
            await using var stream = await upload.ContentSource!(CancellationToken.None);
            using var reader = new StreamReader(stream);

            Assert.Equal("on disk", await reader.ReadToEndAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_request_payload_round_trips_the_documented_options()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.FileRetainAsync(
                "b",
                [HindsightFileUpload.FromBytes("n.txt", new byte[] { 1 })],
                new FileRetainRequest
                {
                    Parser = ["iris", "markitdown"],
                    Context = "ctx",
                    Async = true,
                    FilesMetadata = [new FileRetainMetadata { FileName = "n.txt", Parser = ["iris"] }],
                },
                CancellationToken.None);

            // The `request` field is a JSON string, not a JSON part - that is what the
            // server declares.
            var requestPart = transport.Requests[0].Multipart!.Parts.Single(p => p.Name == "request");
            var json = Encoding.UTF8.GetString(requestPart.Content!.Value.Span);

            Assert.Contains("\"parser\":[\"iris\",\"markitdown\"]", json, StringComparison.Ordinal);
            Assert.Contains("\"context\":\"ctx\"", json, StringComparison.Ordinal);
            Assert.Contains("\"async\":true", json, StringComparison.Ordinal);
            Assert.Equal("text/plain", requestPart.ContentType);
        }
    }

    [Fact]
    public async Task An_unmodelled_upload_option_survives_the_round_trip()
    {
        // The escape hatch matters here: the request model is described in prose only,
        // so a caller targeting a newer server must be able to send an option this
        // version does not know.
        var (client, transport) = Create();
        using (client)
        {
            var request = new FileRetainRequest
            {
                AdditionalProperties = new Dictionary<string, JsonElement>
                {
                    ["future_option"] = JsonDocument.Parse("\"value\"").RootElement.Clone(),
                },
            };

            await client.FileRetainAsync("b", [HindsightFileUpload.FromBytes("n.txt", new byte[] { 1 })], request, CancellationToken.None);

            var requestPart = transport.Requests[0].Multipart!.Parts.Single(p => p.Name == "request");
            var json = Encoding.UTF8.GetString(requestPart.Content!.Value.Span);

            Assert.Contains("future_option", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_omitted_request_still_sends_a_valid_empty_object()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.FileRetainAsync("b", [HindsightFileUpload.FromBytes("n.txt", new byte[] { 1 })], null, CancellationToken.None);

            var requestPart = transport.Requests[0].Multipart!.Parts.Single(p => p.Name == "request");
            Assert.Equal("{}", Encoding.UTF8.GetString(requestPart.Content!.Value.Span));
        }
    }

    [Fact]
    public async Task Bank_transfer_import_sends_the_archive_with_its_query_options()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.ImportBankTransferAsync(
                "b",
                HindsightFileUpload.FromBytes("bank.zip", new byte[] { 1, 2, 3 }),
                "target",
                true,
                CancellationToken.None);

            var request = transport.Requests[0];
            Assert.Equal("/v1/default/banks/b/transfer/import", request.Path);
            Assert.False(request.IsIdempotent);

            var query = request.Query.ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal("target", query["target_bank_id"]);
            Assert.Equal("true", query["include_data"]);
            Assert.Equal("file", request.Multipart!.Parts[0].Name);
        }
    }

    [Fact]
    public async Task Bank_transfer_import_omits_withheld_query_options()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.ImportBankTransferAsync(
                "b", HindsightFileUpload.FromBytes("bank.zip", new byte[] { 1 }), null, null, CancellationToken.None);

            Assert.Empty(transport.Requests[0].Query);
        }
    }

    [Fact]
    public async Task Bank_transfer_import_rejects_an_upload_with_no_content()
    {
        var (client, transport) = Create();
        using (client)
        {
            // A filename but neither a stream nor bytes.
            var empty = new HindsightFileUpload { FileName = "nothing.zip" };

            var exception = await Assert.ThrowsAsync<HindsightValidationException>(
                () => client.ImportBankTransferAsync("b", empty, null, null, CancellationToken.None));

            Assert.Contains("neither a stream nor bytes", exception.Message, StringComparison.Ordinal);
            Assert.Empty(transport.Requests);
        }
    }

    [Fact]
    public async Task Bank_transfer_import_uses_the_zip_content_type_by_default()
    {
        var (client, transport) = Create();
        using (client)
        {
            await client.ImportBankTransferAsync(
                "b", HindsightFileUpload.FromBytes("bank.zip", new byte[] { 1 }), null, null, CancellationToken.None);

            Assert.Equal("application/octet-stream", transport.Requests[0].Multipart!.Parts[0].ContentType);
        }
    }

    [Fact]
    public async Task Uploads_honour_a_custom_tenant()
    {
        var transport = new StubTransport().EnqueueJson(200, """{"success":true}""");
        using var client = new HindsightClient(new Configuration.HindsightClientOptions
        {
            BaseAddress = "http://localhost:8888",
            Tenant = "acme",
            Transport = transport,
        })
        {
            TimeProvider = new ImmediateTimeProvider(),
        };

        await client.FileRetainAsync("b", [HindsightFileUpload.FromBytes("n.txt", new byte[] { 1 })], null, CancellationToken.None);

        Assert.Equal("/v1/acme/banks/b/files/retain", transport.Requests[0].Path);
    }
}
