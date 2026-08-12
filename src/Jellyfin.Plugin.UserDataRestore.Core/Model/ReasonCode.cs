namespace Jellyfin.Plugin.UserDataRestore.Core.Model;

/// <summary>
/// The one category every detached row and every collapsed candidate ends in
/// (DESIGN §7.6). A high no-match or ambiguity rate is a product result, not an
/// error to paper over with fallback matching.
/// </summary>
public enum ReasonCode
{
    /// <summary>Uniquely matched, sufficient evidence, target has no state: recoverable.</summary>
    Ready,

    /// <summary>The target already holds this state. A rerun is a no-op.</summary>
    AlreadyApplied,

    /// <summary>Every recoverable field is default, so restoring it would change nothing.</summary>
    SourceHasNoEffect,

    /// <summary>No surviving Jellyfin user has this <c>UserId</c>.</summary>
    UnknownUser,

    /// <summary>No current item reports this key.</summary>
    NoCurrentKeyMatch,

    /// <summary>More than one current item reports this key.</summary>
    AmbiguousCurrentKey,

    /// <summary>The only current item reporting this key is not a supported recovery target.</summary>
    UnsupportedCurrentItem,

    /// <summary>The only current item reporting this key sits outside the configured final paths.</summary>
    PathOutsideFinalScope,

    /// <summary>Unique match, but the identity evidence rule in DESIGN §7.3 is not met.</summary>
    InsufficientIdentityEvidence,

    /// <summary>Rows that collapsed onto one target disagree about the state.</summary>
    InconsistentSourceState,

    /// <summary>The row itself is not usable: bad key, negative counter, out-of-range rating, impossible date.</summary>
    InvalidSourceState,

    /// <summary>The target already has user state, and it is not the state being recovered.</summary>
    CurrentStateConflict,
}

/// <summary>
/// Wire names for <see cref="ReasonCode"/>.
/// </summary>
/// <remarks>
/// Spelled out rather than derived from the enum name so the plan schema cannot
/// change underneath an operator because someone renamed a C# member.
/// </remarks>
public static class ReasonCodes
{
    private static readonly Dictionary<ReasonCode, string> Names = new()
    {
        [ReasonCode.Ready] = "ready",
        [ReasonCode.AlreadyApplied] = "already_applied",
        [ReasonCode.SourceHasNoEffect] = "source_has_no_effect",
        [ReasonCode.UnknownUser] = "unknown_user",
        [ReasonCode.NoCurrentKeyMatch] = "no_current_key_match",
        [ReasonCode.AmbiguousCurrentKey] = "ambiguous_current_key",
        [ReasonCode.UnsupportedCurrentItem] = "unsupported_current_item",
        [ReasonCode.PathOutsideFinalScope] = "path_outside_final_scope",
        [ReasonCode.InsufficientIdentityEvidence] = "insufficient_identity_evidence",
        [ReasonCode.InconsistentSourceState] = "inconsistent_source_state",
        [ReasonCode.InvalidSourceState] = "invalid_source_state",
        [ReasonCode.CurrentStateConflict] = "current_state_conflict",
    };

    /// <summary>Gets every reason code, in reporting order.</summary>
    public static IReadOnlyList<ReasonCode> All { get; } = [.. Names.Keys];

    /// <summary>
    /// Maps a reason code to its stable wire name.
    /// </summary>
    /// <param name="code">The code to map.</param>
    /// <returns>The snake_case name used in plans, logs, and summaries.</returns>
    public static string ToWire(ReasonCode code) =>
        Names.TryGetValue(code, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(code));
}
