using Jellyfin.Plugin.UserDataRestore.Core.Analysis;

namespace Jellyfin.Plugin.UserDataRestore.Core.Planning;

/// <summary>
/// What became of one planned write.
/// </summary>
/// <remarks>
/// The distinction that matters is not success versus failure, it is what the
/// run can still prove about the item afterwards. <see cref="Skipped"/> and
/// <see cref="Failed"/> both leave the target provably untouched;
/// <see cref="Uncertain"/> does not, and that is the boundary the run stops at.
/// </remarks>
public enum WriteOutcome
{
    /// <summary>The run ended before reaching this write. The target was never touched.</summary>
    NotAttempted,

    /// <summary>The state was written and read back equal.</summary>
    Restored,

    /// <summary>A guard declined before anything was written. The target is untouched.</summary>
    Skipped,

    /// <summary>Something threw before the save was attempted. The target is untouched.</summary>
    Failed,

    /// <summary>
    /// The save was attempted and its result is unknown: it threw — possibly after
    /// committing — or the state did not read back.
    /// </summary>
    Uncertain,
}

/// <summary>Wire names for <see cref="WriteOutcome"/>.</summary>
public static class WriteOutcomes
{
    /// <summary>Gets every outcome, so a summary can report the zeroes too.</summary>
    public static IReadOnlyList<WriteOutcome> All { get; } =
    [
        WriteOutcome.Restored,
        WriteOutcome.Skipped,
        WriteOutcome.Failed,
        WriteOutcome.Uncertain,
        WriteOutcome.NotAttempted,
    ];

    /// <summary>
    /// Renders an outcome for a plan.
    /// </summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>Its stable wire name.</returns>
    public static string ToWire(WriteOutcome outcome) => outcome switch
    {
        WriteOutcome.NotAttempted => "not_attempted",
        WriteOutcome.Restored => "restored",
        WriteOutcome.Skipped => "skipped",
        WriteOutcome.Failed => "failed",
        WriteOutcome.Uncertain => "uncertain",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}

/// <summary>
/// One planned write and what actually happened to it.
/// </summary>
/// <param name="Write">The write as the analysis planned it.</param>
/// <param name="Outcome">What became of it.</param>
/// <param name="Detail">
/// A short machine-readable reason, when there is one: the disqualification that
/// skipped it, or where certainty was lost. Null for a plain restore.
/// </param>
public readonly record struct WriteResult(PlannedWrite Write, WriteOutcome Outcome, string? Detail)
{
    /// <summary>
    /// Records a write the run never reached.
    /// </summary>
    /// <param name="write">The planned write.</param>
    /// <param name="reason">Why the run stopped short of it.</param>
    /// <returns>The result.</returns>
    public static WriteResult NotAttempted(PlannedWrite write, string reason) =>
        new(write, WriteOutcome.NotAttempted, reason);
}
