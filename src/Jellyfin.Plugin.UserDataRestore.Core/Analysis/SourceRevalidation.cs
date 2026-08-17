using System.Globalization;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// Re-checks the stranded rows that authorise a write, immediately before it is
/// made.
/// </summary>
/// <remarks>
/// <para><see cref="TargetRevalidation"/> asks whether the item being written to
/// is still the right one. This asks the other half of the same question: whether
/// the state being written is still the state the sentinel holds.</para>
/// <para>Detached rows are read once, at the top of the analysis, and their
/// fingerprints are carried into every planned write. Nothing this plugin does
/// touches them — that is a standing invariant and the reason a failed run is
/// recoverable — but this plugin is not the only thing that can. Jellyfin's own
/// <c>CleanupUserDataTask</c> deletes sentinel rows past a retention age, and
/// deleting another item can replace the row for the same
/// <c>(UserId, CustomDataKey)</c> with a newer snapshot. Neither needs a library
/// scan, so the guard that abandons a run mid-rebuild does not cover either.</para>
/// <para>Both failures are silent in the worst way. A deleted source leaves the
/// run restoring an in-memory copy of state the server has finished with. A
/// replaced source is worse: the older snapshot lands on the target, and the next
/// run compares the newer source against it, calls the pair
/// <c>current_state_conflict</c>, and never restores the newer state at all. The
/// stale result becomes sticky.</para>
/// <para>So the sources are read again and matched fingerprint for fingerprint.
/// Anything short of exact agreement declines the write. The rows are still
/// untouched either way, so a fresh analysis — which will read whatever the
/// sentinel holds now — reconsiders the whole thing from scratch.</para>
/// </remarks>
public static class SourceRevalidation
{
    /// <summary>Reported when a write records no source at all.</summary>
    public const string NoSourceRecorded = "source_not_recorded";

    /// <summary>Reported when authorising rows have gone.</summary>
    public const string SourceGone = "source_row_gone";

    /// <summary>Reported when authorising rows were replaced by different ones.</summary>
    public const string SourceReplaced = "source_row_replaced";

    /// <summary>Reported when rows appeared under the authorising keys.</summary>
    public const string SourceAppeared = "source_row_appeared";

    /// <summary>
    /// Checks the rows authorising one write against the sentinel as it is now.
    /// </summary>
    /// <param name="expected">
    /// The fingerprints recorded when the write was planned, from
    /// <c>PlannedWrite.SourceFingerprints</c>.
    /// </param>
    /// <param name="live">
    /// The sentinel rows currently held for this write's user and keys.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the sources are unchanged, otherwise a short
    /// wire reason naming how they differ.
    /// </returns>
    public static string? Evaluate(IReadOnlyList<string> expected, IReadOnlyList<DetachedUserDataRow> live)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(live);

        // A write authorised by no recorded source would revalidate vacuously,
        // which is the one outcome this must never produce.
        if (expected.Count == 0)
        {
            return NoSourceRecorded;
        }

        var recorded = expected.ToHashSet(StringComparer.Ordinal);
        var current = live.Select(row => row.Fingerprint).ToHashSet(StringComparer.Ordinal);

        var missing = recorded.Count(fingerprint => !current.Contains(fingerprint));
        var unexpected = current.Count(fingerprint => !recorded.Contains(fingerprint));

        if (missing == 0 && unexpected == 0)
        {
            return null;
        }

        // Named apart because they mean different things to whoever reads the
        // ledger. Gone is cleanup having caught up with a row this run was about
        // to use. Replaced is a second deletion of the same title landing a newer
        // snapshot on top — the case where writing the older one would also poison
        // every later run.
        var reason = missing switch
        {
            0 => SourceAppeared,
            _ when unexpected > 0 => SourceReplaced,
            _ => SourceGone,
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{reason}:{missing} of {expected.Count} missing, {unexpected} unrecognised");
    }
}
