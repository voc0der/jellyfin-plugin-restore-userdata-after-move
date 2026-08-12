namespace Jellyfin.Plugin.UserDataRestore.Sweep;

/// <summary>
/// The shape of a synthetic installation.
/// </summary>
/// <remarks>
/// <para>Every field here is something a real library has and can be measured
/// about itself. The sweep exists because the analyzer's outcome is a function of
/// these and nothing else — so rather than guess one setting and call the answer
/// evidence, vary each and publish the response.</para>
/// <para>What is <em>not</em> a parameter is how Jellyfin strands rows. That is
/// taken from the live runs in <c>evidence/</c>: a moved title keeps one detached
/// row per (user, provider key) holding its last snapshot, and each removed item
/// leaves GUID-keyed rows behind that nothing can map.</para>
/// </remarks>
public sealed record LibraryShape
{
    /// <summary>Gets the number of distinct titles in the library.</summary>
    public int Titles { get; init; } = 2000;

    /// <summary>Gets the fraction of titles that are episodes rather than movies.</summary>
    public double EpisodeShare { get; init; } = 0.7;

    /// <summary>Gets the fraction of titles carrying an IMDb ID (the series' ID, for episodes).</summary>
    public double ImdbCoverage { get; init; } = 0.9;

    /// <summary>Gets the fraction of titles carrying a TMDb ID.</summary>
    public double TmdbCoverage { get; init; } = 0.9;

    /// <summary>Gets the number of users with viewing history.</summary>
    public int Users { get; init; } = 2;

    /// <summary>Gets the probability that a given user has stranded state for a given title.</summary>
    public double WatchedFraction { get; init; } = 0.4;

    /// <summary>
    /// Gets the number of times each title's file has moved. Each move leaves the
    /// removed item's GUID-keyed rows behind; the provider-keyed rows are
    /// overwritten rather than duplicated (DESIGN §17.5).
    /// </summary>
    public int MovesPerTitle { get; init; } = 1;

    /// <summary>
    /// Gets the fraction of titles that exist twice in the current catalog — a
    /// second copy in an unconfigured library, or one still lingering at a vacated
    /// path. Both current items report the same provider keys, so the stranded row
    /// cannot be attributed to either.
    /// </summary>
    public double Duplication { get; init; }

    /// <summary>
    /// Gets the fraction of recoverable pairs whose current item already holds
    /// different user state, which the plugin refuses to overwrite (DESIGN §4.3).
    /// </summary>
    public double CurrentStateFraction { get; init; }

    /// <summary>Gets the seed, so every reported figure is reproducible.</summary>
    public int Seed { get; init; } = 20260812;
}
