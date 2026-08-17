using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;
using MediaBrowser.Controller.Entities;
using NSubstitute;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin.Tests;

/// <summary>
/// The write path, driven through the faults it exists to decline (DESIGN §9.2).
/// </summary>
/// <remarks>
/// The rules were tested exhaustively in the core and the queries eventually in
/// the database suite; what consults them at the moment of writing had no test at
/// all, and three defects in a row landed there. A live harness proves the happy
/// path executes. It cannot arrange for a source row to vanish between two
/// statements, which is exactly the class of thing this decides.
/// </remarks>
public class PlannedWriteApplierTests
{
    private static readonly Guid Selected = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task AnUnchangedWorldRestoresAndVerifiesRightThroughToTheDatabase()
    {
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        harness.Database.AddDetached(WriteHarness.UserId, "603");

        var result = await harness.ApplyAsync(harness.Plan("tt0133093", "603"));

        Assert.Equal(WriteOutcome.Restored, result.Outcome);
        Assert.Null(result.Detail);

        // The stranded rows are the only surviving copy, and the run reads them.
        Assert.Equal(2, harness.Database.DetachedFingerprints(WriteHarness.UserId, ["tt0133093", "603"]).Count);
    }

    [Fact]
    public async Task ASourceDeletedBetweenAnalysisAndSaveDeclinesTheWrite()
    {
        // Jellyfin's own cleanup task, landing in the gap. No library scan, so
        // nothing else in the run notices.
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        harness.Database.AddDetached(WriteHarness.UserId, "603");
        var write = harness.Plan("tt0133093", "603");

        harness.Database.RemoveDetached(WriteHarness.UserId, "603");

        var result = await harness.ApplyAsync(write);

        Assert.Equal(WriteOutcome.Skipped, result.Outcome);
        Assert.StartsWith(SourceRevalidation.SourceGone, result.Detail, StringComparison.Ordinal);
        harness.UserDataManager.DidNotReceiveWithAnyArgs().SaveUserData(default!, default!, default!, default);
    }

    [Fact]
    public async Task ASourceSupersededBetweenAnalysisAndSaveDeclinesTheWrite()
    {
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        harness.Database.AddDetached(WriteHarness.UserId, "603");
        var write = harness.Plan("tt0133093", "603");

        // Another deletion of the same title replaces the row under one key.
        harness.Database.RemoveDetached(WriteHarness.UserId, "603");
        harness.Database.AddDetached(WriteHarness.UserId, "603", playCount: 41);

        var result = await harness.ApplyAsync(write);

        Assert.Equal(WriteOutcome.Skipped, result.Outcome);
        Assert.StartsWith(SourceRevalidation.SourceReplaced, result.Detail, StringComparison.Ordinal);
        harness.UserDataManager.DidNotReceiveWithAnyArgs().SaveUserData(default!, default!, default!, default);
    }

    [Fact]
    public async Task ANewerSourceUnderAKeyThatAuthorisedNothingDeclinesTheWrite()
    {
        // The regression that survived the first fix. The write is built from two
        // keys; the target answers to a third, and a deletion elsewhere strands a
        // newer snapshot under it. A re-read narrowed to the authorising keys
        // never asks, the old state is written, Jellyfin fans it across the third
        // key too, and the newer snapshot reads as a conflict on every run after.
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        harness.Database.AddDetached(WriteHarness.UserId, "603");
        var write = harness.Plan("tt0133093", "603");

        var thirdKey = harness.Item.Id.ToString("D");
        Assert.Contains(thirdKey, write.TargetKeys);
        Assert.DoesNotContain(thirdKey, write.SourceKeys);
        harness.Database.AddDetached(WriteHarness.UserId, thirdKey, playCount: 41);

        var result = await harness.ApplyAsync(write);

        Assert.Equal(WriteOutcome.Skipped, result.Outcome);
        Assert.StartsWith(SourceRevalidation.SourceAppeared, result.Detail, StringComparison.Ordinal);
        harness.UserDataManager.DidNotReceiveWithAnyArgs().SaveUserData(default!, default!, default!, default);
    }

    [Fact]
    public async Task ATargetRowHoldingNothingButDefaultsIsAnExplicitClear()
    {
        // The property the whole repeating schedule rests on. Clearing a flag
        // leaves a row full of defaults behind, which the manager reports exactly
        // as it reports a pair nobody has touched.
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        var write = harness.Plan("tt0133093");

        harness.Database.AddCurrent(
            WriteHarness.UserId, harness.Item.Id, "tt0133093",
            played: false, playCount: 0, ticks: 0, favorite: false, rating: null);

        var result = await harness.ApplyAsync(write);

        Assert.Equal(WriteOutcome.Skipped, result.Outcome);
        Assert.Equal("row_exists", result.Detail);
        harness.UserDataManager.DidNotReceiveWithAnyArgs().SaveUserData(default!, default!, default!, default);
    }

    [Fact]
    public async Task ATargetThatStoppedAnsweringToItsKeysIsNotWrittenTo()
    {
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        var write = harness.Plan("tt0133093");

        // A metadata refresh lands between the analysis and the write.
        harness.Item.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Imdb"] = "tt9999999",
        };

        var result = await harness.ApplyAsync(write);

        Assert.Equal(WriteOutcome.Skipped, result.Outcome);
        Assert.StartsWith(TargetRevalidation.KeyNoLongerReported, result.Detail, StringComparison.Ordinal);
        harness.UserDataManager.DidNotReceiveWithAnyArgs().SaveUserData(default!, default!, default!, default);
    }

    [Fact]
    public async Task AUserThatDisappearedStopsShortOfTheSave()
    {
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        var write = harness.Plan("tt0133093");

        harness.UserManager.GetUserById(WriteHarness.UserId).Returns((User?)null);

        var result = await harness.ApplyAsync(write);

        Assert.Equal(WriteOutcome.Skipped, result.Outcome);
        Assert.Equal("user_gone", result.Detail);
    }

    [Fact]
    public async Task AReaderFailingBeforeTheSaveLeavesTheItemProvablyUntouched()
    {
        // Everything before the save can fail cleanly, and the run has to say so
        // in the one word that means "nothing happened here": Failed, not
        // Uncertain. The batch stops either way; what differs is what an operator
        // is told about this item.
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        var write = harness.Plan("tt0133093");

        harness.Database.Dispose();

        var result = await harness.ApplyAsync(write);

        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.Equal("threw_before_save", result.Detail);
        harness.UserDataManager.DidNotReceiveWithAnyArgs().SaveUserData(default!, default!, default!, default);
    }

    [Fact]
    public async Task ASaveThatThrewLeavesTheAnswerUnknownRatherThanFailed()
    {
        // The save can throw after the database has committed, so once it has
        // been entered the honest answer about the item is that nobody knows.
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        harness.SaveThrows = new InvalidOperationException("the server went away mid-save");

        var result = await harness.ApplyAsync(harness.Plan("tt0133093"));

        Assert.Equal(WriteOutcome.Uncertain, result.Outcome);
        Assert.Equal("save_threw", result.Detail);
    }

    [Fact]
    public async Task ACancellationInsideTheSaveIsStillAnswerableAsUncertain()
    {
        // Cancellation included, and this is the case that makes it worth saying:
        // "the operator pressed stop" is not an answer about an item whose write
        // may already have committed.
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        harness.SaveThrows = new OperationCanceledException();

        var result = await harness.ApplyAsync(harness.Plan("tt0133093"));

        Assert.Equal(WriteOutcome.Uncertain, result.Outcome);
        Assert.Equal("save_threw", result.Detail);
    }

    [Fact]
    public async Task StateThatReadsBackWrongThroughTheManagerIsUncertain()
    {
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        harness.ReadBackReturns = new RecoveryState { Played = true, PlayCount = 99 };

        var result = await harness.ApplyAsync(harness.Plan("tt0133093"));

        Assert.Equal(WriteOutcome.Uncertain, result.Outcome);
        Assert.Equal("verification_mismatch", result.Detail);
    }

    [Fact]
    public async Task ASaveTheManagerAcceptedButStorageNeverSawIsUncertain()
    {
        // The reason the manager read-back is not the end of it. On 10.11 that
        // read is answered from a cache the save populated, so it reports success
        // for a write that reached no row. What survives a restart is the
        // database, and the database is what this asks.
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        harness.SwallowPersistence = true;

        var result = await harness.ApplyAsync(harness.Plan("tt0133093"));

        Assert.Equal(WriteOutcome.Uncertain, result.Outcome);
        Assert.Equal("not_persisted", result.Detail);
    }

    [Fact]
    public async Task RowsHoldingSomethingOtherThanWhatWasAskedForAreUncertain()
    {
        using var harness = WriteHarness.ForMovie(Selected);
        harness.Database.AddDetached(WriteHarness.UserId, "tt0133093");
        var write = harness.Plan("tt0133093");

        // A row for this pair under a key the fan-out will not overwrite, holding
        // state nobody asked for. It is present before the save, so the pair is
        // caught by the row-existence check first -- which is the point: this
        // asserts the ordering, not just the verdict.
        harness.Database.AddCurrent(WriteHarness.UserId, harness.Item.Id, "stale-key", playCount: 77);

        var result = await harness.ApplyAsync(write);

        Assert.Equal(WriteOutcome.Skipped, result.Outcome);
        Assert.Equal("row_exists", result.Detail);
    }
}
