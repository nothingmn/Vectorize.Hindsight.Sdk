using System.Globalization;

namespace Vectorize.Hindsight.Sdk.Admin;

/// <summary>The counts a <c>repair-bank</c> run reports.</summary>
/// <remarks>
/// All zero when the <c>Done:</c> line was absent, which a caller can distinguish by checking
/// whether parsing produced a value at all — <see cref="AdminResult.RepairTotals"/> is null in that
/// case rather than a zeroed instance.
/// </remarks>
public sealed class RepairBankTotals
{
    /// <summary>Schemas visited.</summary>
    public long Schemas { get; init; }

    /// <summary>Banks scanned.</summary>
    public long BanksScanned { get; init; }

    /// <summary>Indexes already present.</summary>
    public long AlreadyPresent { get; init; }

    /// <summary>Indexes created.</summary>
    public long Created { get; init; }

    /// <summary>Indexes dropped.</summary>
    public long Dropped { get; init; }

    /// <summary>Operations that failed.</summary>
    public long Failed { get; init; }
}

/// <summary>
/// Parsing helpers for the CLI's summary lines.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately tolerant, because the output is prose rather than a contract. A line that does not
/// match yields nothing rather than throwing: a wording change in a future release should degrade the
/// convenience surface, never break the call. The raw text is always on
/// <see cref="AdminResult.StandardOutput"/> alongside, so nothing is lost.
/// </para>
/// <para>
/// This is the same rule the HTTP client applies to union arms and unmodelled fields — never fail a
/// whole result because one part is unfamiliar.
/// </para>
/// </remarks>
internal static class AdminOutputParser
{
    private const string BackedUpMarker = "Backed up ";
    private const string AcrossMarker = " across ";

    /// <summary>Reads the row count from a <c>backup</c> summary.</summary>
    /// <param name="output">The raw standard output.</param>
    /// <returns>The count, or null when the summary is absent or worded differently.</returns>
    internal static long? ParseBackedUpRows(string output)
    {
        foreach (var line in Lines(output))
        {
            var index = line.IndexOf(BackedUpMarker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var rest = line[(index + BackedUpMarker.Length)..];
            var space = rest.IndexOf(' ');

            // "Backed up 8 rows across 23 tables": the number ends at the first space.
            if (space > 0 &&
                long.TryParse(rest[..space], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows))
            {
                return rows;
            }
        }

        return null;
    }

    /// <summary>Reads the table count from a <c>backup</c> summary.</summary>
    /// <param name="output">The raw standard output.</param>
    /// <returns>The count, or null when the summary is absent or worded differently.</returns>
    internal static long? ParseBackedUpTables(string output)
    {
        foreach (var line in Lines(output))
        {
            if (line.IndexOf(BackedUpMarker, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var across = line.IndexOf(AcrossMarker, StringComparison.OrdinalIgnoreCase);
            if (across < 0)
            {
                continue;
            }

            var after = line[(across + AcrossMarker.Length)..];
            var space = after.IndexOf(' ');
            var number = space > 0 ? after[..space] : after;

            if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tables))
            {
                return tables;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the <c>Done:</c> totals a <c>repair-bank</c> run prints.
    /// </summary>
    /// <remarks>
    /// The counts are read from the first occurrence of each label. The per-schema line and the
    /// <c>Done:</c> line both carry these words, and the <c>Done:</c> line is the authoritative total,
    /// so parsing is anchored to that line rather than to a substring search across the stream.
    /// </remarks>
    /// <param name="output">The raw standard output.</param>
    /// <returns>The counts, or null when the line is absent.</returns>
    internal static RepairBankTotals? ParseRepairTotals(string output)
    {
        foreach (var line in Lines(output))
        {
            if (!line.StartsWith("Done:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new RepairBankTotals
            {
                Schemas = ReadCountBefore(line, "schema(s)"),
                BanksScanned = ReadCountBefore(line, "bank(s) scanned"),
                AlreadyPresent = ReadCountBefore(line, "already present"),
                Created = ReadCountBefore(line, "created"),
                Dropped = ReadCountBefore(line, "dropped"),
                Failed = ReadCountBefore(line, "failed"),
            };
        }

        return null;
    }

    /// <summary>Whether the output reports that nothing matched.</summary>
    /// <param name="output">The raw standard output.</param>
    internal static bool ReportsNothingFound(string output) =>
        Lines(output).Any(line => line.Equals("No processing tasks found", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the integer immediately preceding a label.</summary>
    /// <param name="line">The line to read from.</param>
    /// <param name="label">The label that follows the number, e.g. <c>bank(s) scanned</c>.</param>
    private static long ReadCountBefore(string line, string label)
    {
        var index = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 0;
        }

        // "3 bank(s) scanned": the number is the whitespace-delimited token before the label.
        var before = line[..index].TrimEnd();
        var start = before.LastIndexOf(' ');
        var token = start >= 0 ? before[(start + 1)..] : before;

        return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static IEnumerable<string> Lines(string text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);
}
