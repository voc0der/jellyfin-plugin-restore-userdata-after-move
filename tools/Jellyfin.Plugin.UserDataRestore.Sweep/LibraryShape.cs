namespace Jellyfin.Plugin.UserDataRestore.Sweep;

/// <summary>
/// The shape of a synthetic installation.
/// </summary>
/// <remarks>
/// <para>Every field is something a real library has. What is <em>not</em> a
/// parameter is how Jellyfin strands rows: that is taken from the live runs in
/// <c>evidence/</c>.</para>
/// <para>Provider coverage is assigned <b>per series</b>, not per episode.
/// Jellyfin derives an episode's key from its series' IMDb ID, so a series
/// without one loses every episode it has at once. Drawing per episode would
/// spread that risk evenly across thousands of independent coin flips, collapse
/// the variance, and quietly turn the simulation into a restatement of its own
/// parameters.</para>
/// </remarks>
public sealed record LibraryShape
{
    /// <summary>Gets the approximate number of current items (movies plus episodes).</summary>
    public int Titles { get; init; } = 2000;

    /// <summary>Gets the approximate fraction of items that are episodes.</summary>
    public double EpisodeShare { get; init; } = 0.7;

    /// <summary>
    /// Gets the mean episode count per series. Sizes are drawn from a geometric
    /// distribution around it, so a handful of long-running shows sit alongside
    /// many short ones — which is what makes item-weighted coverage an unreliable
    /// predictor.
    /// </summary>
    public double MeanEpisodesPerSeries { get; init; } = 18;

    /// <summary>Gets the fraction of movies, and of series, carrying an IMDb ID.</summary>
    public double ImdbCoverage { get; init; } = 0.9;

    /// <summary>Gets the fraction of movies, and of series, carrying a TMDb ID.</summary>
    public double TmdbCoverage { get; init; } = 0.9;

    /// <summary>Gets the number of users with viewing history.</summary>
    public int Users { get; init; } = 2;

    /// <summary>Gets the probability that a given user has stranded state for a given item.</summary>
    public double WatchedFraction { get; init; } = 0.4;

    /// <summary>
    /// Gets the number of times each item's file has moved. Each move leaves the
    /// removed item's GUID-keyed rows behind; provider-keyed rows are overwritten
    /// rather than duplicated (DESIGN §17.5).
    /// </summary>
    public int MovesPerTitle { get; init; } = 1;

    /// <summary>
    /// Gets the fraction of items that exist twice in the current catalog — a
    /// second copy elsewhere, or one still lingering at a vacated path.
    /// </summary>
    public double Duplication { get; init; }

    /// <summary>
    /// Gets the fraction of recoverable pairs whose current item already holds
    /// different user state, which the plugin refuses to overwrite (DESIGN §4.3).
    /// </summary>
    public double CurrentStateFraction { get; init; }

    /// <summary>Gets the seed. Every population is a pure function of it.</summary>
    public int Seed { get; init; } = 1;
}
