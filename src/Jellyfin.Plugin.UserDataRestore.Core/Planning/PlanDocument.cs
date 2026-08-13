using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UserDataRestore.Core.Planning;

/// <summary>
/// The recovery plan (DESIGN §8): the immutable, reviewable artifact an analysis
/// run produces.
/// </summary>
/// <remarks>
/// The analysis writes it and the apply task reads it, but the plan is not
/// authority to write: apply re-runs the whole analysis and reconciles against
/// this document, so what the plan really carries is a record of what was true
/// when it was made.
/// </remarks>
public sealed record PlanDocument
{
    /// <summary>The schema version of plans this build writes.</summary>
    public const string CurrentSchemaVersion = "1";

    /// <summary>Gets the plan schema version.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets the plugin version that produced the plan.</summary>
    [JsonPropertyName("pluginVersion")]
    public required string PluginVersion { get; init; }

    /// <summary>Gets the Jellyfin version this build was compiled against.</summary>
    [JsonPropertyName("targetJellyfinVersion")]
    public required string TargetJellyfinVersion { get; init; }

    /// <summary>
    /// Gets the exact Jellyfin NuGet package this build compiled against.
    /// </summary>
    /// <remarks>
    /// Recorded separately from <see cref="TargetJellyfinVersion"/> because a
    /// prerelease server reports the stable version number: a plan produced by an
    /// RC5-built plugin and one produced by a stable-built plugin are otherwise
    /// indistinguishable after the fact.
    /// </remarks>
    [JsonPropertyName("builtAgainstJellyfinPackage")]
    public required string BuiltAgainstJellyfinPackage { get; init; }

    /// <summary>Gets the manifest ABI this build declares.</summary>
    [JsonPropertyName("targetAbi")]
    public required string TargetAbi { get; init; }

    /// <summary>Gets the server's identity, so a plan cannot be applied on another server.</summary>
    [JsonPropertyName("serverId")]
    public required string ServerId { get; init; }

    /// <summary>Gets the exact running server version.</summary>
    [JsonPropertyName("serverVersion")]
    public required string ServerVersion { get; init; }

    /// <summary>Gets when the plan was created.</summary>
    [JsonPropertyName("createdUtc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Gets the configured library IDs.</summary>
    [JsonPropertyName("configuredLibraryIds")]
    public IReadOnlyList<string> ConfiguredLibraryIds { get; init; } = [];

    /// <summary>Gets the configured final path prefixes.</summary>
    [JsonPropertyName("finalPathPrefixes")]
    public IReadOnlyList<string> FinalPathPrefixes { get; init; } = [];

    /// <summary>Gets a value indicating whether this build can apply anything. Always false in Milestone 1.</summary>
    [JsonPropertyName("applySupported")]
    public bool ApplySupported { get; init; }

    /// <summary>Gets the summary counts.</summary>
    [JsonPropertyName("summary")]
    public required PlanSummary Summary { get; init; }

    /// <summary>Gets the before/after evidence that analysis wrote nothing.</summary>
    [JsonPropertyName("readOnlyProof")]
    public required PlanReadOnlyProof ReadOnlyProof { get; init; }

    /// <summary>Gets one entry per detached row.</summary>
    [JsonPropertyName("sourceRows")]
    public IReadOnlyList<PlanSourceRow> SourceRows { get; init; } = [];

    /// <summary>Gets one entry per collapsed candidate.</summary>
    [JsonPropertyName("candidates")]
    public IReadOnlyList<PlanCandidate> Candidates { get; init; } = [];

    /// <summary>Gets the exact ordered list of writes a later apply task would perform.</summary>
    [JsonPropertyName("writes")]
    public IReadOnlyList<PlanWrite> Writes { get; init; } = [];

    /// <summary>
    /// Gets the canonical SHA-256 plan ID. Excluded from its own computation; see
    /// <see cref="PlanCanonicalizer"/>.
    /// </summary>
    [JsonPropertyName("planId")]
    public string PlanId { get; init; } = string.Empty;
}

/// <summary>Summary counts for the whole run.</summary>
public sealed record PlanSummary
{
    /// <summary>Gets detached rows per reason code, keyed by wire name.</summary>
    [JsonPropertyName("rowCounts")]
    public required IReadOnlyDictionary<string, int> RowCounts { get; init; }

    /// <summary>Gets candidates per reason code, keyed by wire name.</summary>
    [JsonPropertyName("candidateCounts")]
    public required IReadOnlyDictionary<string, int> CandidateCounts { get; init; }

    /// <summary>Gets the number of planned writes.</summary>
    [JsonPropertyName("writeCount")]
    public int WriteCount { get; init; }

    /// <summary>Gets the run-level observations.</summary>
    [JsonPropertyName("diagnostics")]
    public required IReadOnlyDictionary<string, long> Diagnostics { get; init; }
}

/// <summary>Before/after fingerprints of the whole <c>UserData</c> table.</summary>
public sealed record PlanReadOnlyProof
{
    /// <summary>Gets the row count before analysis.</summary>
    [JsonPropertyName("rowCountBefore")]
    public required int RowCountBefore { get; init; }

    /// <summary>Gets the row count after analysis.</summary>
    [JsonPropertyName("rowCountAfter")]
    public required int RowCountAfter { get; init; }

    /// <summary>Gets the table digest before analysis.</summary>
    [JsonPropertyName("digestBefore")]
    public required string DigestBefore { get; init; }

    /// <summary>Gets the table digest after analysis.</summary>
    [JsonPropertyName("digestAfter")]
    public required string DigestAfter { get; init; }

    /// <summary>Gets a value indicating whether the table is byte-for-byte unchanged.</summary>
    [JsonPropertyName("unchanged")]
    public required bool Unchanged { get; init; }
}

/// <summary>One detached row's disposition.</summary>
public sealed record PlanSourceRow
{
    /// <summary>Gets the user ID.</summary>
    [JsonPropertyName("userId")]
    public required string UserId { get; init; }

    /// <summary>Gets the key exactly as stored.</summary>
    [JsonPropertyName("customDataKey")]
    public string? CustomDataKey { get; init; }

    /// <summary>Gets the retention stamp.</summary>
    [JsonPropertyName("retentionDate")]
    public string? RetentionDate { get; init; }

    /// <summary>Gets the row's state.</summary>
    [JsonPropertyName("state")]
    public required PlanState State { get; init; }

    /// <summary>Gets the fields recorded but not recovered in v1.</summary>
    [JsonPropertyName("reportOnly")]
    public required PlanReportOnlyFields ReportOnly { get; init; }

    /// <summary>Gets the stable fingerprint the apply preflight will re-check.</summary>
    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }

    /// <summary>Gets the classification.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>Gets the specific validation failure, when there was one.</summary>
    [JsonPropertyName("violation")]
    public string? Violation { get; init; }

    /// <summary>Gets the uniquely matched target, when there was one.</summary>
    [JsonPropertyName("targetItemId")]
    public string? TargetItemId { get; init; }

    /// <summary>Gets the identity evidence the key carries.</summary>
    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }

    /// <summary>Gets the provider that produced the key, when known.</summary>
    [JsonPropertyName("evidenceProvider")]
    public string? EvidenceProvider { get; init; }

    /// <summary>Gets a value indicating whether the key is series-GUID derived.</summary>
    [JsonPropertyName("seriesGuidEpisodeDerived")]
    public bool SeriesGuidEpisodeDerived { get; init; }

    /// <summary>Gets every current item that reported this key.</summary>
    [JsonPropertyName("matches")]
    public IReadOnlyList<PlanMatch> Matches { get; init; } = [];
}

/// <summary>A current item that reported a detached key.</summary>
public sealed record PlanMatch
{
    /// <summary>Gets the item ID.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Gets the item kind.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Gets why the item is not an eligible target, or <c>none</c>.</summary>
    [JsonPropertyName("exclusion")]
    public required string Exclusion { get; init; }

    /// <summary>Gets the item name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Gets the item path.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

/// <summary>One collapsed candidate.</summary>
public sealed record PlanCandidate
{
    /// <summary>Gets the user ID.</summary>
    [JsonPropertyName("userId")]
    public required string UserId { get; init; }

    /// <summary>Gets the target item ID.</summary>
    [JsonPropertyName("targetItemId")]
    public required string TargetItemId { get; init; }

    /// <summary>Gets the target kind.</summary>
    [JsonPropertyName("targetKind")]
    public required string TargetKind { get; init; }

    /// <summary>Gets the target name.</summary>
    [JsonPropertyName("targetName")]
    public string? TargetName { get; init; }

    /// <summary>Gets the target path.</summary>
    [JsonPropertyName("targetPath")]
    public string? TargetPath { get; init; }

    /// <summary>Gets the libraries the target belongs to.</summary>
    [JsonPropertyName("targetLibraryIds")]
    public IReadOnlyList<string> TargetLibraryIds { get; init; } = [];

    /// <summary>Gets the target's complete current key set.</summary>
    [JsonPropertyName("targetKeys")]
    public IReadOnlyList<string> TargetKeys { get; init; } = [];

    /// <summary>Gets the detached keys that collapsed onto this candidate.</summary>
    [JsonPropertyName("contributingKeys")]
    public IReadOnlyList<PlanContributingKey> ContributingKeys { get; init; } = [];

    /// <summary>Gets the state that would be restored.</summary>
    [JsonPropertyName("recoveredState")]
    public PlanState? RecoveredState { get; init; }

    /// <summary>Gets which identity-evidence case admitted the candidate.</summary>
    [JsonPropertyName("evidenceRule")]
    public required string EvidenceRule { get; init; }

    /// <summary>Gets the number of live rows the target already has for this user.</summary>
    [JsonPropertyName("currentRowCount")]
    public int CurrentRowCount { get; init; }

    /// <summary>Gets the target's existing state, when it has any.</summary>
    [JsonPropertyName("currentState")]
    public PlanState? CurrentState { get; init; }

    /// <summary>Gets the classification.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>A detached key that contributed to a candidate.</summary>
public sealed record PlanContributingKey
{
    /// <summary>Gets the key.</summary>
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    /// <summary>Gets the evidence it carries.</summary>
    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }

    /// <summary>Gets the provider that produced it, when known.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>Gets the source row fingerprint.</summary>
    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }
}

/// <summary>One planned write.</summary>
public sealed record PlanWrite
{
    /// <summary>Gets the user to write for.</summary>
    [JsonPropertyName("userId")]
    public required string UserId { get; init; }

    /// <summary>Gets the item to write to.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Gets the state to write.</summary>
    [JsonPropertyName("state")]
    public required PlanState State { get; init; }

    /// <summary>Gets the identity-evidence case that admitted it.</summary>
    [JsonPropertyName("evidenceRule")]
    public required string EvidenceRule { get; init; }

    /// <summary>Gets the fingerprints of the source rows behind it.</summary>
    [JsonPropertyName("sourceFingerprints")]
    public IReadOnlyList<string> SourceFingerprints { get; init; } = [];
}

/// <summary>The six recoverable fields, serialized.</summary>
public sealed record PlanState
{
    /// <summary>Gets the played flag.</summary>
    [JsonPropertyName("played")]
    public bool Played { get; init; }

    /// <summary>Gets the play count.</summary>
    [JsonPropertyName("playCount")]
    public int PlayCount { get; init; }

    /// <summary>Gets the resume position.</summary>
    [JsonPropertyName("playbackPositionTicks")]
    public long PlaybackPositionTicks { get; init; }

    /// <summary>Gets the favorite flag.</summary>
    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; init; }

    /// <summary>Gets the last played timestamp.</summary>
    [JsonPropertyName("lastPlayedDate")]
    public string? LastPlayedDate { get; init; }

    /// <summary>Gets the rating.</summary>
    [JsonPropertyName("rating")]
    public double? Rating { get; init; }
}

/// <summary>Fields recorded for review but never restored in v1 (DESIGN §9.2).</summary>
public sealed record PlanReportOnlyFields
{
    /// <summary>Gets the like flag.</summary>
    [JsonPropertyName("likes")]
    public bool? Likes { get; init; }

    /// <summary>Gets the selected audio stream index.</summary>
    [JsonPropertyName("audioStreamIndex")]
    public int? AudioStreamIndex { get; init; }

    /// <summary>Gets the selected subtitle stream index.</summary>
    [JsonPropertyName("subtitleStreamIndex")]
    public int? SubtitleStreamIndex { get; init; }
}
