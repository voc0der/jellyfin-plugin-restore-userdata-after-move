using System.Globalization;

namespace Jellyfin.Plugin.UserDataRestore.Core.Model;

/// <summary>
/// One <c>UserData</c> row whose <c>ItemId</c> is the sentinel
/// <c>00000000-0000-0000-0000-000000000001</c> — a snapshot Jellyfin kept after
/// the item that owned it was removed (DESIGN §2, §7.1).
/// </summary>
public sealed record DetachedUserDataRow
{
    /// <summary>Gets the user the snapshot belongs to.</summary>
    public required Guid UserId { get; init; }

    /// <summary>
    /// Gets the key exactly as stored. Never normalized, lowercased, stripped of
    /// prefixes, or parsed as a provider ID (DESIGN §2.1).
    /// </summary>
    public required string? CustomDataKey { get; init; }

    /// <summary>Gets the retention stamp Jellyfin applied when it detached the row.</summary>
    public DateTime? RetentionDate { get; init; }

    /// <summary>Gets a value indicating whether the item was played.</summary>
    public bool Played { get; init; }

    /// <summary>Gets the play count.</summary>
    public int PlayCount { get; init; }

    /// <summary>Gets the resume position in ticks.</summary>
    public long PlaybackPositionTicks { get; init; }

    /// <summary>Gets a value indicating whether the item was a favorite.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Gets the last played timestamp.</summary>
    public DateTime? LastPlayedDate { get; init; }

    /// <summary>Gets the user rating.</summary>
    public double? Rating { get; init; }

    /// <summary>Gets the like flag. Reported only; not recovered in v1.</summary>
    public bool? Likes { get; init; }

    /// <summary>Gets the selected audio stream. Reported only; not recovered in v1 (DESIGN §9.2).</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Gets the selected subtitle stream. Reported only; not recovered in v1.</summary>
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>Gets the recoverable subset of this row.</summary>
    public RecoveryState State => new()
    {
        Played = Played,
        PlayCount = PlayCount,
        PlaybackPositionTicks = PlaybackPositionTicks,
        IsFavorite = IsFavorite,
        LastPlayedDate = DateTimeNormalization.ToUtc(LastPlayedDate),
        Rating = Rating,
    };

    /// <summary>
    /// Gets a stable rendering of every field that matters for safety.
    /// </summary>
    /// <remarks>
    /// Two jobs. It is what each write is re-checked against immediately before
    /// the save (DESIGN §9.1): analysis and writing happen in one pass, but the
    /// sentinel is not this plugin's alone, and a row can be deleted by Jellyfin's
    /// own cleanup or replaced by a newer snapshot in the gap. And it is the
    /// plan's identifier for a row — the thing that lets one run's artifact be
    /// compared against another's, and lets a reader tell two otherwise identical
    /// stranded snapshots apart. It covers the report-only fields for both
    /// purposes: a row whose stream indexes changed is a different row.
    /// </remarks>
    public string Fingerprint => string.Create(
        CultureInfo.InvariantCulture,
        $"user={UserId:N};key={CustomDataKey};retention={FormatDate(RetentionDate)};{RecoveryStateComparer.Render(State)};likes={FormatBool(Likes)};audio={FormatInt(AudioStreamIndex)};subtitle={FormatInt(SubtitleStreamIndex)}");

    /// <summary>
    /// Gets everything this row says, apart from the key it says it under.
    /// </summary>
    /// <remarks>
    /// Jellyfin fans one save out across every key the item reported, so a single
    /// item's stranded state arrives as several rows identical in every field but
    /// <see cref="CustomDataKey"/>. This is what makes two of them recognisable as
    /// two renderings of one snapshot rather than two snapshots that happen to
    /// look alike — and therefore what makes it a contradiction when they resolve
    /// to different current items.
    /// </remarks>
    public string PayloadFingerprint => string.Create(
        CultureInfo.InvariantCulture,
        $"user={UserId:N};retention={FormatDate(RetentionDate)};{RecoveryStateComparer.Render(State)};likes={FormatBool(Likes)};audio={FormatInt(AudioStreamIndex)};subtitle={FormatInt(SubtitleStreamIndex)}");

    private static string FormatDate(DateTime? value) =>
        value is null ? "-" : DateTimeNormalization.ToUtc(value.Value).ToString("O", CultureInfo.InvariantCulture);

    private static string FormatBool(bool? value) => value is null ? "-" : value.Value ? "1" : "0";

    private static string FormatInt(int? value) => value is null ? "-" : value.Value.ToString(CultureInfo.InvariantCulture);
}
