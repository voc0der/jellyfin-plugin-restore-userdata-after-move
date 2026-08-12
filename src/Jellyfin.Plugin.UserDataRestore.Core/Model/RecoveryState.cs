namespace Jellyfin.Plugin.UserDataRestore.Core.Model;

/// <summary>
/// The six user-state fields this plugin is willing to recover (DESIGN §7.4).
/// Stream indexes are deliberately absent: they are positional and may refer to
/// different streams after a media replacement (DESIGN §9.2).
/// </summary>
public sealed record RecoveryState
{
    /// <summary>The state a Jellyfin item has when nobody has touched it.</summary>
    public static readonly RecoveryState Default = new();

    /// <summary>Gets a value indicating whether the item is marked played.</summary>
    public bool Played { get; init; }

    /// <summary>Gets the number of completed plays.</summary>
    public int PlayCount { get; init; }

    /// <summary>Gets the resume position in ticks.</summary>
    public long PlaybackPositionTicks { get; init; }

    /// <summary>Gets a value indicating whether the item is a favorite.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Gets the last time the item was played, normalized to UTC.</summary>
    public DateTime? LastPlayedDate { get; init; }

    /// <summary>Gets the user rating, 0-10.</summary>
    public double? Rating { get; init; }

    /// <summary>
    /// Gets a value indicating whether every recoverable field holds its default
    /// value, meaning a restore would change nothing (DESIGN §7.4).
    /// </summary>
    public bool IsDefault =>
        !Played
        && PlayCount == 0
        && PlaybackPositionTicks == 0
        && !IsFavorite
        && LastPlayedDate is null
        && Rating is null;
}
