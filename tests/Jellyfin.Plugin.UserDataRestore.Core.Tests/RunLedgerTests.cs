using System.Text.Json;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// The append-only record a run writes as it goes (DESIGN §8, §9.2 step 11).
/// </summary>
/// <remarks>
/// The plan is composed after the last write and published in one operation, so
/// anything that goes wrong between the first save and that moment takes the
/// whole record with it — after user data has already changed. These tests are
/// about the property that fixes: a line on disk per write, before the next one
/// is attempted, readable whether or not the run ever finished.
/// </remarks>
public class RunLedgerTests
{
    private static readonly Guid MovieId = new("74f9957e-b453-7dbb-b614-d528834acab2");
    private static readonly DateTimeOffset Started = new(2026, 8, 14, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EachWriteIsOneLineOfJson()
    {
        using var directory = new TemporaryDirectory();

        using (var ledger = RunLedger.Open(directory.Path, Started))
        {
            ledger.Append(new WriteResult(Write(), WriteOutcome.Restored, null));
            ledger.Append(new WriteResult(Write(), WriteOutcome.Skipped, "row_exists"));
        }

        var lines = File.ReadAllLines(Single(directory));
        Assert.Equal(2, lines.Length);

        var first = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("restored", first.GetProperty("outcome").GetString());
        Assert.Equal(MovieId.ToString("D"), first.GetProperty("itemId").GetString());
        Assert.Equal(Scenario.UserA.ToString("D"), first.GetProperty("userId").GetString());
        Assert.False(first.TryGetProperty("detail", out _));

        var second = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("skipped", second.GetProperty("outcome").GetString());
        Assert.Equal("row_exists", second.GetProperty("detail").GetString());
    }

    [Fact]
    public void ALineIsOnDiskBeforeTheNextIsWritten()
    {
        // The whole point. A buffered line is not a record of anything: the
        // failure this exists for is the one that stops the process, and it would
        // take the buffer with it.
        using var directory = new TemporaryDirectory();
        using var ledger = RunLedger.Open(directory.Path, Started);

        ledger.Append(new WriteResult(Write(), WriteOutcome.Restored, null));

        Assert.Single(File.ReadAllLines(Single(directory)));
    }

    [Fact]
    public void AnUnfinishedLedgerIsStillReadable()
    {
        // Never closed, never disposed — the crash case. Every line written so
        // far must parse, because a ledger truncated by a power cut is a ledger
        // missing its last line and nothing more.
        using var directory = new TemporaryDirectory();

        // Disposed by scope rather than by a call at the end, so a failing
        // assertion below cannot leave the handle open and take the directory's
        // own cleanup down with it. The reads still happen while the ledger is
        // open, which is the case under test.
        using var ledger = RunLedger.Open(directory.Path, Started);

        ledger.Append(new WriteResult(Write(), WriteOutcome.Restored, null));
        ledger.Append(new WriteResult(Write(), WriteOutcome.Uncertain, "save_threw"));

        var lines = File.ReadAllLines(Single(directory));
        Assert.All(lines, line => Assert.NotNull(JsonDocument.Parse(line)));
        Assert.Equal(["restored", "uncertain"], lines.Select(line =>
            JsonDocument.Parse(line).RootElement.GetProperty("outcome").GetString()));
    }

    [Fact]
    public void LedgersAreListedNewestFirstAndPruned()
    {
        using var directory = new TemporaryDirectory();

        for (var hour = 0; hour < 5; hour++)
        {
            using var ledger = RunLedger.Open(directory.Path, Started.AddHours(hour));
            ledger.Append(new WriteResult(Write(), WriteOutcome.Restored, null));
        }

        Assert.Equal(5, RunLedger.List(directory.Path).Count);
        Assert.Equal(2, RunLedger.PruneToLatest(directory.Path, 3));

        var remaining = RunLedger.List(directory.Path).Select(Path.GetFileName).ToArray();
        Assert.Equal(3, remaining.Length);

        // Newest first, and it is the newest three that survived.
        Assert.Equal(remaining, remaining.OrderByDescending(name => name, StringComparer.Ordinal));
        Assert.Contains("20260814T070000Z", remaining[0], StringComparison.Ordinal);
    }

    [Fact]
    public void PruningNeverRemovesTheLastLedger()
    {
        using var directory = new TemporaryDirectory();
        using (var ledger = RunLedger.Open(directory.Path, Started))
        {
            ledger.Append(new WriteResult(Write(), WriteOutcome.Restored, null));
        }

        Assert.Equal(0, RunLedger.PruneToLatest(directory.Path, 0));
        Assert.Single(RunLedger.List(directory.Path));
    }

    [Fact]
    public void TheLedgerSurvivesAPlanThatCannotBeWritten()
    {
        // The failure the ledger exists for, end to end. Storage refuses the
        // plan; the record of which pairs were touched is still on disk, because
        // it was written while the writes were happening rather than composed
        // after the last one.
        using var directory = new TemporaryDirectory();

        using (var ledger = RunLedger.Open(directory.Path, Started))
        {
            ledger.Append(new WriteResult(Write(), WriteOutcome.Restored, null));
        }

        // A directory cannot be created beneath a file, so this store cannot
        // publish anything, whatever the plan says.
        var blocked = Path.Join(directory.Path, "occupied");
        File.WriteAllText(blocked, "not a directory");
        var store = new PlanStore(Path.Join(blocked, "plans"));

        Assert.ThrowsAny<IOException>(() => store.Write(SealedPlan()));

        var lines = File.ReadAllLines(Single(directory));
        Assert.Equal("restored", JsonDocument.Parse(Assert.Single(lines)).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public void ListingADirectoryThatDoesNotExistIsEmptyRatherThanAnError()
    {
        Assert.Empty(RunLedger.List(Path.Join(Path.GetTempPath(), "userdata-restore-absent-" + Guid.NewGuid().ToString("N"))));
    }

    private static string Single(TemporaryDirectory directory) =>
        Assert.Single(RunLedger.List(directory.Path));

    /// <summary>A minimal sealed plan, so the store has something valid to refuse.</summary>
    private static PlanDocument SealedPlan()
    {
        var result = Scenario.Analyze([Scenario.Row(Scenario.UserA, "tt0133093")], [Scenario.Movie(MovieId)]);

        return PlanBuilder.Build(result, new PlanContext
        {
            PluginVersion = "1.0.0.0",
            TargetJellyfinVersion = "10.11.11",
            JellyfinPackageVersion = "10.11.11",
            TargetAbi = "10.11.11.0",
            ServerId = "test-server",
            ServerVersion = "10.11.11",
            CreatedUtc = Started,
            Options = Scenario.Options(),
            FingerprintBefore = new Verification.UserDataFingerprint(1, "abc"),
            FingerprintAfter = new Verification.UserDataFingerprint(1, "abc"),
            WriteResults = [.. result.Writes.Select(write => new WriteResult(write, WriteOutcome.Restored, null))],
        });
    }

    private static PlannedWrite Write() => new()
    {
        UserId = Scenario.UserA,
        ItemId = MovieId,
        State = new RecoveryState { Played = true, PlayCount = 1 },
        EvidenceRule = IdentityEvidenceRule.ImdbRule,
        SourceFingerprints = ["fingerprint"],
        SourceKeys = ["tt0133093"],
        TargetKeys = ["tt0133093"],
        SentinelFingerprints = ["fingerprint"],
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "userdata-restore-ledger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Temporary directory; a leak here is not worth failing a test.
            }
        }
    }
}
