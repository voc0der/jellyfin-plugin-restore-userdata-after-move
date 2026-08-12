using System.Globalization;
using System.Text;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

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
    /// <param name="planId">The plan ID.</param>
    /// <param name="planPath">Where the plan was written.</param>
    /// <returns>A multi-line summary.</returns>
    public static string Render(AnalysisResult result, string planId, string planPath)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = new StringBuilder();
        var ready = result.CandidateCounts[ReasonCode.Ready];

        text.Append(CultureInfo.InvariantCulture, $"Inspected {result.Diagnostics.DetachedRowsInspected} detached rows ");
        text.Append(CultureInfo.InvariantCulture, $"against {result.Diagnostics.EligibleTargetCount} eligible items ");
        text.Append(CultureInfo.InvariantCulture, $"for {result.Diagnostics.KnownUserCount} users.");
        text.Append('\n');

        text.Append(ready == 0
            ? "No writes available: nothing was uniquely and safely recoverable."
            : string.Create(CultureInfo.InvariantCulture, $"{ready} recoverable snapshots found. This build cannot apply them."));
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
}
