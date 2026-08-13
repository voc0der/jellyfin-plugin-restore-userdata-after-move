using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Applying;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;
using Jellyfin.Plugin.UserDataRestore.Core.Verification;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// Arming and whole-plan preflight (DESIGN §6.3, §9.1).
/// </summary>
public class ApplyTests
{
    private static readonly Guid MovieId = new("74f9957e-b453-7dbb-b614-d528834acab2");
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APlanAppliesWhenNothingHasChanged()
    {
        var rows = new[] { Scenario.Row(Scenario.UserA, "tt0133093") };
        var plan = BuildPlan(rows);

        var result = ApplyPreflight.Reconcile(plan, Scenario.Analyze(rows, [Scenario.Movie(MovieId)]));

        Assert.True(result.MayProceed);
        var write = Assert.Single(result.Pending);
        Assert.Equal(WriteDisposition.Write, write.Disposition);
        Assert.Equal(MovieId, write.ItemId);
    }

    [Fact]
    public void ASourceRowEditedUnderneathThePlanBlocksTheRun()
    {
        // The snapshot an operator reviewed is not the snapshot on disk any more:
        // somebody played the title after the plan was written.
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);
        var changed = new[] { Scenario.Row(Scenario.UserA, "tt0133093", playCount: 99) };

        var result = ApplyPreflight.Reconcile(plan, Scenario.Analyze(changed, [Scenario.Movie(MovieId)]));

        Assert.False(result.MayProceed);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("have changed", StringComparison.Ordinal));
    }

    [Fact]
    public void AKeyThatBecameAmbiguousBlocksTheRun()
    {
        var rows = new[] { Scenario.Row(Scenario.UserA, "tt0133093") };
        var plan = BuildPlan(rows);

        // A second current item now reports the same key — a copy restored into
        // another library, or the old path repopulated.
        var twin = Scenario.Movie(new Guid("5fc90611-0000-0000-0000-00000000000f"));

        var result = ApplyPreflight.Reconcile(plan, Scenario.Analyze(rows, [Scenario.Movie(MovieId), twin]));

        Assert.False(result.MayProceed);
    }

    [Fact]
    public void AnAlreadyAppliedPairIsANoOpRatherThanAFailure()
    {
        var rows = new[] { Scenario.Row(Scenario.UserA, "tt0133093") };
        var plan = BuildPlan(rows);

        // The target now holds exactly the planned state: someone applied it, or
        // Jellyfin reattached it, in between.
        var current = new[]
        {
            Scenario.CurrentRow(Scenario.UserA, MovieId, "tt0133093"),
        };

        var result = ApplyPreflight.Reconcile(plan, Scenario.Analyze(rows, [Scenario.Movie(MovieId)], current));

        Assert.True(result.MayProceed);
        Assert.Empty(result.Pending);
        Assert.Equal(WriteDisposition.AlreadyApplied, result.Writes.Single().Disposition);
    }

    [Fact]
    public void AnEditedPlanFileIsRefused()
    {
        var plan = BuildPlan([Scenario.Row(Scenario.UserA, "tt0133093")]);
        var tampered = plan with { Writes = [] };

        var result = ApplyPreflight.Reconcile(tampered, Scenario.Analyze([Scenario.Row(Scenario.UserA, "tt0133093")], [Scenario.Movie(MovieId)]));

        Assert.False(result.MayProceed);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("does not match its own ID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wrong-plan", 1, "server", "10.11.11", true, "not the plan")]
    [InlineData("plan", 2, "server", "10.11.11", true, "were authorized")]
    [InlineData("plan", 1, "other", "10.11.11", true, "different server")]
    [InlineData("plan", 1, "server", "12.0.0", true, "when armed")]
    [InlineData("plan", 1, "server", "10.11.11", false, "backup acknowledgement")]
    public void AnArmIsRefusedWhenAnythingAboutItDisagrees(
        string planId,
        int writeCount,
        string serverId,
        string serverVersion,
        bool backup,
        string expected)
    {
        var arm = new ArmState
        {
            PlanId = planId,
            ExpectedWriteCount = writeCount,
            ServerId = serverId,
            ServerVersion = serverVersion,
            ArmedUtc = Now,
            ExpiresUtc = Now.AddMinutes(15),
            BackupAcknowledged = backup,
        };

        var verdict = ArmValidator.Validate(arm, "plan", 1, "server", "10.11.11", Now);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected, verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExpiredArmIsRefused()
    {
        var arm = new ArmState
        {
            PlanId = "plan",
            ExpectedWriteCount = 1,
            ServerId = "server",
            ServerVersion = "10.11.11",
            ArmedUtc = Now,
            ExpiresUtc = Now.AddMinutes(15),
            BackupAcknowledged = true,
        };

        Assert.True(ArmValidator.Validate(arm, "plan", 1, "server", "10.11.11", Now.AddMinutes(14)).IsValid);
        Assert.False(ArmValidator.Validate(arm, "plan", 1, "server", "10.11.11", Now.AddMinutes(16)).IsValid);
    }

    [Fact]
    public void NothingArmedMeansNothingApplies()
    {
        Assert.False(ArmValidator.Validate(null, "plan", 1, "server", "10.11.11", Now).IsValid);
        Assert.False(ArmValidator.Validate(new ArmState(), "plan", 1, "server", "10.11.11", Now).IsValid);
    }

    [Fact]
    public void ThePhraseNamesThePlanAndTheWriteCount()
    {
        // An operator confirming "yes" to an abstract question has confirmed
        // nothing. The phrase carries what they are authorizing.
        Assert.Equal("APPLY 3f17c4a9b28e 428", ArmState.Phrase("3f17c4a9b28e0011223344", 428));
    }

    private static PlanDocument BuildPlan(IReadOnlyList<DetachedUserDataRow> rows)
    {
        var result = Scenario.Analyze(rows, [Scenario.Movie(MovieId)]);

        return PlanBuilder.Build(result, new PlanContext
        {
            PluginVersion = "1.0.0.0",
            TargetJellyfinVersion = "10.11.11",
            JellyfinPackageVersion = "10.11.11",
            TargetAbi = "10.11.11.0",
            ServerId = "test-server",
            ServerVersion = "10.11.11",
            CreatedUtc = Now,
            Options = Scenario.Options(),
            FingerprintBefore = new UserDataFingerprint(10, "abc"),
            FingerprintAfter = new UserDataFingerprint(10, "abc"),
        });
    }
}
