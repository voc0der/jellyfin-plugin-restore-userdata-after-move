namespace Jellyfin.Plugin.UserDataRestore.Core.Model;

/// <summary>
/// Puts database-sourced timestamps on one footing.
/// </summary>
/// <remarks>
/// Jellyfin stores UTC, but the value an EF provider hands back may carry
/// <see cref="DateTimeKind.Unspecified"/>. Comparing those against a
/// <see cref="DateTimeKind.Utc"/> value silently shifts by the local offset, so
/// every timestamp entering the core is normalized once, here.
/// </remarks>
public static class DateTimeNormalization
{
    /// <summary>
    /// Reinterprets an unspecified-kind timestamp as UTC and converts a local one.
    /// </summary>
    /// <param name="value">The timestamp to normalize.</param>
    /// <returns>The equivalent UTC timestamp.</returns>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// Normalizes an optional timestamp.
    /// </summary>
    /// <param name="value">The timestamp to normalize, or <see langword="null"/>.</param>
    /// <returns>The equivalent UTC timestamp, or <see langword="null"/>.</returns>
    public static DateTime? ToUtc(DateTime? value) => value is null ? null : ToUtc(value.Value);
}
