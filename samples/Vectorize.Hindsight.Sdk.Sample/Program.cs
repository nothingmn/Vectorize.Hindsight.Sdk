using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vectorize.Hindsight.Sdk;
using Vectorize.Hindsight.Sdk.DependencyInjection;
using Vectorize.Hindsight.Sdk.Errors;
using Vectorize.Hindsight.Sdk.Models;

// A runnable end-to-end sample. Point it at a Hindsight server with
//   HINDSIGHT_SAMPLE_BASE_URL (default http://localhost:8888).
//
// It is written to be safe to run repeatedly: it uses a bank named for the process and
// deletes it at the end, and it reports a failure honestly rather than swallowing one.

var baseAddress = Environment.GetEnvironmentVariable("HINDSIGHT_SAMPLE_BASE_URL")
    ?? "http://localhost:8888";

Console.WriteLine($"Hindsight sample against {baseAddress}");
Console.WriteLine(new string('-', 60));

// --- Composition root -------------------------------------------------------
// The DI package is the convenient route; a client can equally be constructed directly.
var services = new ServiceCollection();
services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
services.AddHindsight(options =>
{
    options.BaseAddress = baseAddress;
    options.MaxAttempts = 2;
});

await using var provider = services.BuildServiceProvider();

var client = provider.GetRequiredService<HindsightClient>();

// A real host threads its shutdown token here instead of a timer.
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = cancellation.Token;

var bankId = $"sdk-sample-{Guid.NewGuid():N}"[..32];
var succeeded = false;

try
{
    // --- Version ------------------------------------------------------------
    var version = await client.GetVersionAsync(token);
    Console.WriteLine($"server api_version : {version.ApiVersion}");

    // Features is nullable because response models are tolerant of an absent field, so a
    // caller reads it defensively rather than trusting it is always there.
    var features = version.Features;
    Console.WriteLine($"  observations      : {features?.Observations}");
    Console.WriteLine($"  worker            : {features?.Worker}");
    Console.WriteLine($"  bank_llm_health   : {features?.BankLlmHealth}");
    Console.WriteLine();

    // --- Bank lifecycle -----------------------------------------------------
    await client.CreateOrUpdateBankAsync(bankId, new CreateBankRequest(), token);
    Console.WriteLine($"created bank       : {bankId}");

    var config = await client.GetBankConfigAsync(bankId, token);
    Console.WriteLine($"  bank_id from cfg : {config.BankId}");

    var banks = await client.ListBanksAsync(null, token);
    Console.WriteLine($"  banks visible    : {banks.Total}");

    // --- Retain -------------------------------------------------------------
    // The API is designed around tokens, not result counts: set max_tokens to how
    // much of the context you want memories to occupy.
    Console.WriteLine();
    Console.WriteLine("retaining a fact...");
    var retain = await client.RetainMemoriesAsync(
        bankId,
        new RetainRequest
        {
            Items =
            [
                new MemoryItem
                {
                    Content = HindsightMemoryContent.FromText(
                        "Rob is building a .NET client for the Hindsight agent-memory API."),
                    Context = "sdk sample",
                    DocumentId = "sample-doc-1",
                },
            ],
        },
        token);

    // A 200 here is the server acknowledging the request, not proof the extraction
    // finished - see the operation polling below.
    Console.WriteLine($"  accepted          : success={retain.Success} items={retain.ItemsCount} async={retain.Async}");
    Console.WriteLine($"  tokens (in/out)   : {retain.Usage?.InputTokens}/{retain.Usage?.OutputTokens}");

    // --- Recall -------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("recalling...");
    var recall = await client.RecallMemoriesAsync(
        bankId,
        new RecallRequest
        {
            Query = "What is Rob building?",
            Budget = HindsightValues.Budget.MID,
            MaxTokens = 1024,
        },
        token);

    foreach (var result in recall.Results)
    {
        Console.WriteLine($"  [{result.Type}] {result.Text}");
    }

    // --- Tag-group expression tree -----------------------------------------
    // The recursive filter is a real object graph, not a string to be assembled.
    var filter = HindsightTagGroup.Any(
        HindsightTagGroup.Tags(HindsightValues.TagGroupLeafMatch.ANY, "sample"),
        HindsightTagGroup.Not(HindsightTagGroup.Tags(HindsightValues.TagGroupLeafMatch.ANY, "excluded")));

    var tagged = await client.RecallMemoriesAsync(
        bankId,
        new RecallRequest { Query = "Rob", TagGroups = [filter] },
        token);
    Console.WriteLine($"  tag-filtered hits : {tagged.Results.Count}");

    // --- Async operations ---------------------------------------------------
    // An async retain returns immediately; the work is visible here, and this is where
    // a provider failure would show up rather than in the retain response.
    Console.WriteLine();
    Console.WriteLine("submitting an async retain...");
    var submitted = await client.RetainMemoriesAsync(
        bankId,
        new RetainRequest
        {
            Items = [new MemoryItem { Content = HindsightMemoryContent.FromText("Async sample fact about Hindsight.") }],
            Async = true,
        },
        token);

    Console.WriteLine($"  operation id      : {submitted.OperationId}");

    if (submitted.OperationId is { } operationId)
    {
        var status = await client.GetOperationStatusAsync(bankId, operationId, null, token);
        Console.WriteLine($"  status            : {status.Status}");

        // ChildOperations is nullable because a response model tolerates an absent field.
        foreach (var child in status.ChildOperations ?? [])
        {
            if (!string.IsNullOrEmpty(child.ErrorMessage))
            {
                // This is the important part: an upstream LLM failure arrives HERE, not
                // as an HTTP error on the retain call.
                Console.WriteLine($"  child {child.OperationId} error: {child.ErrorMessage[..Math.Min(160, child.ErrorMessage.Length)]}");
            }
        }
    }

    // --- Documents, entities, directives -----------------------------------
    var documents = await client.ListDocumentsAsync(bankId, null, token);
    Console.WriteLine();
    Console.WriteLine($"documents          : {documents.Total}");
    Console.WriteLine($"entities           : {(await client.ListEntitiesAsync(bankId, null, token)).Total}");
    Console.WriteLine($"directives         : {(await client.ListDirectivesAsync(bankId, null, token)).Total}");

    // --- Health -------------------------------------------------------------
    var readiness = await client.GetReadinessAsync(token);
    Console.WriteLine($"readiness          : {readiness.ValueKind}");

    succeeded = true;
}
catch (HindsightNotFoundException ex) when (ex.IsFeatureDisabled)
{
    // A 404 whose body names a disabled feature is not a missing resource. Distinguishing
    // them is why the exception carries this flag.
    Console.WriteLine($"feature disabled   : {ex.Message}");
    succeeded = true;
}
catch (HindsightRateLimitException ex)
{
    Console.WriteLine($"rate limited       : retry after {ex.RetryAfter}");
}
catch (HindsightRequestValidationException ex)
{
    Console.WriteLine($"server rejected the request on {ex.Endpoint}:");
    foreach (var error in ex.Errors ?? [])
    {
        Console.WriteLine($"  {error.LocationPath}: {error.Message}");
    }
}
catch (HindsightException ex)
{
    Console.WriteLine($"Hindsight error    : {ex.GetType().Name}: {ex.Message}");
}
finally
{
    // Repeatable runs: the bank is removed even when something above failed.
    try
    {
        await client.DeleteBankAsync(bankId, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine($"deleted bank       : {bankId}");
    }
    catch (HindsightException ex)
    {
        Console.WriteLine($"cleanup failed     : {ex.Message}");
    }
}

Console.WriteLine(new string('-', 60));
Console.WriteLine(succeeded ? "sample completed" : "sample did not complete");
