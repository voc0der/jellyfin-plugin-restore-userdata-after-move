namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>What the persisted library selection turned out to be.</summary>
public enum LibrarySelectionKind
{
    /// <summary>Nothing was configured, so every recoverable library is in scope.</summary>
    Defaulted,

    /// <summary>An operator chose these libraries.</summary>
    Explicit,

    /// <summary>Values were configured, but at least one is not a library ID.</summary>
    Malformed,
}

/// <summary>
/// The persisted library selection, read without guessing at it.
/// </summary>
/// <param name="Kind">Whether the selection defaulted, was chosen, or is corrupt.</param>
/// <param name="LibraryIds">The parsed IDs. Empty unless <paramref name="Kind"/> is
/// <see cref="LibrarySelectionKind.Explicit"/>.</param>
/// <param name="MalformedValues">The entries that are not library IDs, exactly as stored.</param>
public readonly record struct LibrarySelection(
    LibrarySelectionKind Kind,
    IReadOnlyList<Guid> LibraryIds,
    IReadOnlyList<string> MalformedValues)
{
    /// <summary>
    /// Reads a persisted selection.
    /// </summary>
    /// <param name="configured">The stored values, as the configuration page posts them.</param>
    /// <returns>The selection, or the reason it cannot be read.</returns>
    /// <remarks>
    /// The distinction this draws is the whole point. Dropping unparseable values
    /// and then asking whether anything is left cannot tell "nobody has chosen
    /// libraries yet" — which correctly means all of them — from "somebody chose
    /// libraries and the stored form of that choice is corrupt", which means
    /// nothing safe at all. Collapsing the second into the first *widens* the
    /// scope of a run that writes to user data, on the strength of a value the
    /// plugin just admitted it could not read.
    ///
    /// So one bad entry condemns the whole selection rather than being quietly
    /// skipped. A partial read is still a guess about which libraries were meant,
    /// and the only thing that writes this field is a page that posts IDs the
    /// server itself supplied: an entry that is not one did not come from an
    /// operator ticking a box, and the honest answer is to say so and stop.
    /// </remarks>
    public static LibrarySelection Parse(IReadOnlyList<string>? configured)
    {
        if (configured is null || configured.Count == 0)
        {
            return new LibrarySelection(LibrarySelectionKind.Defaulted, [], []);
        }

        var ids = new List<Guid>(configured.Count);
        var malformed = new List<string>();

        foreach (var value in configured)
        {
            // Guid.TryParse accepts several renderings; the page posts "D" form.
            // Empty is malformed rather than absent: the array said there was a
            // value here, and there is not one.
            if (Guid.TryParse(value, out var parsed) && !parsed.Equals(Guid.Empty))
            {
                ids.Add(parsed);
            }
            else
            {
                malformed.Add(value ?? string.Empty);
            }
        }

        return malformed.Count > 0
            ? new LibrarySelection(LibrarySelectionKind.Malformed, [], malformed)
            : new LibrarySelection(LibrarySelectionKind.Explicit, [.. ids.Distinct()], []);
    }
}
