using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Jellyfin.Plugin.UserDataRestore.Core.Model;

/// <summary>
/// Compares two <see cref="RecoveryState"/> values.
/// </summary>
/// <remarks>
/// <para>Two comparisons are needed and they are not the same comparison.</para>
/// <para><see cref="Exact"/> answers "did Jellyfin write these two detached rows
/// in the same operation?" (DESIGN §7.4, which says <em>identical</em>). Any
/// tolerance there would silently merge two different moments in time.</para>
/// <para><see cref="Semantic"/> answers "does the current item already hold this
/// state?" (DESIGN §7.5 <c>already_applied</c>, §4.10 idempotency). That one has
/// to survive a round trip through the database and through
/// <c>IUserDataManager</c>, so it allows sub-second date drift and floating
/// point noise in the rating.</para>
/// </remarks>
public sealed class RecoveryStateComparer : IEqualityComparer<RecoveryState>
{
    private readonly long _dateToleranceTicks;
    private readonly double _ratingTolerance;

    private RecoveryStateComparer(TimeSpan dateTolerance, double ratingTolerance)
    {
        _dateToleranceTicks = dateTolerance.Ticks;
        _ratingTolerance = ratingTolerance;
    }

    /// <summary>Gets a bit-for-bit comparer, used to collapse redundant source rows.</summary>
    public static RecoveryStateComparer Exact { get; } = new(TimeSpan.Zero, 0d);

    /// <summary>Gets a round-trip-tolerant comparer, used against current item state.</summary>
    public static RecoveryStateComparer Semantic { get; } = new(TimeSpan.FromSeconds(1), 1e-6);

    /// <inheritdoc />
    public bool Equals(RecoveryState? x, RecoveryState? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return x.Played == y.Played
            && x.PlayCount == y.PlayCount
            && x.PlaybackPositionTicks == y.PlaybackPositionTicks
            && x.IsFavorite == y.IsFavorite
            && DatesEqual(x.LastPlayedDate, y.LastPlayedDate)
            && RatingsEqual(x.Rating, y.Rating);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only the fields compared without tolerance contribute, so the hash stays
    /// consistent with <see cref="Equals(RecoveryState?, RecoveryState?)"/> for
    /// both comparers.
    /// </remarks>
    public int GetHashCode([DisallowNull] RecoveryState obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return HashCode.Combine(obj.Played, obj.PlayCount, obj.PlaybackPositionTicks, obj.IsFavorite);
    }

    /// <summary>
    /// Renders a state as a stable single-line string for fingerprints and logs.
    /// </summary>
    /// <param name="state">The state to render.</param>
    /// <returns>A round-trippable, culture-independent rendering.</returns>
    public static string Render(RecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"played={state.Played};count={state.PlayCount};ticks={state.PlaybackPositionTicks};fav={state.IsFavorite};last={FormatDate(state.LastPlayedDate)};rating={FormatRating(state.Rating)}");
    }

    private static string FormatDate(DateTime? value) =>
        value is null ? "-" : DateTimeNormalization.ToUtc(value.Value).ToString("O", CultureInfo.InvariantCulture);

    private static string FormatRating(double? value) =>
        value is null ? "-" : value.Value.ToString("R", CultureInfo.InvariantCulture);

    private bool DatesEqual(DateTime? a, DateTime? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var delta = Math.Abs(DateTimeNormalization.ToUtc(a.Value).Ticks - DateTimeNormalization.ToUtc(b.Value).Ticks);
        return delta <= _dateToleranceTicks;
    }

    private bool RatingsEqual(double? a, double? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return Math.Abs(a.Value - b.Value) <= _ratingTolerance;
    }
}
