namespace Jellyfin.Plugin.UserDataRestore.Jellyfin.Tests;

/// <summary>
/// Every read this plugin makes, run against the host's real model (DESIGN §12.2).
/// </summary>
/// <remarks>
/// These exist because of a near miss. The per-write source revalidation added a
/// keyed query — <c>keys.Contains(row.CustomDataKey)</c> over the sentinel — and
/// it shipped twice, in two releases, without anything ever executing it. Had the
/// provider been unable to translate that expression, the throw would have landed
/// inside the write loop: every restore failing before its save, the run stopping
/// at the first one, and nothing in the plugin's own account of itself pointing at
/// the query. It happens to translate. Nothing established that until afterwards,
/// which is the gap these close rather than the bug.
/// </remarks>
public class UserDataReaderTests
{
    private static readonly Guid UserA = new("a7fb7734-0000-0000-0000-000000000001");
    private static readonly Guid UserB = new("18fe613b-0000-0000-0000-000000000002");
    private static readonly Guid MovieId = new("74f9957e-b453-7dbb-b614-d528834acab2");
    private static readonly Guid EpisodeId = new("6d1e5aa0-0000-0000-0000-00000000000b");

    [Fact]
    public async Task DetachedRowsAreReadWithEveryFieldTheAnalysisNeeds()
    {
        using var database = UserDataDatabase.Create();
        database.AddDetached(UserA, "tt0133093", playCount: 7, rating: 8.5);
        database.AddCurrent(UserA, MovieId, "tt0133093");

        var rows = await database.Reader.ReadDetachedAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(UserA, row.UserId);
        Assert.Equal("tt0133093", row.CustomDataKey);
        Assert.Equal(7, row.PlayCount);
        Assert.Equal(8.5, row.Rating);
        Assert.True(row.Played);
        Assert.True(row.IsFavorite);

        // Normalized on the way out, because a stamp read back as Unspecified
        // compares unequal to the same instant read as Utc, and the corroboration
        // rule turns on stamps being equal.
        Assert.Equal(DateTimeKind.Utc, row.RetentionDate!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, row.LastPlayedDate!.Value.Kind);
    }

    [Fact]
    public async Task TheKeyedRereadAsksTheProviderSomethingItCanTranslate()
    {
        // The query the per-write source check depends on, executed rather than
        // assumed. A provider that cannot translate the key set throws here, at
        // build time for the reviewer, instead of on a server mid-restore.
        using var database = UserDataDatabase.Create();
        database.AddDetached(UserA, "tt0133093", playCount: 1);
        database.AddDetached(UserA, "603", playCount: 2);
        database.AddDetached(UserA, "unrelated", playCount: 3);

        var rows = await database.Reader.ReadDetachedAsync(
            UserA,
            ["tt0133093", "603", MovieId.ToString("D")],
            CancellationToken.None);

        Assert.Equal(["603", "tt0133093"], rows.Select(row => row.CustomDataKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheKeyedRereadNeverCrossesToAnotherUser()
    {
        // Two users' rows sit under the same key — that is what a shared library
        // looks like — and a write authorised for one of them must not be
        // revalidated against the other's.
        using var database = UserDataDatabase.Create();
        database.AddDetached(UserA, "tt0133093", playCount: 1);
        database.AddDetached(UserB, "tt0133093", playCount: 99);

        var rows = await database.Reader.ReadDetachedAsync(UserA, ["tt0133093"], CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(UserA, row.UserId);
        Assert.Equal(1, row.PlayCount);
    }

    [Fact]
    public async Task TheKeyedRereadIgnoresLiveRowsUnderTheSameKey()
    {
        // Only the sentinel is a source. A live row for a real item under the same
        // key is the state of some other item, and reading it as a source would
        // make every write look superseded.
        using var database = UserDataDatabase.Create();
        database.AddDetached(UserA, "tt0133093", playCount: 1);
        database.AddCurrent(UserA, MovieId, "tt0133093", playCount: 42);

        var rows = await database.Reader.ReadDetachedAsync(UserA, ["tt0133093"], CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.PlayCount);
    }

    [Fact]
    public async Task AnEmptyKeySetAsksTheDatabaseNothing()
    {
        using var database = UserDataDatabase.Create();
        database.AddDetached(UserA, "tt0133093");

        Assert.Empty(await database.Reader.ReadDetachedAsync(UserA, [], CancellationToken.None));
    }

    [Fact]
    public async Task RowExistenceIsAnsweredForThePairAndNotItsNeighbours()
    {
        // The distinction IUserDataManager cannot draw: a row holding nothing but
        // defaults is an explicit clear, and reading it as "no row" is how a
        // scheduled run would put back what a user just removed.
        using var database = UserDataDatabase.Create();
        database.AddCurrent(UserA, MovieId, "tt0133093", played: false, playCount: 0, ticks: 0, favorite: false, rating: null);

        var reader = database.Reader;

        Assert.True(await reader.RowExistsAsync(UserA, MovieId, CancellationToken.None));
        Assert.False(await reader.RowExistsAsync(UserB, MovieId, CancellationToken.None));
        Assert.False(await reader.RowExistsAsync(UserA, EpisodeId, CancellationToken.None));
    }

    [Fact]
    public async Task CurrentRowsComeBackForTheRequestedPairsAndNoOthers()
    {
        // Batched on both axes, so the query returns the cross product of the two
        // batches and the wanted pairs are selected again in memory. This is the
        // test that the second selection actually happens: (UserB, MovieId) is in
        // the cross product and was never asked for.
        using var database = UserDataDatabase.Create();
        database.AddCurrent(UserA, MovieId, "tt0133093", playCount: 1);
        database.AddCurrent(UserB, EpisodeId, "tt0903747001001", playCount: 2);
        database.AddCurrent(UserB, MovieId, "tt0133093", playCount: 3);

        var rows = await database.Reader.ReadCurrentAsync(
            [(UserA, MovieId), (UserB, EpisodeId)],
            CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.UserId.Equals(UserA) && row.ItemId.Equals(MovieId));
        Assert.Contains(rows, row => row.UserId.Equals(UserB) && row.ItemId.Equals(EpisodeId));
        Assert.DoesNotContain(rows, row => row.UserId.Equals(UserB) && row.ItemId.Equals(MovieId));
    }

    [Fact]
    public async Task MorePairsThanOneBatchStillComeBackWhole()
    {
        // BatchSize is 200 on both axes to stay clear of a provider's parameter
        // ceiling. A run on a real library goes past it, and nothing until now had
        // ever made this chunk.
        using var database = UserDataDatabase.Create();
        var pairs = new List<(Guid UserId, Guid ItemId)>();
        for (var i = 0; i < 450; i++)
        {
            var itemId = new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
            database.AddCurrent(UserA, itemId, "key-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            pairs.Add((UserA, itemId));
        }

        var rows = await database.Reader.ReadCurrentAsync(pairs, CancellationToken.None);

        Assert.Equal(450, rows.Count);
        Assert.Equal(450, rows.Select(row => row.ItemId).Distinct().Count());
    }

    [Fact]
    public async Task TheWholeTableFingerprintsAndReadingItChangesNothing()
    {
        // The query-only invariant, checked the way the plugin itself checks a
        // run: fingerprint, do the reads, fingerprint again. Analysis that
        // mutates a single stranded row destroys the only surviving copy of it.
        using var database = UserDataDatabase.Create();
        database.AddDetached(UserA, "tt0133093");
        database.AddDetached(UserB, "603");
        database.AddCurrent(UserA, MovieId, "tt0133093");

        var reader = database.Reader;
        var before = await reader.FingerprintAsync(CancellationToken.None);

        await reader.ReadDetachedAsync(CancellationToken.None);
        await reader.ReadDetachedAsync(UserA, ["tt0133093"], CancellationToken.None);
        await reader.ReadCurrentAsync([(UserA, MovieId)], CancellationToken.None);
        await reader.RowExistsAsync(UserA, MovieId, CancellationToken.None);

        var after = await reader.FingerprintAsync(CancellationToken.None);

        Assert.Equal(3, before.RowCount);
        Assert.Equal(before.Digest, after.Digest);
        Assert.Equal(3, database.RowCount());
    }

    [Fact]
    public async Task TheModelGateAcceptsTheHostThisWasBuiltAgainst()
    {
        // The check that runs before any other read. If it rejects the very model
        // the plugin was compiled against, every run refuses on every server.
        using var database = UserDataDatabase.Create();

        await database.Reader.EnsureModelCompatibleAsync(CancellationToken.None);
    }
}
