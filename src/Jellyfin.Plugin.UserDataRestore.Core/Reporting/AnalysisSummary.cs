using System.Globalization;
using System.Text;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;

namespace Jellyfin.Plugin.UserDataRestore.Core.Reporting;

/// <summary>
/// The operator-facing summary of a run (DESIGN §10).
/// </summary>
/// <remarks>
/// Counts and IDs only. Titles, usernames, paths, and state values belong in the
/// JSON plan, which an operator opens deliberately, not in a server log that gets
/// pasted into a forum thread.
/// </remarks>
public static class AnalysisSummary
{
    /// <summary>
    /// Renders the headline result.
    /// </summary>
    /// <param name="result">The completed analysis.</param>
    /// <param name="outcomes">What became of each planned write.</param>
    /// <param name="planId">The plan ID.</param>
    /// <param name="planPath">Where the plan was written.</param>
    /// <returns>A multi-line summary.</returns>
    /// <remarks>
    /// Written in the past tense throughout, because by the time this is rendered
    /// the writes have already been attempted. It once ended by telling the
    /// operator to run an apply task — which had already been folded into this
    /// one, so it named something that did not exist and implied the restore was
    /// still pending when it had in fact just happened.
    /// </remarks>
    public static string Render(
        AnalysisResult result,
        IReadOnlyList<WriteResult> outcomes,
        string planId,
        string planPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(outcomes);

        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"Inspected {result.Diagnostics.DetachedRowsInspected} detached rows ");
        text.Append(CultureInfo.InvariantCulture, $"against {result.Diagnostics.EligibleTargetCount} eligible items ");
        text.Append(CultureInfo.InvariantCulture, $"for {result.Diagnostics.KnownUserCount} users.");
        text.Append('\n');

        text.Append(outcomes.Count == 0
            ? "Nothing was restored: nothing was uniquely and safely recoverable."
            : RenderOutcomes(outcomes));
        text.Append('\n');

        text.Append("Candidates: ");
        text.Append(string.Join(
            ", ",
            ReasonCodes.All
                .Where(code => result.CandidateCounts[code] > 0)
                .Select(code => string.Create(CultureInfo.InvariantCulture, $"{ReasonCodes.ToWire(code)}={result.CandidateCounts[code]}"))));
        text.Append('\n');

        text.Append("Rows: ");
        text.Append(string.Join(
            ", ",
            ReasonCodes.All
                .Where(code => result.RowCounts[code] > 0)
                .Select(code => string.Create(CultureInfo.InvariantCulture, $"{ReasonCodes.ToWire(code)}={result.RowCounts[code]}"))));
        text.Append('\n');

        text.Append(CultureInfo.InvariantCulture, $"Plan {planId} written to {planPath}");
        return text.ToString();
    }

    /// <summary>
    /// States what became of the writes, naming every outcome that occurred.
    /// </summary>
    /// <remarks>
    /// Restores are stated as a fraction so a partial run cannot read as a whole
    /// one, and anything that is not a restore is named rather than folded into
    /// the remainder: "3 of 4" invites the reader to assume the fourth is coming,
    /// and it is not.
    /// </remarks>
    private static string RenderOutcomes(IReadOnlyList<WriteResult> outcomes)
    {
        var restored = outcomes.Count(entry => entry.Outcome == WriteOutcome.Restored);
        var rest = WriteOutcomes.All
            .Where(outcome => outcome != WriteOutcome.Restored)
            .Select(outcome => (Outcome: outcome, Count: outcomes.Count(entry => entry.Outcome == outcome)))
            .Where(entry => entry.Count > 0)
            .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Count} {WriteOutcomes.ToWire(entry.Outcome)}"))
            .ToArray();

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"Restored {restored} of {outcomes.Count} recoverable snapshots");

        return rest.Length == 0
            ? summary + "."
            : summary + " (" + string.Join(", ", rest) + ").";
    }
}
