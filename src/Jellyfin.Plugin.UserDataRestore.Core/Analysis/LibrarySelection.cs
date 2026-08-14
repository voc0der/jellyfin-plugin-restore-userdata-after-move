namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>What the persisted library selection turned out to be.</summary>
public enum LibrarySelectionKind
{
    /// <summary>Nothing was selected, so nothing is in scope.</summary>
    None,

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
    /// Nothing selected means nothing in scope. This plugin writes to user data,
    /// so the only scope it will ever act on is one somebody ticked; an empty
    /// selection is an answer, not a gap to be filled in.
    ///
    /// The distinction this draws is between that empty answer and no readable
    /// answer at all. Dropping unparseable values and then asking whether
    /// anything is left cannot tell "nobody has ticked a library" — which
    /// correctly means no run — from "somebody ticked libraries and the stored
    /// form of that choice is corrupt", which means the run would silently do
    /// nothing while the page still shows a scope. That is this codebase's other
    /// standing failure: an empty result indistinguishable from a correct one.
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
            return new LibrarySelection(LibrarySelectionKind.None, [], []);
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
