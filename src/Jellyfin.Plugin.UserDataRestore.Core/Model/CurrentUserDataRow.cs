namespace Jellyfin.Plugin.UserDataRestore.Core.Model;

/// <summary>
/// A live <c>UserData</c> row for a candidate <c>(UserId, ItemId)</c> pair.
/// </summary>
/// <remarks>
/// Read as a row rather than through <c>IUserDataManager</c> on purpose: the
/// manager synthesizes a default object when nothing is stored, and DESIGN §7.5
/// needs to distinguish "no state" from "explicitly unwatched".
/// </remarks>
public sealed record CurrentUserDataRow
{
    /// <summary>Gets the user the row belongs to.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Gets the item the row belongs to.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Gets the key this row was written under.</summary>
    public string? CustomDataKey { get; init; }

    /// <summary>Gets a value indicating whether the item is played.</summary>
    public bool Played { get; init; }

    /// <summary>Gets the play count.</summary>
    public int PlayCount { get; init; }

    /// <summary>Gets the resume position in ticks.</summary>
    public long PlaybackPositionTicks { get; init; }

    /// <summary>Gets a value indicating whether the item is a favorite.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Gets the last played timestamp.</summary>
    public DateTime? LastPlayedDate { get; init; }

    /// <summary>Gets the user rating.</summary>
    public double? Rating { get; init; }

    /// <summary>Gets the recoverable subset of this row.</summary>
    public RecoveryState State => new()
    {
        Played = Played,
        PlayCount = PlayCount,
        PlaybackPositionTicks = PlaybackPositionTicks,
        IsFavorite = IsFavorite,
        LastPlayedDate = DateTimeNormalization.ToUtc(LastPlayedDate),
        Rating = Rating,
    };
}
