using System.Globalization;
using Vectorize.Hindsight.Sdk;
using Vectorize.Hindsight.Sdk.Configuration;
using Vectorize.Hindsight.Sdk.CrossBank;
using Vectorize.Hindsight.Sdk.Models;

// Cross-bank recall against the live rig.
//
// This is the only check that proves the feature works against a real deployment: real banks,
// real recall, real provenance. Three banks are created with deliberately different content, so
// the merge has something to merge and the deduplication has a genuine duplicate to find.
//
// The rig is at 127.0.0.1:8888. Production is never touched.

var baseAddress = args.Length > 0 ? args[0] : "http://127.0.0.1:8888";
var suffix = Guid.NewGuid().ToString("N")[..8];
var bankA = $"xbank-a-{suffix}";
var bankB = $"xbank-b-{suffix}";
var bankC = $"xbank-c-{suffix}";

using var client = new HindsightClient(new HindsightClientOptions { BaseAddress = baseAddress });

Console.WriteLine($"rig: {baseAddress}");
Console.WriteLine();

async Task<int> SeedAsync(string bankId, string documentId, params string[] facts)
{
    var items = facts.Select((fact, index) => new MemoryItem
    {
        Content = fact,
        Context = $"seeded for cross-bank verification ({index})",
        DocumentId = documentId,
    }).ToList();

    var response = await client.RetainMemoriesAsync(
        bankId,
        new RetainRequest { Items = items, Async = false },
        CancellationToken.None);

    return response.ItemsCount ?? items.Count;
}

// Bank A and Bank B deliberately share one fact under the same document and chunk, which is the
// duplicate the merge should collapse. Everything else is distinct per bank.
Console.WriteLine("seeding three banks...");
await client.CreateOrUpdateBankAsync(bankA, new CreateBankRequest(), CancellationToken.None);
await client.CreateOrUpdateBankAsync(bankB, new CreateBankRequest(), CancellationToken.None);
await client.CreateOrUpdateBankAsync(bankC, new CreateBankRequest(), CancellationToken.None);

// The first fact is seeded into both A and B under the SAME document id, which models one source
// document indexed into two banks - the case deduplication exists to find. Document ids differ
// for the rest, because documents that differ are different evidence.
var seededA = await SeedAsync(bankA, "shared-source-doc",
    "Alice Nakamura is the principal engineer on the billing platform.",
    "Alice Nakamura reports to Dana Whitfield.");
var seededB = await SeedAsync(bankB, "shared-source-doc",
    "Alice Nakamura is the principal engineer on the billing platform.",
    "Alice Nakamura is based in the Vancouver office.");
var seededC = await SeedAsync(bankC, "doc-c",
    "The billing platform migrated to PostgreSQL in March.",
    "Billing runs a nightly reconciliation at 02:00 UTC.");

Console.WriteLine($"  bank A: {seededA} retained");
Console.WriteLine($"  bank B: {seededB} retained");
Console.WriteLine($"  bank C: {seededC} retained");
Console.WriteLine();

// --- The fan-out ---------------------------------------------------------

Console.WriteLine("=== cross-bank recall ===");
var result = await client.RecallAcrossBanksAsync(
    "Who is Alice Nakamura and what do we know about billing?",
    [bankA, bankB, bankC],
    new CrossBankRecallOptions { PerBankLimit = 8, MaxCandidates = 12 },
    CancellationToken.None);

Console.WriteLine($"banks asked        : {result.BankCount}");
Console.WriteLine($"banks answered     : {result.SucceededCount}");
Console.WriteLine($"candidate pool     : {result.CandidatePoolSize}");
Console.WriteLine($"duplicates merged  : {result.DuplicatesMerged}");
Console.WriteLine($"final candidates   : {result.Candidates.Count}");
Console.WriteLine($"partial            : {result.IsPartial}");
Console.WriteLine($"contributing banks : {string.Join(", ", result.ContributingBanks)}");
Console.WriteLine();

Console.WriteLine("merged order (provenance preserved):");
foreach (var candidate in result.Candidates)
{
    var score = candidate.RetrievalScore is { } s
        ? s.ToString("0.000", CultureInfo.InvariantCulture)
        : "n/a";

    var banks = candidate.IsDuplicateAcrossBanks
        ? $"seen in {string.Join("+", candidate.SeenInBanks)}"
        : $"bank {candidate.BankId}";

    var text = candidate.Text.Length > 62 ? candidate.Text[..62] + "..." : candidate.Text;
    Console.WriteLine($"  rank {candidate.Rank}  score {score,-6}  {banks,-24}  {text}");
}

Console.WriteLine();

// --- A failing bank must not sink the others -----------------------------

Console.WriteLine("=== degraded: one bank does not exist ===");
var degraded = await client.RecallAcrossBanksAsync(
    "Who is Alice Nakamura?",
    [bankA, $"xbank-missing-{suffix}"],
    new CrossBankRecallOptions { PerBankLimit = 5, ValidateBanksExist = true },
    CancellationToken.None);

Console.WriteLine("a bank that does not exist answers HTTP 200 with no results, so it is only");
Console.WriteLine("distinguishable from an empty bank by listing the banks first.");
Console.WriteLine($"partial            : {degraded.IsPartial}");
Console.WriteLine($"candidates         : {degraded.Candidates.Count}");
foreach (var failure in degraded.Failures)
{
    Console.WriteLine($"  {failure.BankId}: {failure.Failure?.GetType().Name}");
}

Console.WriteLine();

// --- The context handed to a model --------------------------------------

Console.WriteLine("=== rendered context (what a model receives) ===");
var context = result.ToContext();
foreach (var line in context.Split('\n').Take(16))
{
    Console.WriteLine($"  {line}");
}

Console.WriteLine();

// --- Verify the claims ---------------------------------------------------

var failures = new List<string>();

if (result.SucceededCount != 3)
{
    failures.Add($"expected 3 banks to answer, got {result.SucceededCount}");
}

if (result.Candidates.Count == 0)
{
    failures.Add("no candidates were merged");
}

// Every candidate must still know its bank. This is the property the whole design rests on.
if (result.Candidates.Any(c => string.IsNullOrEmpty(c.BankId)))
{
    failures.Add("a candidate lost its bank id");
}

// The duplicate seeded into A and B must be recognised as one fact seen twice.
var duplicate = result.Candidates.FirstOrDefault(c => c.IsDuplicateAcrossBanks);
if (duplicate is null)
{
    failures.Add("the fact seeded into both bank A and bank B was not recognised as a duplicate");
}
else
{
    Console.WriteLine($"duplicate collapsed, seen in: {string.Join(", ", duplicate.SeenInBanks)}");
}

// A bank that does not exist must be reported, not silently dropped.
if (!degraded.IsPartial || degraded.Failures.Count != 1)
{
    failures.Add("a missing bank was not reported as a partial failure");
}

if (degraded.Candidates.Count == 0)
{
    failures.Add("the surviving bank returned nothing after the other bank failed");
}

if (!context.Contains("[SOURCE", StringComparison.Ordinal))
{
    failures.Add("the rendered context carries no source markers");
}

Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine("FAILED:");
    foreach (var failure in failures)
    {
        Console.WriteLine($"  - {failure}");
    }

    return 1;
}

Console.WriteLine("PASSED: fan-out, provenance, deduplication, and partial-failure reporting all verified live.");

// Leave the rig as it was found.
foreach (var bank in new[] { bankA, bankB, bankC })
{
    try
    {
        await client.DeleteBankAsync(bank, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"cleanup of {bank}: {ex.Message}");
    }
}

Console.WriteLine("cleaned up the three banks.");
return 0;
