using System.Globalization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.UserDataRestore.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Plugin.UserDataRestore.Tests.Fixtures;

/// <summary>
/// A real <see cref="JellyfinDbContext"/> over a throwaway SQLite database.
/// </summary>
/// <remarks>
/// <para>The host's own context and model, not a stand-in, because the thing
/// worth testing about this plugin's reads is the half that only exists at
/// runtime. Every query here is LINQ some provider has to translate, and a
/// translation it cannot do throws when the query runs — on a server, part-way
/// through a restore, where the failure surfaces as a run that stopped rather
/// than as anything pointing at the expression that caused it. The compiler has
/// nothing to say about it and neither did any test, until this one.</para>
/// <para>One connection, held open for the fixture's life: an in-memory SQLite
/// database exists only as long as a connection to it does, and
/// <see cref="UserDataReader"/> opens and disposes a context per call by design.
/// The factory hands every call a new context over that same connection, which is
/// what the host's pooled factory does too.</para>
/// <para>Rows go in through SQL rather than through the model. <c>UserData</c>
/// carries required navigations to a user and an item, and satisfying them means
/// building two more entity graphs whose own required members have nothing to do
/// with anything under test — a fixture testing itself. What the reader reads is
/// a row in a table, so a row in the table is what these put there.</para>
/// <para><b>What this is not.</b> <see cref="IJellyfinDatabaseProvider"/> is
/// substituted, so the server's own provider conventions — the UTC handling
/// Jellyfin's SQLite provider layers onto the model among them — are absent. This
/// is the real context class, the real entity model, and the real provider doing
/// the translating; it is not a fully host-configured model, and a defect living
/// only in a convention the host adds would pass here. The reader normalizes
/// every timestamp it returns rather than trusting the model to, which is why the
/// assertions about <see cref="DateTimeKind"/> below still mean something — they
/// are testing that normalization, not the convention.</para>
/// </remarks>
public sealed class UserDataDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    private UserDataDatabase()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();

        // Off for the same reason the rows are inserted raw: the users and items
        // these rows point at were deleted, which is the entire subject.
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=OFF");

        Factory = Substitute.For<IDbContextFactory<JellyfinDbContext>>();
        Factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(CreateContext()));
        Factory.CreateDbContext().Returns(_ => CreateContext());
    }

    public IDbContextFactory<JellyfinDbContext> Factory { get; }

    public UserDataReader Reader => new(Factory);

    public static UserDataDatabase Create() => new();

    /// <summary>Adds one detached row, the way a deletion leaves one behind.</summary>
    public void AddDetached(
        Guid userId,
        string key,
        bool played = true,
        int playCount = 3,
        long ticks = 12345,
        bool favorite = true,
        double? rating = 9,
        DateTime? retention = null) =>
        AddRow(UserDataReader.SentinelItemId, userId, key, played, playCount, ticks, favorite, rating, retention);

    /// <summary>Adds one live row against a real item.</summary>
    public void AddCurrent(
        Guid userId,
        Guid itemId,
        string key,
        bool played = true,
        int playCount = 3,
        long ticks = 12345,
        bool favorite = true,
        double? rating = 9) =>
        AddRow(itemId, userId, key, played, playCount, ticks, favorite, rating, null);

    /// <summary>The fingerprints of every detached row this user holds under these keys.</summary>
    public IReadOnlyList<string> DetachedFingerprints(Guid userId, IReadOnlyList<string> keys) =>
        [.. Reader.ReadDetachedAsync(userId, keys, CancellationToken.None)
            .GetAwaiter().GetResult()
            .Select(row => row.Fingerprint)
            .Order(StringComparer.Ordinal)];

    /// <summary>The fingerprint of the one detached row under a key.</summary>
    public string DetachedFingerprint(Guid userId, string key) =>
        DetachedFingerprints(userId, [key]).Single();

    /// <summary>Removes the detached row under a key, as Jellyfin's cleanup does.</summary>
    public void RemoveDetached(Guid userId, string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM UserData WHERE ItemId = $item AND UserId = $user AND CustomDataKey = $key";
        command.Parameters.AddWithValue("$item", UserDataReader.SentinelItemId);
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    public int RowCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM UserData";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Dispose() => _connection.Dispose();

    private void AddRow(
        Guid itemId,
        Guid userId,
        string key,
        bool played,
        int playCount,
        long ticks,
        bool favorite,
        double? rating,
        DateTime? retention)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO UserData (ItemId, UserId, CustomDataKey, Played, PlayCount, PlaybackPositionTicks, "
            + "IsFavorite, Rating, LastPlayedDate, RetentionDate, Likes, AudioStreamIndex, SubtitleStreamIndex) "
            + "VALUES ($item, $user, $key, $played, $count, $ticks, $favorite, $rating, $lastPlayed, $retention, NULL, NULL, NULL)";
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$played", played);
        command.Parameters.AddWithValue("$count", playCount);
        command.Parameters.AddWithValue("$ticks", ticks);
        command.Parameters.AddWithValue("$favorite", favorite);
        command.Parameters.AddWithValue("$rating", (object?)rating ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastPlayed", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        command.Parameters.AddWithValue(
            "$retention", (object?)retention ?? new DateTime(2026, 8, 12, 14, 22, 9, DateTimeKind.Utc));
        command.ExecuteNonQuery();
    }

    private JellyfinDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new JellyfinDbContext(
            options,
            Substitute.For<ILogger<JellyfinDbContext>>(),
            Substitute.For<IJellyfinDatabaseProvider>(),
            Substitute.For<IEntityFrameworkCoreLockingBehavior>());
    }
}
