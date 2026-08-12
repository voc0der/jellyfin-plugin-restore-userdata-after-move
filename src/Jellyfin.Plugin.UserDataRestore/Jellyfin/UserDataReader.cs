using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Verification;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin;

/// <summary>
/// Every read this plugin performs against <c>UserData</c> (DESIGN §7.1, §7.5).
/// </summary>
/// <remarks>
/// <para>Query-only, by invariant (DESIGN §4.1). Every query uses
/// <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/>,
/// projects into plain records, materializes, and disposes the context before the
/// caller does anything else. No context is held across library enumeration or
/// file writing, and no transaction is opened.</para>
/// <para>The context comes from the host's own factory, so the configured
/// provider — whatever it is — applies. There is no SQLite-specific SQL here.</para>
/// </remarks>
public sealed class UserDataReader(IDbContextFactory<JellyfinDbContext> dbFactory)
{
    /// <summary>
    /// The item ID Jellyfin parks detached rows on.
    /// </summary>
    /// <remarks>
    /// Defined locally rather than taken from <c>Jellyfin.Server.Implementations</c>
    /// (DESIGN §5.2): one constant is not worth a dependency on a host assembly
    /// the plugin would otherwise never touch.
    /// </remarks>
    public static readonly Guid SentinelItemId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>How many IDs go into one <c>IN</c> clause.</summary>
    private const int BatchSize = 200;

    private readonly IDbContextFactory<JellyfinDbContext> _dbFactory = dbFactory;

    /// <summary>
    /// Verifies the host's <c>UserData</c> model before anything reads it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the model has been accepted.</returns>
    public async Task EnsureModelCompatibleAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        DatabaseModelGate.EnsureCompatible(db);
    }

    /// <summary>
    /// Fingerprints every row in the table, live and detached.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The whole-table fingerprint.</returns>
    public async Task<UserDataFingerprint> FingerprintAsync(CancellationToken cancellationToken)
    {
        var builder = new UserDataFingerprintBuilder();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Streamed rather than materialized: the point of this pass is to touch
        // every row, and on a large install that is the one query worth not
        // holding in memory.
        var rows = db.UserData
            .AsNoTracking()
            .Select(row => new
            {
                row.ItemId,
                row.UserId,
                row.CustomDataKey,
                row.Played,
                row.PlayCount,
                row.PlaybackPositionTicks,
                row.IsFavorite,
                row.LastPlayedDate,
                row.Rating,
                row.Likes,
                row.AudioStreamIndex,
                row.SubtitleStreamIndex,
                row.RetentionDate,
            })
            .AsAsyncEnumerable();

        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            builder.Add(
                row.ItemId,
                row.UserId,
                row.CustomDataKey,
                row.Played,
                row.PlayCount,
                row.PlaybackPositionTicks,
                row.IsFavorite,
                row.LastPlayedDate,
                row.Rating,
                row.Likes,
                row.AudioStreamIndex,
                row.SubtitleStreamIndex,
                row.RetentionDate);
        }

        return builder.Build();
    }

    /// <summary>
    /// Reads every detached row (DESIGN §7.1).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The detached rows.</returns>
    public async Task<IReadOnlyList<DetachedUserDataRow>> ReadDetachedAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.UserData
            .AsNoTracking()
            .Where(row => row.ItemId == SentinelItemId)
            .Select(row => new
            {
                row.UserId,
                row.CustomDataKey,
                row.RetentionDate,
                row.Played,
                row.PlayCount,
                row.PlaybackPositionTicks,
                row.IsFavorite,
                row.LastPlayedDate,
                row.Rating,
                row.Likes,
                row.AudioStreamIndex,
                row.SubtitleStreamIndex,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => new DetachedUserDataRow
        {
            UserId = row.UserId,
            CustomDataKey = row.CustomDataKey,
            RetentionDate = DateTimeNormalization.ToUtc(row.RetentionDate),
            Played = row.Played,
            PlayCount = row.PlayCount,
            PlaybackPositionTicks = row.PlaybackPositionTicks,
            IsFavorite = row.IsFavorite,
            LastPlayedDate = DateTimeNormalization.ToUtc(row.LastPlayedDate),
            Rating = row.Rating,
            Likes = row.Likes,
            AudioStreamIndex = row.AudioStreamIndex,
            SubtitleStreamIndex = row.SubtitleStreamIndex,
        })];
    }

    /// <summary>
    /// Reads live rows for candidate pairs, in batches (DESIGN §7.5).
    /// </summary>
    /// <param name="pairs">The <c>(user, item)</c> pairs to inspect.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The live rows for exactly those pairs.</returns>
    public async Task<IReadOnlyList<CurrentUserDataRow>> ReadCurrentAsync(
        IReadOnlyList<(Guid UserId, Guid ItemId)> pairs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        if (pairs.Count == 0)
        {
            return [];
        }

        var wanted = pairs.ToHashSet();
        var userIds = pairs.Select(pair => pair.UserId).Distinct().ToArray();
        var itemIds = pairs.Select(pair => pair.ItemId).Distinct().ToArray();
        var results = new List<CurrentUserDataRow>();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Batched on both axes to stay well clear of any provider's parameter
        // ceiling. The query returns the cross product of the two batches, so the
        // requested pairs are selected again in memory.
        foreach (var userBatch in userIds.Chunk(BatchSize))
        {
            foreach (var itemBatch in itemIds.Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rows = await db.UserData
                    .AsNoTracking()
                    .Where(row => userBatch.Contains(row.UserId) && itemBatch.Contains(row.ItemId))
                    .Select(row => new
                    {
                        row.UserId,
                        row.ItemId,
                        row.CustomDataKey,
                        row.Played,
                        row.PlayCount,
                        row.PlaybackPositionTicks,
                        row.IsFavorite,
                        row.LastPlayedDate,
                        row.Rating,
                    })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var row in rows)
                {
                    if (!wanted.Contains((row.UserId, row.ItemId)))
                    {
                        continue;
                    }

                    results.Add(new CurrentUserDataRow
                    {
                        UserId = row.UserId,
                        ItemId = row.ItemId,
                        CustomDataKey = row.CustomDataKey,
                        Played = row.Played,
                        PlayCount = row.PlayCount,
                        PlaybackPositionTicks = row.PlaybackPositionTicks,
                        IsFavorite = row.IsFavorite,
                        LastPlayedDate = DateTimeNormalization.ToUtc(row.LastPlayedDate),
                        Rating = row.Rating,
                    });
                }
            }
        }

        return results;
    }
}
