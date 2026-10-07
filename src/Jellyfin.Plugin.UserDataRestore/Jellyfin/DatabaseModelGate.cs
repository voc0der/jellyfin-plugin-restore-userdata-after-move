using System.Globalization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin;

/// <summary>
/// Checks that the running server's <c>UserData</c> model is the one this build
/// was written against (DESIGN §9.1 item 2, §11).
/// </summary>
/// <remarks>
/// <para>This is the only runtime compatibility check. Like every plugin, this
/// build runs on any server at or above its <c>targetAbi</c> (DESIGN §17.3), and
/// a server's version number says nothing reliable about the entity this plugin
/// reads (12.0 RC5 reported <c>12.0.0</c> exactly as stable does). So check the
/// entity directly: ask the host's own EF model whether every column the
/// projection depends on is still there. A server that reshaped <c>UserData</c> is
/// refused whatever it calls itself, and one that did not is allowed.</para>
/// <para>This is a compatibility check, not an authenticity check. It cannot tell
/// two builds apart when they share a model, and it does not try to.</para>
/// </remarks>
public static class DatabaseModelGate
{
    /// <summary>
    /// The columns DESIGN §7.1 projects, plus the two the fingerprint covers.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredUserDataProperties =
    [
        nameof(UserData.ItemId),
        nameof(UserData.UserId),
        nameof(UserData.CustomDataKey),
        nameof(UserData.Played),
        nameof(UserData.PlayCount),
        nameof(UserData.PlaybackPositionTicks),
        nameof(UserData.IsFavorite),
        nameof(UserData.LastPlayedDate),
        nameof(UserData.Rating),
        nameof(UserData.Likes),
        nameof(UserData.AudioStreamIndex),
        nameof(UserData.SubtitleStreamIndex),
        nameof(UserData.RetentionDate),
    ];

    /// <summary>
    /// Throws unless the host's <c>UserData</c> entity carries every property this
    /// build reads.
    /// </summary>
    /// <param name="db">A host-provided context.</param>
    /// <exception cref="InvalidOperationException">The model is not the expected shape.</exception>
    public static void EnsureCompatible(JellyfinDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        var entity = db.Model.FindEntityType(typeof(UserData))
            ?? throw new InvalidOperationException(
                "The running server's database model has no UserData entity. This plugin build cannot read it safely; refusing to run.");

        var present = entity.GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var missing = RequiredUserDataProperties.Where(name => !present.Contains(name)).ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The running server's UserData model is missing {string.Join(", ", missing)}, which this plugin build reads. Install the build matching this server; refusing to run."));
        }
    }
}
