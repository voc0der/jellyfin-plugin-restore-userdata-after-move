using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin;

/// <summary>
/// The only path by which this plugin may change user state (DESIGN §4.1).
/// </summary>
/// <remarks>
/// <para>Every write goes through <see cref="IUserDataManager"/>. The EF context
/// this plugin opens is query-only and must stay that way: the manager owns key
/// fan-out, change notification, and whatever else Jellyfin does around a save,
/// and a direct row write would skip all of it.</para>
/// <para>Only the six recoverable fields are set. Stream indexes are deliberately
/// left alone (DESIGN §9.2), and nothing here writes a field the plan did not
/// carry.</para>
/// </remarks>
public sealed class UserDataWriter(IUserDataManager userDataManager)
{
    private readonly IUserDataManager _userDataManager = userDataManager;

    /// <summary>
    /// Builds the DTO for a restore.
    /// </summary>
    /// <param name="state">The state to restore.</param>
    /// <param name="item">The item being restored onto.</param>
    /// <returns>The DTO to hand to the manager.</returns>
    /// <remarks>
    /// The key is required and comes from the item itself, never from the
    /// stranded row: the row's key is how the snapshot was found, not where it
    /// belongs now. Jellyfin fans a save out across the item's own keys.
    /// </remarks>
    public static UserItemDataDto ToDto(RecoveryState state, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(item);

        return new UserItemDataDto
        {
            Key = item.GetUserDataKeys()[0],
            ItemId = item.Id,
            Played = state.Played,
            PlayCount = state.PlayCount,
            PlaybackPositionTicks = state.PlaybackPositionTicks,
            IsFavorite = state.IsFavorite,
            LastPlayedDate = state.LastPlayedDate,
            Rating = state.Rating,
        };
    }

    /// <summary>
    /// Reads back the state the server currently holds for a pair.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item.</param>
    /// <returns>The state as the manager reports it.</returns>
    public RecoveryState Read(User user, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        var data = _userDataManager.GetUserData(user, item);
        if (data is null)
        {
            return RecoveryState.Default;
        }

        return new RecoveryState
        {
            Played = data.Played,
            PlayCount = data.PlayCount,
            PlaybackPositionTicks = data.PlaybackPositionTicks,
            IsFavorite = data.IsFavorite,
            LastPlayedDate = data.LastPlayedDate,
            Rating = data.Rating,
        };
    }
}
