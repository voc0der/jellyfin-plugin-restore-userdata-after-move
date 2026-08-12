using Jellyfin.Plugin.UserDataRestore.Core.Verification;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// The read-only proof (PLAN §2 acceptance).
/// </summary>
public class FingerprintTests
{
    [Fact]
    public void RowOrderDoesNotChangeTheFingerprint()
    {
        var forward = Build(["a", "b", "c"]);
        var reversed = Build(["c", "b", "a"]);

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void AChangedFieldChangesTheFingerprint()
    {
        Assert.NotEqual(Build(["a", "b"]), Build(["a", "B"]));
    }

    [Fact]
    public void AnAddedRowChangesTheFingerprint()
    {
        var before = Build(["a", "b"]);
        var after = Build(["a", "b", "c"]);

        Assert.NotEqual(before, after);
        Assert.NotEqual(before.RowCount, after.RowCount);
    }

    [Fact]
    public void ARemovedAndReAddedIdenticalRowIsNotCancelledOut()
    {
        // Additive combining, not XOR: a pair of identical digests must not
        // annihilate.
        Assert.NotEqual(Build(["a", "a"]), Build([]));
        Assert.Equal(2, Build(["a", "a"]).RowCount);
    }

    [Fact]
    public void AnEmptyTableFingerprintsAsZero()
    {
        var empty = Build([]);

        Assert.Equal(0, empty.RowCount);
        Assert.Equal(new string('0', 64), empty.Digest);
    }

    [Fact]
    public void EveryColumnParticipates()
    {
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var lastPlayed = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var baseline = new UserDataFingerprintBuilder();
        baseline.Add(itemId, userId, "603", true, 1, 10, true, lastPlayed, 9, true, 1, 2, lastPlayed);

        var subtitleChanged = new UserDataFingerprintBuilder();
        subtitleChanged.Add(itemId, userId, "603", true, 1, 10, true, lastPlayed, 9, true, 1, 3, lastPlayed);

        var likesChanged = new UserDataFingerprintBuilder();
        likesChanged.Add(itemId, userId, "603", true, 1, 10, true, lastPlayed, 9, false, 1, 2, lastPlayed);

        Assert.NotEqual(baseline.Build(), subtitleChanged.Build());
        Assert.NotEqual(baseline.Build(), likesChanged.Build());
    }

    [Fact]
    public void UnspecifiedAndUtcTimestampsFingerprintTheSame()
    {
        // Providers may return DateTimeKind.Unspecified for a UTC column; treating
        // that as local time would shift every comparison by the host offset.
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var utc = new UserDataFingerprintBuilder();
        utc.Add(itemId, userId, "k", true, 1, 0, false, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, null);

        var unspecified = new UserDataFingerprintBuilder();
        unspecified.Add(itemId, userId, "k", true, 1, 0, false, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), null, null, null, null, null);

        Assert.Equal(utc.Build(), unspecified.Build());
    }

    private static UserDataFingerprint Build(IEnumerable<string> lines)
    {
        var builder = new UserDataFingerprintBuilder();
        foreach (var line in lines)
        {
            builder.AddLine(line);
        }

        return builder.Build();
    }
}
