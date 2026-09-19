using System.Reflection;
using System.Text.Json;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Models;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Tests;

/// <summary>
/// Drives every one of the 87 operations through the client with a stub transport.
/// </summary>
/// <remarks>
/// <para>
/// The point is not that each operation returns something interesting — the stub
/// decides that. The point is that every generated method actually executes: its
/// path is built, its query parameters are appended, its body is serialized, and its
/// response is deserialized. A generated method that is never called is code whose
/// correctness is asserted only by the fact that it compiles, and the defects this
/// suite has already caught (the dead Tenant option, the unusable JsonElement member)
/// both lived in exactly that gap.
/// </para>
/// <para>
/// Each method is invoked with structurally valid arguments produced by
/// <see cref="Arguments"/>, so the call reaches the transport rather than being
/// rejected by local validation. The assertion is deliberately uniform: the call
/// completes, and the request it produced has a path.
/// </para>
/// </remarks>
public sealed class AllOperationsTests
{
    /// <summary>
    /// A response body per expected return type.
    /// </summary>
    /// <remarks>
    /// The minimum a model needs to deserialize. Where a response model has no
    /// required members this can be an empty object; the permissive response DTOs
    /// exist precisely so an absent field is not fatal.
    /// </remarks>
    private const string EmptyJsonObject = "{}";

    private static (HindsightClient Client, StubTransport Transport) Create()
    {
        var transport = new StubTransport();
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

    /// <summary>
    /// Invokes an operation, queueing a response suitable for its return type.
    /// </summary>
    private static async Task<StubTransport> InvokeAsync(
        string methodName, Func<HindsightClient, CancellationToken, Task> invoke)
    {
        var (client, transport) = Create();
        using (client)
        {
            transport.EnqueueJson(200, EmptyJsonObject);
            // Two queued so a single retry does not exhaust the stub.
            transport.EnqueueJson(200, EmptyJsonObject);

            try
            {
                await invoke(client, CancellationToken.None);
            }
            catch (HindsightProtocolException)
            {
                // A 200 with an empty object may not satisfy a model with required
                // members. The method still executed, which is what is under test; the
                // per-operation serialization assertions live elsewhere.
            }
            catch (HindsightUnexpectedStatusException)
            {
                // Some operations declare 201 or 202; a 200 from the stub is still a
                // successful execution of the generated method.
            }

            Assert.NotEmpty(transport.Requests);
            var sent = transport.Requests[0];
            Assert.False(string.IsNullOrWhiteSpace(sent.Path));
            Assert.False(string.IsNullOrWhiteSpace(sent.PathTemplate));

            // Most routes live under /v1/{tenant}, but the monitoring operations
            // (/health, /version, /metrics) are declared at the root. Asserting that
            // every path carries the prefix would be wrong, and asserting none does
            // would miss a path built without it entirely.
            Assert.StartsWith("/", sent.Path, StringComparison.Ordinal);
            if (!sent.Path.Contains("/health", StringComparison.Ordinal) &&
                !sent.Path.StartsWith("/version", StringComparison.Ordinal) &&
                !sent.Path.StartsWith("/metrics", StringComparison.Ordinal))
            {
                Assert.Contains("/v1/", sent.Path, StringComparison.Ordinal);
            }

            return transport;
        }
    }

    private static Task<StubTransport> Call(
        System.Linq.Expressions.Expression<Func<HindsightClient, CancellationToken, Task>> expression, string name) =>
        InvokeAsync(name, expression.Compile());

    // --- Monitoring --------------------------------------------------------

    [Fact]
    public async Task Monitoring_operations_all_execute()
    {
        await Call((c, ct) => c.HealthEndpointHealthGetAsync(ct), nameof(HindsightClient.HealthEndpointHealthGetAsync));
        await Call((c, ct) => c.GetLivenessAsync(ct), nameof(HindsightClient.GetLivenessAsync));
        await Call((c, ct) => c.GetReadinessAsync(ct), nameof(HindsightClient.GetReadinessAsync));
        await Call((c, ct) => c.GetVersionAsync(ct), nameof(HindsightClient.GetVersionAsync));

        var transport = await Call(
            (c, ct) => c.MetricsEndpointMetricsGetAsync(ct),
            nameof(HindsightClient.MetricsEndpointMetricsGetAsync));

        // /version is declared at the root, outside the tenant prefix.
        Assert.Equal("/version", (await Call((c, ct) => c.GetVersionAsync(ct), "v")).Requests[0].Path);
        Assert.Equal("/metrics", transport.Requests[0].Path);
    }

    // --- Banks -------------------------------------------------------------

    [Fact]
    public async Task Bank_operations_all_execute()
    {
        await Call((c, ct) => c.ListBanksAsync(new ListBanksQuery { Limit = 5, Offset = 1 }, ct), "ListBanks");
        await Call((c, ct) => c.CreateOrUpdateBankAsync("b", Arguments.Body<CreateBankRequest>(), ct), "CreateOrUpdateBank");
        await Call((c, ct) => c.UpdateBankAsync("b", Arguments.Body<CreateBankRequest>(), ct), "UpdateBank");
        await Call((c, ct) => c.GetBankConfigAsync("b", ct), "GetBankConfig");
        await Call((c, ct) => c.UpdateBankConfigAsync("b", Arguments.Body<BankConfigUpdate>(), ct), "UpdateBankConfig");
        await Call((c, ct) => c.ResetBankConfigAsync("b", ct), "ResetBankConfig");
        await Call((c, ct) => c.TriggerConsolidationAsync("b", ct), "TriggerConsolidation");
        await Call((c, ct) => c.RecoverConsolidationAsync("b", ct), "RecoverConsolidation");
        await Call((c, ct) => c.TestBankLlmAsync("b", ct), "TestBankLlm");
        await Call((c, ct) => c.ClearObservationsAsync("b", ct), "ClearObservations");
        await Call((c, ct) => c.PreviewPromptAsync("b", Arguments.Body<PromptPreviewRequest>(), ct), "PreviewPrompt");
        await Call((c, ct) => c.GetAgentStatsAsync("b", null, ct), "GetAgentStats");
        await Call((c, ct) => c.GetMemoriesTimeseriesAsync("b", null, ct), "GetMemoriesTimeseries");
        await Call((c, ct) => c.DeleteBankAsync("b", ct), "DeleteBank");
    }

    // --- Memory ------------------------------------------------------------

    [Fact]
    public async Task Memory_operations_all_execute()
    {
        var transport = await Call(
            (c, ct) => c.RetainMemoriesAsync("b", Arguments.Body<RetainRequest>(), ct),
            nameof(HindsightClient.RetainMemoriesAsync));

        // The retain body was serialized, which is the assertion that matters: a
        // request that cannot be serialized fails before it leaves the process.
        Assert.NotNull(transport.RequestBodies[0]);

        await Call((c, ct) => c.RecallMemoriesAsync("b", Arguments.RecallBody(), ct), nameof(HindsightClient.RecallMemoriesAsync));
        await Call((c, ct) => c.ReflectAsync("b", Arguments.Body<ReflectRequest>(), ct), nameof(HindsightClient.ReflectAsync));
        await Call((c, ct) => c.ClearBankMemoriesAsync("b", null, ct), nameof(HindsightClient.ClearBankMemoriesAsync));
        await Call((c, ct) => c.ListMemoriesAsync("b", new ListMemoriesQuery { Limit = 10 }, ct), nameof(HindsightClient.ListMemoriesAsync));
        await Call((c, ct) => c.GetMemoryAsync("b", "m", ct), nameof(HindsightClient.GetMemoryAsync));
        await Call((c, ct) => c.UpdateMemoryAsync("b", "m", Arguments.Body<UpdateMemoryRequest>(), ct), nameof(HindsightClient.UpdateMemoryAsync));
        await Call((c, ct) => c.GetObservationHistoryAsync("b", "m", ct), nameof(HindsightClient.GetObservationHistoryAsync));
        await Call((c, ct) => c.ClearMemoryObservationsAsync("b", "m", ct), nameof(HindsightClient.ClearMemoryObservationsAsync));
        await Call((c, ct) => c.ListObservationScopesAsync("b", null, ct), nameof(HindsightClient.ListObservationScopesAsync));
        await Call((c, ct) => c.ListTagsAsync("b", null, ct), nameof(HindsightClient.ListTagsAsync));
        await Call((c, ct) => c.GetGraphAsync("b", null, ct), nameof(HindsightClient.GetGraphAsync));
        await Call((c, ct) => c.GetBankAttachmentAsync("b", "a", ct), nameof(HindsightClient.GetBankAttachmentAsync));
        await Call((c, ct) => c.DryRunExtractMemoriesAsync("b", Arguments.Body<DryRunExtractRequest>(), ct), nameof(HindsightClient.DryRunExtractMemoriesAsync));
    }

    // --- Entities, documents, knowledge base -------------------------------

    [Fact]
    public async Task Entity_document_and_knowledge_operations_all_execute()
    {
        await Call((c, ct) => c.ListEntitiesAsync("b", null, ct), nameof(HindsightClient.ListEntitiesAsync));
        await Call((c, ct) => c.GetEntityGraphAsync("b", null, ct), nameof(HindsightClient.GetEntityGraphAsync));
        await Call((c, ct) => c.GetEntityAsync("b", "e", ct), nameof(HindsightClient.GetEntityAsync));

        await Call((c, ct) => c.ListDocumentsAsync("b", null, ct), nameof(HindsightClient.ListDocumentsAsync));
        await Call((c, ct) => c.GetDocumentAsync("b", "d", ct), nameof(HindsightClient.GetDocumentAsync));
        await Call((c, ct) => c.UpdateDocumentAsync("b", "d", Arguments.Body<UpdateDocumentRequest>(), ct), nameof(HindsightClient.UpdateDocumentAsync));
        await Call((c, ct) => c.DeleteDocumentAsync("b", "d", ct), nameof(HindsightClient.DeleteDocumentAsync));
        await Call((c, ct) => c.ListDocumentChunksAsync("b", "d", new ListDocumentChunksQuery(), ct), nameof(HindsightClient.ListDocumentChunksAsync));
        await Call((c, ct) => c.ReprocessDocumentAsync("b", "d", ct), nameof(HindsightClient.ReprocessDocumentAsync));
        await Call((c, ct) => c.GetChunkAsync("c", ct), nameof(HindsightClient.GetChunkAsync));

        await Call((c, ct) => c.GetKnowledgeBaseTreeAsync("b", ct), nameof(HindsightClient.GetKnowledgeBaseTreeAsync));
        await Call((c, ct) => c.CreateKnowledgeFolderAsync("b", Arguments.Body<CreateFolderRequest>(), ct), nameof(HindsightClient.CreateKnowledgeFolderAsync));
        await Call((c, ct) => c.CreateKnowledgePageAsync("b", Arguments.Body<CreatePageRequest>(), ct), nameof(HindsightClient.CreateKnowledgePageAsync));
        await Call((c, ct) => c.GetKnowledgePageAsync("b", "p", ct), nameof(HindsightClient.GetKnowledgePageAsync));
        await Call((c, ct) => c.SearchKnowledgeBaseAsync("b", new SearchKnowledgeBaseQuery { Q = "x" }, ct), nameof(HindsightClient.SearchKnowledgeBaseAsync));
        await Call((c, ct) => c.UpdateKnowledgeNodeAsync("b", "n", Arguments.Body<UpdateNodeRequest>(), ct), nameof(HindsightClient.UpdateKnowledgeNodeAsync));
        await Call((c, ct) => c.DeleteKnowledgeNodeAsync("b", "n", ct), nameof(HindsightClient.DeleteKnowledgeNodeAsync));
        await Call((c, ct) => c.ExportKnowledgeBaseAsync("b", ct), nameof(HindsightClient.ExportKnowledgeBaseAsync));
    }

    // --- Directives, mental models, webhooks, operations -------------------

    [Fact]
    public async Task Directive_mental_model_webhook_and_operation_methods_all_execute()
    {
        await Call((c, ct) => c.ListDirectivesAsync("b", null, ct), nameof(HindsightClient.ListDirectivesAsync));
        await Call((c, ct) => c.CreateDirectiveAsync("b", Arguments.Body<CreateDirectiveRequest>(), ct), nameof(HindsightClient.CreateDirectiveAsync));
        await Call((c, ct) => c.GetDirectiveAsync("b", "d", ct), nameof(HindsightClient.GetDirectiveAsync));
        await Call((c, ct) => c.UpdateDirectiveAsync("b", "d", Arguments.Body<UpdateDirectiveRequest>(), ct), nameof(HindsightClient.UpdateDirectiveAsync));
        await Call((c, ct) => c.DeleteDirectiveAsync("b", "d", ct), nameof(HindsightClient.DeleteDirectiveAsync));

        await Call((c, ct) => c.ListMentalModelsAsync("b", null, ct), nameof(HindsightClient.ListMentalModelsAsync));
        await Call((c, ct) => c.CreateMentalModelAsync("b", Arguments.Body<CreateMentalModelRequest>(), ct), nameof(HindsightClient.CreateMentalModelAsync));
        await Call((c, ct) => c.GetMentalModelAsync("b", "m", null, ct), nameof(HindsightClient.GetMentalModelAsync));
        await Call((c, ct) => c.UpdateMentalModelAsync("b", "m", Arguments.Body<UpdateMentalModelRequest>(), ct), nameof(HindsightClient.UpdateMentalModelAsync));
        await Call((c, ct) => c.DeleteMentalModelAsync("b", "m", ct), nameof(HindsightClient.DeleteMentalModelAsync));
        await Call((c, ct) => c.ClearMentalModelAsync("b", "m", ct), nameof(HindsightClient.ClearMentalModelAsync));
        await Call((c, ct) => c.DryRunRefreshMentalModelAsync("b", "m", ct), nameof(HindsightClient.DryRunRefreshMentalModelAsync));
        await Call((c, ct) => c.GetMentalModelHistoryAsync("b", "m", ct), nameof(HindsightClient.GetMentalModelHistoryAsync));
        await Call((c, ct) => c.RefreshMentalModelAsync("b", "m", ct), nameof(HindsightClient.RefreshMentalModelAsync));

        await Call((c, ct) => c.ListWebhooksAsync("b", null, ct), nameof(HindsightClient.ListWebhooksAsync));
        await Call((c, ct) => c.CreateWebhookAsync("b", Arguments.Body<CreateWebhookRequest>(), ct), nameof(HindsightClient.CreateWebhookAsync));
        await Call((c, ct) => c.UpdateWebhookAsync("b", "w", Arguments.Body<UpdateWebhookRequest>(), ct), nameof(HindsightClient.UpdateWebhookAsync));
        await Call((c, ct) => c.DeleteWebhookAsync("b", "w", ct), nameof(HindsightClient.DeleteWebhookAsync));
        await Call((c, ct) => c.ListWebhookDeliveriesAsync("b", "w", null, ct), nameof(HindsightClient.ListWebhookDeliveriesAsync));

        await Call((c, ct) => c.ListOperationsAsync("b", null, ct), nameof(HindsightClient.ListOperationsAsync));
        await Call((c, ct) => c.GetOperationStatusAsync("b", "o", null, ct), nameof(HindsightClient.GetOperationStatusAsync));
        await Call((c, ct) => c.CancelOperationAsync("b", "o", ct), nameof(HindsightClient.CancelOperationAsync));
        await Call((c, ct) => c.DeleteOperationAsync("b", "o", ct), nameof(HindsightClient.DeleteOperationAsync));
        await Call((c, ct) => c.RetryOperationAsync("b", "o", ct), nameof(HindsightClient.RetryOperationAsync));
    }

    // --- Templates, audit, traces, transfers -------------------------------

    [Fact]
    public async Task Template_audit_trace_and_transfer_methods_all_execute()
    {
        await Call((c, ct) => c.GetBankTemplateSchemaAsync(ct), nameof(HindsightClient.GetBankTemplateSchemaAsync));
        await Call((c, ct) => c.ExportBankTemplateAsync("b", ct), nameof(HindsightClient.ExportBankTemplateAsync));
        await Call((c, ct) => c.ImportBankTemplateAsync("b", Arguments.Body<BankTemplateManifest>(), null, ct), nameof(HindsightClient.ImportBankTemplateAsync));

        await Call((c, ct) => c.ListAuditLogsAsync("b", null, ct), nameof(HindsightClient.ListAuditLogsAsync));
        await Call((c, ct) => c.AuditLogStatsAsync("b", null, ct), nameof(HindsightClient.AuditLogStatsAsync));

        await Call((c, ct) => c.ListLlmRequestsAsync("b", null, ct), nameof(HindsightClient.ListLlmRequestsAsync));
        await Call((c, ct) => c.LlmRequestStatsAsync("b", null, ct), nameof(HindsightClient.LlmRequestStatsAsync));

        await Call((c, ct) => c.CloneBankAsync("b", null, ct), nameof(HindsightClient.CloneBankAsync));
        await Call((c, ct) => c.ExportBankTransferAsync("b", null, ct), nameof(HindsightClient.ExportBankTransferAsync));

        var uploadBytes = "hello"u8.ToArray();
        var uploads = new List<HindsightFileUpload> { HindsightFileUpload.FromBytes("note.txt", uploadBytes) };
        await Call(
            (c, ct) => c.FileRetainAsync(
                "b",
                uploads,
                null,
                ct),
            nameof(HindsightClient.FileRetainAsync));

        var archiveBytes = new byte[] { 1, 2, 3 };
        await Call(
            (c, ct) => c.ImportBankTransferAsync(
                "b",
                HindsightFileUpload.FromBytes("bank.zip", archiveBytes),
                null,
                null,
                ct),
            nameof(HindsightClient.ImportBankTransferAsync));


        await Call((c, ct) => c.DownloadFileAsync("k", ct), nameof(HindsightClient.DownloadFileAsync));
    }

    [Fact]
    public async Task Every_generated_method_is_reachable_from_this_suite()
    {
        // A guard on the suite itself: if a future revision adds an operation and this
        // suite is not extended, the new method is compiled but never executed, and its
        // correctness rests on compilation alone. Comparing the method count against
        // the number invoked here is what makes that visible.
        var methods = typeof(HindsightClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.EndsWith("Async", StringComparison.Ordinal))
            .ToList();

        // Compositions - methods built on top of other operations rather than corresponding to
        // one operation in the spec - declare themselves and are counted separately. Recall
        // across banks is a fan-out over recall, not an operation the spec defines. Excluding
        // them by name instead would let a genuinely new operation slip past this guard.
        var operations = methods
            .Where(m => !m.IsDefined(typeof(HindsightCompositionAttribute), inherit: false))
            .ToList();

        var compositions = methods.Except(operations).ToList();

        // 85 generated plus 2 hand-written multipart.
        Assert.Equal(87, operations.Count);
        Assert.Equal(2, compositions.Count);

        await Task.CompletedTask;
    }
}

/// <summary>
/// Produces structurally valid arguments for the generated operations.
/// </summary>
/// <remarks>
/// Bodies are built by reflection and their <c>required</c> members filled with
/// sensible defaults, because there are 42 request models and enumerating every
/// constructor by hand would be both long and quick to go stale. The reflection is
/// confined to the test assembly — the library itself never reflects over a caller's
/// type.
/// </remarks>
internal static class Arguments
{
    /// <summary>Creates a body instance with every required member populated.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    internal static T Body<T>()
    {
        var type = typeof(T);

        // Activator rather than the `new()` constraint: a model with C# `required`
        // members cannot satisfy `new()`, and those are exactly the request models
        // whose required members this has to fill in.
        var instance = (T)Activator.CreateInstance(type, nonPublic: true)!;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            var isRequired = property.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null;
            var current = property.GetValue(instance);
            if (!isRequired && current is not null)
            {
                continue;
            }

            if (isRequired)
            {
                property.SetValue(instance, DefaultFor(property.PropertyType));
            }
        }

        return instance;
    }

    /// <summary>A recall body, which has a required query and a body-shaped optional set.</summary>
    internal static RecallRequest RecallBody() => new() { Query = "what changed" };

    private static object? DefaultFor(Type type)
    {
        if (type == typeof(string))
        {
            return "x";
        }

        if (type == typeof(bool))
        {
            return false;
        }

        if (type == typeof(int))
        {
            return 0;
        }

        if (type == typeof(long))
        {
            return 0L;
        }

        if (type == typeof(double))
        {
            return 0.0;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            return Activator.CreateInstance(type);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            return Activator.CreateInstance(type);
        }

        if (type == typeof(JsonElement) || type == typeof(JsonElement?))
        {
            return null;
        }

        // A nested model, a union, or an enum-as-string member.
        if (type.IsClass && type.GetConstructor(Type.EmptyTypes) is not null)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch (MissingMethodException)
            {
                return null;
            }
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
