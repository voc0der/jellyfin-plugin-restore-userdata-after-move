namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// The operator-configured scope of an analysis run (DESIGN §6.1).
/// </summary>
public sealed record AnalysisOptions
{
    /// <summary>
    /// Gets the libraries whose items may be recovery targets. Empty means no
    /// target is eligible, which is the correct reading of "not configured yet".
    /// </summary>
    public IReadOnlyList<Guid> EligibleLibraryIds { get; init; } = [];

    /// <summary>Gets the final path prefixes a target must sit beneath.</summary>
    public IReadOnlyList<string> FinalPathPrefixes { get; init; } = [];

    /// <summary>
    /// Gets the comparison used for paths. Ordinal on Linux, ordinal
    /// case-insensitive on Windows and macOS.
    /// </summary>
    public StringComparison PathComparison { get; init; } = StringComparison.Ordinal;

    /// <summary>
    /// Gets a value indicating whether a target's media path must exist on disk.
    /// </summary>
    public bool RequirePathExists { get; init; } = true;

    /// <summary>
    /// Gets the clock used to reject impossible <c>LastPlayedDate</c> values.
    /// </summary>
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets a value indicating whether both library IDs and path prefixes are
    /// configured. Nothing is eligible until they are.
    /// </summary>
    public bool IsScopeConfigured => EligibleLibraryIds.Count > 0 && FinalPathPrefixes.Count > 0;
}
