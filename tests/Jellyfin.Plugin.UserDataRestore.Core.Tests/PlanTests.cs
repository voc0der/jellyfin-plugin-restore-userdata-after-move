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

    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void ARowWhoseRatingJsonCannotExpressStillProducesAPlan(double rating, string expected)
    {
        // The validator already rejects these as invalid_source_state, so such a
        // row is only ever reported — but it is reported inside an artifact that
        // is written after the run's writes have already landed. A number the
        // serializer refuses would take the audit record of those writes with it,
        // so the value is carried as a literal and the numeric field left null.
        var plan = BuildPlan(
        [
            Scenario.Row(Scenario.UserA, "tt0133093"),
            Scenario.Row(Scenario.UserB, "603", rating: rating),
        ]);

        var broken = Assert.Single(plan.SourceRows, row => row.UserId == Scenario.UserB.ToString("D"));
        Assert.Equal(ReasonCodes.ToWire(ReasonCode.InvalidSourceState), broken.Reason);
        Assert.Null(broken.State.Rating);
        Assert.Equal(expected, broken.State.RatingLiteral);

        // The write that had nothing to do with the malformed row still reaches
        // the plan, which is the whole point of not throwing.
        Assert.Single(plan.Writes);
        Assert.True(PlanCanonicalizer.VerifyPlanId(plan));
        Assert.True(PlanCanonicalizer.VerifyPlanId(
            PlanCanonicalizer.FromJson(PlanCanonicalizer.ToReadableJson(plan))));
    }

    [Fact]
    public void AFiniteRatingCarriesNoLiteral()
    {
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093", rating: 9)]);

        var write = Assert.Single(plan.Writes);
        Assert.Equal(9, write.State.Rating);
        Assert.Null(write.State.RatingLiteral);
        Assert.DoesNotContain("ratingLiteral", PlanCanonicalizer.ToReadableJson(plan), StringComparison.Ordinal);
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

    [Fact]
    public void EveryExclusionHasAPinnedWireName()
    {
        // Pinned by value, not by round-trip: a test that asserts ToWire agrees
        // with itself would pass through the rename these names exist to survive.
        Assert.Equal("eligible", ItemExclusions.ToWire(ItemExclusion.None));
        Assert.Equal("unsupported_type", ItemExclusions.ToWire(ItemExclusion.UnsupportedType));
        Assert.Equal("virtual_or_extra", ItemExclusions.ToWire(ItemExclusion.VirtualOrExtra));
        Assert.Equal("missing_media_file", ItemExclusions.ToWire(ItemExclusion.MissingPath));
        Assert.Equal("library_not_configured", ItemExclusions.ToWire(ItemExclusion.LibraryNotConfigured));
        Assert.Equal("path_outside_final_scope", ItemExclusions.ToWire(ItemExclusion.PathOutsideFinalScope));

        Assert.Equal("other", ItemKinds.ToWire(ItemKind.Other));
        Assert.Equal("movie", ItemKinds.ToWire(ItemKind.Movie));
        Assert.Equal("episode", ItemKinds.ToWire(ItemKind.Episode));
    }

    [Theory]
    [InlineData(ItemExclusion.LibraryNotConfigured, "library_not_configured")]
    [InlineData(ItemExclusion.PathOutsideFinalScope, "path_outside_final_scope")]
    [InlineData(ItemExclusion.MissingPath, "missing_media_file")]
    [InlineData(ItemExclusion.VirtualOrExtra, "virtual_or_extra")]
    public void AnExcludedMatchIsNamedInTheWireVocabulary(ItemExclusion exclusion, string expected)
    {
        // The regression this exists for: the field was rendered with
        // Exclusion.ToString(), so the plan carried "LibraryNotConfigured" while
        // every other classification in the document was snake_case.
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")], target: ExcludedMovie(exclusion));

        var match = Assert.Single(Assert.Single(plan.SourceRows).Matches);
        Assert.Equal(expected, match.Exclusion);
        Assert.Equal("movie", match.Kind);
    }

    [Fact]
    public void AnEligibleMatchAndItsCandidateAreNamedTheSameWay()
    {
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);

        Assert.Equal("eligible", Assert.Single(Assert.Single(plan.SourceRows).Matches).Exclusion);
        Assert.Equal("movie", Assert.Single(plan.Candidates).TargetKind);
    }

    /// <summary>A movie the eligibility rule rejects for exactly one reason.</summary>
    private static CurrentItemSnapshot ExcludedMovie(ItemExclusion exclusion) => exclusion switch
    {
        ItemExclusion.LibraryNotConfigured => Scenario.Movie(MovieId, libraryId: Scenario.OtherLibraryId),
        ItemExclusion.PathOutsideFinalScope => Scenario.Movie(MovieId, path: "/mnt/staging/Test Movie (2020).mkv"),
        // No path at all, rather than a path that does not exist: the latter is
        // only an exclusion when RequirePathExists is on, which these options
        // leave off, and the point here is the name not the rule.
        ItemExclusion.MissingPath => Scenario.Movie(MovieId) with { Path = null },
        ItemExclusion.VirtualOrExtra => Scenario.Movie(MovieId) with { IsVirtualItem = true },
        _ => throw new ArgumentOutOfRangeException(nameof(exclusion)),
    };

    [Fact]
    public void EveryWriteRecordsWhatBecameOfIt()
    {
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);

        var write = Assert.Single(plan.Writes);
        Assert.Equal("restored", write.Outcome);
        Assert.Null(write.OutcomeDetail);
    }

    [Fact]
    public void AMixedRunIsNotReportedAsAllRestored()
    {
        // The regression this exists for: the plan copied every analysis-time
        // `ready` write into `writes` and called the array the list of restores
        // the run performed. A skip, a throw and a completed restore then read
        // identically, and the artifact claimed all three had happened.
        var plan = BuildPlan(
            [
                Scenario.Row(Scenario.UserA, "tt0133093"),
                Scenario.Row(Scenario.UserB, "tt0133093", played: false, rating: 1),
            ],
            outcomes: result =>
            [
                new WriteResult(result.Writes[0], WriteOutcome.Restored, null),
                new WriteResult(result.Writes[1], WriteOutcome.Skipped, "row_exists"),
            ]);

        Assert.Equal(2, plan.Summary.WriteCount);
        Assert.Equal(["restored", "skipped"], plan.Writes.Select(write => write.Outcome));
        Assert.Equal([null, "row_exists"], plan.Writes.Select(write => write.OutcomeDetail));
        Assert.Equal(1, plan.Summary.WriteOutcomeCounts["restored"]);
        Assert.Equal(1, plan.Summary.WriteOutcomeCounts["skipped"]);
        Assert.Equal(0, plan.Summary.WriteOutcomeCounts["uncertain"]);
    }

    [Fact]
    public void TheSummaryCarriesEveryOutcomeIncludingTheZeroes()
    {
        // "Nothing ended uncertain" is the answer to the question this block
        // exists to raise, and an absent key does not say it.
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);

        foreach (var outcome in WriteOutcomes.All)
        {
            Assert.True(plan.Summary.WriteOutcomeCounts.ContainsKey(WriteOutcomes.ToWire(outcome)));
        }
    }

    [Fact]
    public void TwoRunsThatDifferOnlyInOutcomeDoNotShareAPlanId()
    {
        var restored = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);
        var uncertain = BuildPlan(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            outcomes: result => [new WriteResult(result.Writes[0], WriteOutcome.Uncertain, "save_threw")]);

        Assert.NotEqual(restored.PlanId, uncertain.PlanId);
        Assert.True(PlanCanonicalizer.VerifyPlanId(uncertain));
    }

    [Fact]
    public void OutcomesMustCoverEveryPlannedWrite()
    {
        // A short list would silently drop writes off the end of the record.
        Assert.Throws<ArgumentException>(() => BuildPlan(
            [
                Scenario.Row(Scenario.UserA, "tt0133093"),
                Scenario.Row(Scenario.UserB, "tt0133093", played: false, rating: 1),
            ],
            outcomes: result => [new WriteResult(result.Writes[0], WriteOutcome.Restored, null)]));
    }

    [Fact]
    public void APlanSurvivesTheLossOfItsClosingFingerprint()
    {
        // Taken after the writes have landed, so losing it must not cost the
        // record of the restores it was going to be proof about.
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")], dropAfter: true);

        Assert.Null(plan.TableChange.RowCountAfter);
        Assert.Null(plan.TableChange.DigestAfter);
        Assert.Null(plan.TableChange.Unchanged);
        Assert.Equal("abc", plan.TableChange.DigestBefore);
        Assert.Single(plan.Writes);
        Assert.True(PlanCanonicalizer.VerifyPlanId(plan));
    }

    private static PlanDocument BuildPlan(
        IReadOnlyList<DetachedUserDataRow> rows,
        DateTimeOffset? created = null,
        CurrentItemSnapshot? target = null,
        AnalysisOptions? options = null,
        Func<AnalysisResult, IReadOnlyList<WriteResult>>? outcomes = null,
        bool dropAfter = false)
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
            FingerprintAfter = dropAfter ? null : new UserDataFingerprint(10, "abc"),
            WriteResults = outcomes is null ? AllRestored(result) : outcomes(result),
        });
    }

    /// <summary>The happy path: every planned write landed and verified.</summary>
    private static IReadOnlyList<WriteResult> AllRestored(AnalysisResult result) =>
        [.. result.Writes.Select(write => new WriteResult(write, WriteOutcome.Restored, null))];

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "userdata-restore-tests-" + Guid.NewGuid().ToString("N"));
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
