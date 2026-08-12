using System.Globalization;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// Rejects detached rows that cannot be trusted as a recovery source
/// (DESIGN §7.4).
/// </summary>
public static class SourceStateValidator
{
    /// <summary>How far past the analysis clock a last-played date may sit before it is impossible.</summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);

    /// <summary>The earliest plausible last-played date. Jellyfin did not exist in 1899.</summary>
    private static readonly DateTime EarliestPlausibleDate = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Validates one detached row.
    /// </summary>
    /// <param name="row">The row to validate.</param>
    /// <param name="options">Analysis options, for the clock.</param>
    /// <param name="violation">A short description of the first problem found.</param>
    /// <returns><see langword="true"/> when the row is usable.</returns>
    public static bool TryValidate(DetachedUserDataRow row, AnalysisOptions options, out string? violation)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(row.CustomDataKey))
        {
            violation = "missing_key";
            return false;
        }

        if (row.UserId.Equals(Guid.Empty))
        {
            violation = "empty_user_id";
            return false;
        }

        if (row.PlayCount < 0)
        {
            violation = string.Create(CultureInfo.InvariantCulture, $"negative_play_count:{row.PlayCount}");
            return false;
        }

        if (row.PlaybackPositionTicks < 0)
        {
            violation = string.Create(CultureInfo.InvariantCulture, $"negative_position:{row.PlaybackPositionTicks}");
            return false;
        }

        if (row.Rating is { } rating && (double.IsNaN(rating) || double.IsInfinity(rating) || rating < 0 || rating > 10))
        {
            violation = string.Create(CultureInfo.InvariantCulture, $"rating_out_of_range:{rating:R}");
            return false;
        }

        if (row.LastPlayedDate is { } lastPlayed)
        {
            var normalized = DateTimeNormalization.ToUtc(lastPlayed);
            if (normalized < EarliestPlausibleDate || normalized > DateTimeNormalization.ToUtc(options.NowUtc) + FutureTolerance)
            {
                violation = string.Create(CultureInfo.InvariantCulture, $"implausible_last_played:{normalized:O}");
                return false;
            }
        }

        violation = null;
        return true;
    }
}
