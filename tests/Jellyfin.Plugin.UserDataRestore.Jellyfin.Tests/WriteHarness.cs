using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;
using Jellyfin.Plugin.UserDataRestore.Jellyfin;
using Jellyfin.Plugin.UserDataRestore.Tests.Fixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin.Tests;

/// <summary>
/// One planned write, wired to a real database and substituted host managers, so
/// a fault can be put anywhere along it.
/// </summary>
/// <remarks>
/// <para>The managers are substitutes and the database is real, which matches
/// where the risk is. What <see cref="PlannedWriteApplier"/> asks the managers is
/// simple and was never in doubt; what it asks the database is a query somebody
/// wrote, and the order it asks things in is the part that has gone wrong
/// repeatedly.</para>
/// <para><see cref="SaveUserData"/> is made to behave the way the server does —
/// the state becomes readable through the manager, and a row lands under every
/// key the item reports — because a harness whose save does nothing would let a
/// verification step pass by asserting against its own inaction.</para>
/// </remarks>
internal sealed class WriteHarness : IDisposable
{
    private readonly UserDataDatabase _database;
    private readonly FakeLibrary _library;

    private WriteHarness(FakeLibrary library, BaseItem item, Guid selectedLibraryId)
    {
        _database = UserDataDatabase.Create();
        _library = library;
        Item = item;

        User = new User("gap", "Default", "Default") { Id = UserId };
        UserManager = Substitute.For<IUserManager>();
        UserManager.GetUserById(UserId).Returns(User);

        UserDataManager = Substitute.For<IUserDataManager>();
        UserDataManager
            .When(manager => manager.SaveUserData(
                Arg.Any<User>(), Arg.Any<BaseItem>(), Arg.Any<UpdateUserItemDataDto>(), Arg.Any<UserDataSaveReason>()))
            .Do(call => Persist(call.Arg<UpdateUserItemDataDto>()));

        Options = new AnalysisOptions
        {
            EligibleLibraryIds = [selectedLibraryId],
            FinalPathPrefixes = ["/data/library/movies", "/data/library/tv"],
            PathComparison = StringComparison.Ordinal,
            RequirePathExists = false,
            NowUtc = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    public static Guid UserId { get; } = new("a7fb7734-0000-0000-0000-000000000001");

    public UserDataDatabase Database => _database;

    public BaseItem Item { get; }

    public User User { get; }

    public IUserManager UserManager { get; }

    public IUserDataManager UserDataManager { get; }

    public AnalysisOptions Options { get; }

    /// <summary>Set to make the save throw, as a save that races a shutdown does.</summary>
    public Exception? SaveThrows { get; set; }

    /// <summary>Set to make the manager report something other than what was written.</summary>
    public RecoveryState? ReadBackReturns { get; set; }

    /// <summary>Set to stop the save reaching storage, leaving only the cache.</summary>
    public bool SwallowPersistence { get; set; }

    public static WriteHarness ForMovie(Guid libraryId)
    {
        var library = FakeLibrary.Create();
        var movie = library.AddMovie("The Matrix", libraryId, new() { ["Imdb"] = "tt0133093", ["Tmdb"] = "603" });
        return new WriteHarness(library, movie, libraryId);
    }

    public ILibraryManager LibraryManager => _library.Manager;

    /// <summary>The write the analyzer would have produced for this item.</summary>
    public PlannedWrite Plan(params string[] sourceKeys)
    {
        var state = new RecoveryState
        {
            Played = true,
            PlayCount = 3,
            PlaybackPositionTicks = 12345,
            IsFavorite = true,
            LastPlayedDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Rating = 9,
        };

        var targetKeys = Item.GetUserDataKeys()
            .Where(key => !string.IsNullOrEmpty(key))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Whatever the sentinel holds under this target's keys right now is what
        // the analysis would have recorded, which is what makes a row added or
        // removed afterwards a change rather than a surprise.
        var sentinel = _database.DetachedFingerprints(UserId, targetKeys);

        return new PlannedWrite
        {
            UserId = UserId,
            ItemId = Item.Id,
            State = state,
            EvidenceRule = IdentityEvidenceRule.ImdbRule,
            SourceFingerprints = [.. sourceKeys.Select(key => _database.DetachedFingerprint(UserId, key))],
            SourceKeys = [.. sourceKeys.Order(StringComparer.Ordinal)],
            TargetKeys = targetKeys,
            SentinelFingerprints = sentinel,
        };
    }

    public Task<WriteResult> ApplyAsync(PlannedWrite write) =>
        new PlannedWriteApplier(
            UserManager,
            LibraryManager,
            new UserDataWriter(UserDataManager),
            _database.Reader,
            new LibraryItemCollector(LibraryManager, _ => true),
            Options,
            KeyOwnership.Build([new LibraryItemCollector(LibraryManager, _ => true).Snapshot(Item, Options.EligibleLibraryIds, false)]),
            NullLogger.Instance).ApplyAsync(write, CancellationToken.None);

    public void Dispose() => _database.Dispose();

    private void Persist(UpdateUserItemDataDto dto)
    {
        if (SaveThrows is { } failure)
        {
            throw failure;
        }

        var state = ReadBackReturns ?? new RecoveryState
        {
            Played = dto.Played ?? false,
            PlayCount = dto.PlayCount ?? 0,
            PlaybackPositionTicks = dto.PlaybackPositionTicks ?? 0,
            IsFavorite = dto.IsFavorite ?? false,
            LastPlayedDate = dto.LastPlayedDate,
            Rating = dto.Rating,
        };

        UserDataManager.GetUserData(Arg.Any<User>(), Arg.Any<BaseItem>()).Returns(new UserItemData
        {
            Played = state.Played,
            PlayCount = state.PlayCount,
            PlaybackPositionTicks = state.PlaybackPositionTicks,
            IsFavorite = state.IsFavorite,
            LastPlayedDate = state.LastPlayedDate,
            Rating = state.Rating,
            Key = "written",
        });

        if (SwallowPersistence)
        {
            return;
        }

        // The fan-out the server performs: one row per key the item reports.
        foreach (var key in Item.GetUserDataKeys().Distinct(StringComparer.Ordinal))
        {
            _database.AddCurrent(
                UserId,
                Item.Id,
                key,
                state.Played,
                state.PlayCount,
                state.PlaybackPositionTicks,
                state.IsFavorite,
                state.Rating);
        }
    }
}
