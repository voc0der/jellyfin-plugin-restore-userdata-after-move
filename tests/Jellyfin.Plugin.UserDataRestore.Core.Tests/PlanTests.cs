using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;
using Jellyfin.Plugin.UserDataRestore.Core.Verification;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// Plan canonicalization, hashing, and storage (DESIGN §8, §12.1, §12.2).
/// </summary>
public class PlanTests
{
    private static readonly Guid MovieId = new("74f9957e-b453-7dbb-b614-d528834acab2");
    private static readonly Guid OtherMovieId = new("5fc90611-0000-0000-0000-00000000000f");

    [Fact]
    public void TheSameInputsHashTheSameRegardlessOfRowOrder()
    {
        var forward = BuildPlan(
        [
            Scenario.Row(Scenario.UserA, "tt0133093"),
            Scenario.Row(Scenario.UserA, MovieId.ToString("D")),
            Scenario.Row(Scenario.UserB, "tt0133093", played: false, rating: 1),
        ]);

        var reversed = BuildPlan(
        [
            Scenario.Row(Scenario.UserB, "tt0133093", played: false, rating: 1),
            Scenario.Row(Scenario.UserA, MovieId.ToString("D")),
            Scenario.Row(Scenario.UserA, "tt0133093"),
        ]);

        Assert.Equal(forward.PlanId, reversed.PlanId);
        Assert.True(PlanCanonicalizer.VerifyPlanId(forward));
    }

    [Fact]
    public void TheOrderJellyfinReturnsAnItemsKeysInDoesNotChangeThePlanId()
    {
        // GetUserDataKeys() order is the host's business and carries no meaning
        // this plugin asserts. Since array order became part of the plan ID, an
        // incidental reordering upstream would otherwise produce a different ID
        // for an identical analysis.
        var movie = Scenario.Movie(MovieId);
        var reversedKeys = movie with { UserDataKeys = [.. movie.UserDataKeys.Reverse()] };

        var forward = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")], target: movie);
        var reversed = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")], target: reversedKeys);

        Assert.Equal(
            forward.Candidates.Single().TargetKeys,
            reversed.Candidates.Single().TargetKeys);
        Assert.Equal(forward.PlanId, reversed.PlanId);
    }

    [Fact]
    public void AnItemListingTheSameKeyTwiceDoesNotChangeThePlanId()
    {
        // CurrentKeyIndex already counts a repeated key once, so the two targets
        // below are the same target as far as matching is concerned. Since array
        // length is part of the plan ID as much as order is, the artifact has to
        // agree with that reading.
        var movie = Scenario.Movie(MovieId);
        var repeatedKey = movie with { UserDataKeys = [.. movie.UserDataKeys, movie.UserDataKeys[0]] };

        var once = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")], target: movie);
        var twice = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")], target: repeatedKey);

        Assert.Equal(movie.UserDataKeys.Count, twice.Candidates.Single().TargetKeys.Count);
        Assert.Equal(
            once.Candidates.Single().TargetKeys,
            twice.Candidates.Single().TargetKeys);
        Assert.Equal(once.PlanId, twice.PlanId);
    }

    [Fact]
    public void AConfigurationThatRepeatsItselfDoesNotChangeThePlanId()
    {
        // The path prefixes come from a textarea, one per line. A duplicated line
        // scopes the run identically and must identify the plan identically.
        var options = Scenario.Options();
        var repeated = options with
        {
            EligibleLibraryIds = [.. options.EligibleLibraryIds, options.EligibleLibraryIds[0]],
            FinalPathPrefixes = [.. options.FinalPathPrefixes, options.FinalPathPrefixes[0]],
        };

        var plain = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")], options: options);
        var duplicated = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")], options: repeated);

        Assert.Equal(options.FinalPathPrefixes.Count, duplicated.FinalPathPrefixes.Count);
        Assert.Equal(options.EligibleLibraryIds.Count, duplicated.ConfiguredLibraryIds.Count);
        Assert.Equal(plain.PlanId, duplicated.PlanId);
    }

    [Fact]
    public void DifferentContentHashesDifferently()
    {
        var one = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093", playCount: 3)]);
        var two = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093", playCount: 4)]);

        Assert.NotEqual(one.PlanId, two.PlanId);
    }

    [Fact]
    public void TamperingWithAPlanBreaksItsId()
    {
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);

        var tampered = plan with
        {
            Writes = [.. plan.Writes.Select(write => write with { ItemId = OtherMovieId.ToString("D") })],
        };

        Assert.True(PlanCanonicalizer.VerifyPlanId(plan));
        Assert.False(PlanCanonicalizer.VerifyPlanId(tampered));
    }

    [Fact]
    public void ReorderingTheWriteListBreaksThePlanId()
    {
        // DESIGN §8 calls the write list "the exact ordered list". A canonical form
        // that sorted arrays before hashing would let a reviewed plan and an
        // applied plan disagree about execution order under one plan ID.
        var plan = BuildPlan(
        [
            Scenario.Row(Scenario.UserA, "tt0133093"),
            Scenario.Row(Scenario.UserB, "tt0133093", played: false, rating: 1),
        ]);

        Assert.Equal(2, plan.Writes.Count);
        var reordered = plan with { Writes = [.. plan.Writes.Reverse()] };

        Assert.True(PlanCanonicalizer.VerifyPlanId(plan));
        Assert.False(PlanCanonicalizer.VerifyPlanId(reordered));
    }

    [Fact]
    public void ReorderingAnyOtherArrayAlsoBreaksThePlanId()
    {
        var plan = BuildPlan(
        [
            Scenario.Row(Scenario.UserA, "tt0133093"),
            Scenario.Row(Scenario.UserA, "603"),
        ]);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(2, candidate.ContributingKeys.Count);

        var reordered = plan with
        {
            Candidates = [candidate with { ContributingKeys = [.. candidate.ContributingKeys.Reverse()] }],
        };

        Assert.False(PlanCanonicalizer.VerifyPlanId(reordered));
    }

    [Fact]
    public void ChangingTheScopeChangesTheId()
    {
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);
        var rescoped = plan with { FinalPathPrefixes = ["/somewhere/else"] };

        Assert.False(PlanCanonicalizer.VerifyPlanId(rescoped));
    }

    [Fact]
    public void APlanRoundTripsThroughJson()
    {
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);

        var restored = PlanCanonicalizer.FromJson(PlanCanonicalizer.ToReadableJson(plan));

        Assert.Equal(plan.PlanId, restored.PlanId);
        Assert.True(PlanCanonicalizer.VerifyPlanId(restored));
        Assert.Equal(plan.Writes.Count, restored.Writes.Count);
    }

    [Fact]
    public void APlanRecordsThatTheBuildThatWroteItCanApply()
    {
        // Analysis-only builds wrote plans too. The flag is how a reader tells
        // one of those from a plan this build produced.
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);

        Assert.True(plan.ApplySupported);
    }

    [Fact]
    public void PlansArePublishedAtomicallyAndCanBeReadBack()
    {
        using var directory = new TemporaryDirectory();
        var store = new PlanStore(directory.Path);
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);

        var path = store.Write(plan);

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.Contains(PlanStore.Shorten(plan.PlanId), Path.GetFileName(path), StringComparison.Ordinal);
        Assert.True(PlanCanonicalizer.VerifyPlanId(PlanCanonicalizer.FromJson(File.ReadAllText(path))));
    }

    [Fact]
    public void RetentionKeepsTheNewestPlans()
    {
        using var directory = new TemporaryDirectory();
        var store = new PlanStore(directory.Path);

        for (var i = 0; i < 8; i++)
        {
            store.Write(BuildPlan(
                [Scenario.Row(Scenario.UserA, "tt0133093", playCount: i)],
                created: new DateTimeOffset(2026, 8, 12, 0, 0, i, TimeSpan.Zero)));
        }

        var deleted = store.PruneToLatest(3);

        Assert.Equal(5, deleted);
        Assert.Equal(3, store.List().Count);
    }

    [Fact]
    public void RetentionNeverDeletesAProtectedPlan()
    {
        using var directory = new TemporaryDirectory();
        var store = new PlanStore(directory.Path);

        var oldest = BuildPlan(
            [Scenario.Row(Scenario.UserA, "tt0133093", playCount: 1)],
            created: new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
        store.Write(oldest);

        for (var i = 1; i < 5; i++)
        {
            store.Write(BuildPlan(
                [Scenario.Row(Scenario.UserA, "tt0133093", playCount: i + 1)],
                created: new DateTimeOffset(2026, 8, 12, 0, 0, i, TimeSpan.Zero)));
        }

        store.PruneToLatest(2, oldest.PlanId);

        Assert.Contains(store.List(), stored => stored.ShortPlanId == PlanStore.Shorten(oldest.PlanId));
    }

    [Fact]
    public void TheSummaryCarriesEveryReasonCode()
    {
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);

        foreach (var code in ReasonCodes.All)
        {
            Assert.True(plan.Summary.RowCounts.ContainsKey(ReasonCodes.ToWire(code)));
            Assert.True(plan.Summary.CandidateCounts.ContainsKey(ReasonCodes.ToWire(code)));
        }
    }

    private static PlanDocument BuildPlan(
        IReadOnlyList<DetachedUserDataRow> rows,
        DateTimeOffset? created = null,
        CurrentItemSnapshot? target = null,
        AnalysisOptions? options = null)
    {
        options ??= Scenario.Options();
        var result = Scenario.Analyze(rows, [target ?? Scenario.Movie(MovieId)], options: options);

        return PlanBuilder.Build(result, new PlanContext
        {
            PluginVersion = "1.0.0.0",
            TargetJellyfinVersion = "10.11.11",
            JellyfinPackageVersion = "10.11.11",
            TargetAbi = "10.11.11.0",
            ServerId = "test-server",
            ServerVersion = "10.11.11",
            CreatedUtc = created ?? new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero),
            Options = options,
            FingerprintBefore = new UserDataFingerprint(10, "abc"),
            FingerprintAfter = new UserDataFingerprint(10, "abc"),
        });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "userdata-restore-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Already gone.
            }
        }
    }
}
