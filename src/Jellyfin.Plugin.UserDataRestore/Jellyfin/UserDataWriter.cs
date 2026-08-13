using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

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
    public static UpdateUserItemDataDto ToDto(RecoveryState state, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(item);

        return new UpdateUserItemDataDto
        {
            Played = state.Played,
            PlayCount = state.PlayCount,
            PlaybackPositionTicks = state.PlaybackPositionTicks,
            IsFavorite = state.IsFavorite,
            LastPlayedDate = state.LastPlayedDate,
            Rating = state.Rating,
        };
    }

    /// <summary>
    /// Restores state onto an item, through the manager and nothing else.
    /// </summary>
    /// <param name="user">The user to restore for.</param>
    /// <param name="item">The target item.</param>
    /// <param name="state">The state to write.</param>
    /// <remarks>
    /// Absolute values, never toggles or increments (DESIGN §9.2): a retry of the
    /// same write has to land on the same result. The partial-update DTO leaves
    /// the current audio and subtitle selections alone.
    /// </remarks>
    public void Save(User user, BaseItem item, RecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(state);

        _userDataManager.SaveUserData(user, item, ToDto(state, item), UserDataSaveReason.UpdateUserData);
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
