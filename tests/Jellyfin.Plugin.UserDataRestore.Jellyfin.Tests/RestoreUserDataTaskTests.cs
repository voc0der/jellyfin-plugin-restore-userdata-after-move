using Jellyfin.Plugin.UserDataRestore.Core.Planning;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin.Tests;

public sealed class RestoreUserDataTaskTests
{
    [Fact]
    public async Task RestoresAndRecordsTheRunOnANewerServerThenRetriesWithoutWriting()
    {
        using var harness = new RestoreTaskHarness();
        var movie = harness.AddMovie();
        harness.Plugin.Configuration.VerboseLogging = true;
        var detached = harness.Database.DetachedFingerprint(harness.User.Id, "tt0133093");
        Directory.CreateDirectory(harness.Plugin.PlanDirectory);
        var oldPlan = Path.Join(harness.Plugin.PlanDirectory, "plan-20000101T000000Z-old.json");
        var oldLedger = Path.Join(harness.Plugin.PlanDirectory, "run-20000101T000000Z.jsonl");
        File.WriteAllText(oldPlan, "{}");
        File.WriteAllText(oldLedger, "{}");

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        var plan = harness.ReadPlan();
        var write = Assert.Single(plan.Writes);
        Assert.Equal("restored", write.Outcome);
        Assert.Equal(movie.Id.ToString("D"), write.ItemId);
        Assert.Equal(harness.User.Id.ToString("D"), write.UserId);
        Assert.Equal("coverage-server", plan.ServerId);
        Assert.Equal("12.1.0", plan.ServerVersion);
        Assert.False(plan.TableChange.Unchanged);
        Assert.Equal(1, plan.TableChange.RowCountBefore);
        Assert.Equal(3, plan.TableChange.RowCountAfter);
        Assert.Equal([5, 50, 95, 95, 100], harness.ProgressValues);
        var entry = Assert.Single(harness.ReadLedger());
        Assert.Equal(write.ItemId, entry.GetProperty("itemId").GetString());
        Assert.Equal(write.UserId, entry.GetProperty("userId").GetString());
        Assert.Equal("restored", entry.GetProperty("outcome").GetString());
        Assert.False(File.Exists(oldPlan));
        Assert.False(File.Exists(oldLedger));
        Assert.Equal(detached, harness.Database.DetachedFingerprint(harness.User.Id, "tt0133093"));
        Assert.Contains(harness.Messages, entry => entry.Level == LogLevel.Debug && entry.Message.Contains(movie.Id.ToString(), StringComparison.Ordinal));

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        var retry = harness.ReadPlan();
        Assert.Empty(retry.Writes);
        Assert.True(retry.TableChange.Unchanged);
        Assert.Equal(1, retry.Summary.CandidateCounts["already_applied"]);
        harness.UserDataManager.ReceivedWithAnyArgs(1).SaveUserData(default!, default!, default!, default);
    }

    [Fact]
    public async Task ARunningLibraryScanSkipsBeforeReadingTheDatabase()
    {
        using var harness = new RestoreTaskHarness();
        harness.Scan.State.Returns(TaskState.Running);
        harness.Database.Factory.ClearReceivedCalls();

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal([100], harness.ProgressValues);
        Assert.Empty(harness.Database.Factory.ReceivedCalls());
        AssertNoSave(harness);
        Assert.Empty(new PlanStore(harness.Plugin.PlanDirectory).List());
    }

    [Fact]
    public async Task AnUnrelatedRunningTaskDoesNotPreventRecovery()
    {
        using var harness = new RestoreTaskHarness();
        harness.AddMovie();
        harness.Scan.ScheduledTask.Key.Returns("OtherTask");
        harness.Scan.State.Returns(TaskState.Running);

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal("restored", Assert.Single(harness.ReadPlan().Writes).Outcome);
        Assert.Empty(harness.Task.GetDefaultTriggers());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnusableScopeFailsBeforeReadingTheDatabase(bool emptyFolders)
    {
        using var harness = new RestoreTaskHarness();
        if (emptyFolders)
        {
            harness.Folder.Locations = [];
        }
        else
        {
            harness.Plugin.Configuration.EligibleLibraryIds = [];
        }

        harness.Database.Factory.ClearReceivedCalls();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Empty(harness.Database.Factory.ReceivedCalls());
        AssertNoSave(harness);
        Assert.Empty(new PlanStore(harness.Plugin.PlanDirectory).List());
    }

    [Fact]
    public async Task LegacyOverridesArePersistentlyClearedBeforeResolvingTargets()
    {
        using var harness = new RestoreTaskHarness();
        harness.AddMovie();
        var missing = harness.AddMovie("tt0137523");
        File.Delete(missing.Path);
        harness.Plugin.Configuration.FinalPathPrefixes = ["/obsolete/narrow/scope"];
        harness.Plugin.Configuration.RequirePathExists = false;

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Empty(harness.Plugin.Configuration.FinalPathPrefixes);
        Assert.True(harness.Plugin.Configuration.RequirePathExists);
        harness.Serializer.Received(1).SerializeToFile(harness.Plugin.Configuration, harness.Plugin.ConfigurationFilePath);
        var plan = harness.ReadPlan();
        Assert.Equal([harness.MediaDirectory], plan.FinalPathPrefixes);
        Assert.Equal("restored", Assert.Single(plan.Writes).Outcome);
        Assert.DoesNotContain(plan.Writes, write => write.ItemId == missing.Id.ToString("D"));
        Assert.Contains(harness.Messages, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("media file was not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationAfterOneRestoreRecordsEveryOutcomeBeforeThrowing()
    {
        using var harness = TwoMovies();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        harness.OnProgress = value =>
        {
            if (value > 50 && value < 95)
            {
                Assert.Single(harness.ReadLedger());
                cancellation.Cancel();
            }
        };

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => harness.ExecuteAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        AssertOutcomes(harness, "restored", "not_attempted");
        Assert.Equal(ApplySequence.Cancelled, harness.ReadPlan().Writes[1].OutcomeDetail);
        Assert.NotNull(harness.ReadPlan().TableChange.DigestAfter);
        Assert.Equal(100, harness.ProgressValues[^1]);
        harness.UserDataManager.ReceivedWithAnyArgs(1).SaveUserData(default!, default!, default!, default);
    }

    [Fact]
    public async Task AScanStartingAfterOneRestoreRecordsTheUnattemptedRemainder()
    {
        using var harness = TwoMovies();
        harness.OnProgress = value =>
        {
            if (value > 50 && value < 95)
            {
                harness.Scan.State.Returns(TaskState.Running);
            }
        };

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        AssertOutcomes(harness, "restored", "not_attempted");
        Assert.Equal(ApplySequence.LibraryScanStarted, harness.ReadPlan().Writes[1].OutcomeDetail);
        Assert.Contains(harness.Messages, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("part-way", StringComparison.Ordinal));
        harness.UserDataManager.ReceivedWithAnyArgs(1).SaveUserData(default!, default!, default!, default);
    }

    [Theory]
    [InlineData(false, "failed", "threw_before_save")]
    [InlineData(true, "uncertain", "save_threw")]
    public async Task AWriteFailureStopsTheRunButPreservesItsArtifacts(bool duringSave, string outcome, string detail)
    {
        using var harness = TwoMovies();
        if (duringSave)
        {
            harness.SaveThrows = new IOException("save failed");
        }
        else
        {
            harness.UserManager.GetUserById(harness.User.Id).Returns(_ => throw new IOException("user lookup failed"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ExecuteAsync(TestContext.Current.CancellationToken));

        AssertOutcomes(harness, outcome, "not_attempted");
        Assert.Equal(detail, harness.ReadPlan().Writes[0].OutcomeDetail);
        Assert.True(harness.ReadPlan().TableChange.Unchanged);
        Assert.Equal(100, harness.ProgressValues[^1]);
        Assert.Contains(harness.Messages, entry => entry.Message.StartsWith("Restored 0 snapshots", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APostRunFingerprintFailureStillPublishesTheCompletedRestores()
    {
        using var harness = new RestoreTaskHarness();
        harness.AddMovie();
        harness.OnProgress = value =>
        {
            if (value == 95)
            {
                harness.Database.Factory.CreateDbContextAsync(Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromException<global::Jellyfin.Database.Implementations.JellyfinDbContext>(
                        new IOException("database unavailable after save")));
            }
        };

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        AssertOutcomes(harness, "restored");
        var table = harness.ReadPlan().TableChange;
        Assert.NotEmpty(table.DigestBefore);
        Assert.Null(table.DigestAfter);
        Assert.Null(table.RowCountAfter);
        Assert.Null(table.Unchanged);
        Assert.Contains(harness.Messages, entry => entry.Level == LogLevel.Error && entry.Message.Contains("post-run fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnavailableLedgerDoesNotPreventSavingOrPublishingThePlan()
    {
        using var harness = new RestoreTaskHarness();
        harness.AddMovie();
        Directory.CreateDirectory(harness.Plugin.DataFolderPath);
        File.WriteAllText(harness.Plugin.PlanDirectory, "blocks ledger directory creation");
        harness.OnProgress = value =>
        {
            if (value == 95 && File.Exists(harness.Plugin.PlanDirectory))
            {
                File.Delete(harness.Plugin.PlanDirectory);
            }
        };

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal("restored", Assert.Single(harness.ReadPlan().Writes).Outcome);
        Assert.Empty(RunLedger.List(harness.Plugin.PlanDirectory));
        Assert.Contains(harness.Messages, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Could not open a run ledger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APlanFailureLeavesTheLedgerAndSummaryAvailable()
    {
        using var harness = new RestoreTaskHarness();
        harness.AddMovie();
        // Fail plan construction after the writes, leaving the on-disk ledger
        // as the surviving account of the run.
        harness.Host.SystemId.Returns(_ => throw new IOException("plan context unavailable"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.IsType<IOException>(error.InnerException);
        Assert.Empty(new PlanStore(harness.Plugin.PlanDirectory).List());
        Assert.Equal("restored", Assert.Single(harness.ReadLedger()).GetProperty("outcome").GetString());
        Assert.Contains(harness.Messages, entry => entry.Message.StartsWith("Restored 1 snapshots", StringComparison.Ordinal));
        Assert.Equal(100, harness.ProgressValues[^1]);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("uncertain")]
    public async Task APlanFailureDoesNotHideWhyTheWritesStopped(string reason)
    {
        using var harness = TwoMovies();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var planFailure = new IOException("plan context unavailable");
        harness.Host.SystemId.Returns(_ => throw planFailure);
        if (reason == "cancelled")
        {
            harness.OnProgress = value =>
            {
                if (value > 50 && value < 95)
                {
                    cancellation.Cancel();
                }
            };
        }
        else if (reason == "failed")
        {
            harness.UserManager.GetUserById(harness.User.Id).Returns(_ => throw new IOException("lookup failed"));
        }
        else
        {
            harness.SaveThrows = new IOException("save failed");
        }

        Exception error = reason == "cancelled"
            ? await Assert.ThrowsAsync<OperationCanceledException>(() => harness.ExecuteAsync(cancellation.Token))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ExecuteAsync(cancellation.Token));

        Assert.Same(planFailure, error.InnerException);
        var ledger = harness.ReadLedger();
        Assert.Equal(2, ledger.Count);
        Assert.Equal(reason == "cancelled" ? "restored" : reason, ledger[0].GetProperty("outcome").GetString());
        Assert.Equal("not_attempted", ledger[1].GetProperty("outcome").GetString());
        Assert.Empty(new PlanStore(harness.Plugin.PlanDirectory).List());
        var restored = reason == "cancelled" ? 1 : 0;
        Assert.Contains(harness.Messages, entry => entry.Message.StartsWith($"Restored {restored} snapshots", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATargetLosingItsFileAfterAnalysisIsRecordedAsSkipped()
    {
        using var harness = new RestoreTaskHarness();
        var movie = harness.AddMovie();
        harness.OnProgress = value =>
        {
            if (value == 50)
            {
                File.Delete(movie.Path);
            }
        };

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        AssertOutcomes(harness, "skipped");
        Assert.True(harness.ReadPlan().TableChange.Unchanged);
        AssertNoSave(harness);
    }

    [Fact]
    public async Task MissingProviderKeysAreReportedWithoutGuessingATarget()
    {
        using var harness = new RestoreTaskHarness();
        var movie = harness.AddMovie();
        movie.ProviderIds.Clear();

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Empty(harness.ReadPlan().Writes);
        Assert.True(harness.ReadPlan().TableChange.Unchanged);
        Assert.Empty(RunLedger.List(harness.Plugin.PlanDirectory));
        Assert.Contains(harness.Messages, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("only an exact old-item GUID", StringComparison.Ordinal));
        AssertNoSave(harness);
    }

    private static RestoreTaskHarness TwoMovies()
    {
        var harness = new RestoreTaskHarness();
        harness.AddMovie();
        harness.AddMovie("tt0137523");
        return harness;
    }

    private static void AssertOutcomes(RestoreTaskHarness harness, params string[] expected)
    {
        var plan = harness.ReadPlan();
        Assert.Equal(expected, plan.Writes.Select(write => write.Outcome));
        Assert.Equal(expected, harness.ReadLedger().Select(entry => entry.GetProperty("outcome").GetString()));
        Assert.Equal(plan.Writes.Count, plan.Summary.WriteCount);
        foreach (var outcome in expected.Distinct(StringComparer.Ordinal))
        {
            Assert.Equal(expected.Count(value => value == outcome), plan.Summary.WriteOutcomeCounts[outcome]);
        }
    }

    private static void AssertNoSave(RestoreTaskHarness harness) =>
        harness.UserDataManager.DidNotReceiveWithAnyArgs().SaveUserData(default!, default!, default!, default);
}
