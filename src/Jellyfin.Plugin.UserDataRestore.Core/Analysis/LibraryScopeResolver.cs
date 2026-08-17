using System.Globalization;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>One movie or TV library as the server currently reports it.</summary>
/// <param name="Id">The library's item ID.</param>
/// <param name="Locations">Every folder configured on that library.</param>
public readonly record struct RecoverableLibrary(Guid Id, IReadOnlyList<string> Locations);

/// <summary>
/// The libraries a run may recover into, or the reason it may not run at all.
/// </summary>
/// <param name="LibraryIds">The library IDs in scope. Empty when refused.</param>
/// <param name="Locations">Every configured location of those libraries.</param>
/// <param name="Refusal">
/// <see langword="null"/> when the selection resolved; otherwise what to tell the
/// operator instead of running.
/// </param>
public readonly record struct LibraryScopeResult(
    IReadOnlyList<Guid> LibraryIds,
    IReadOnlyList<string> Locations,
    string? Refusal);

/// <summary>
/// Turns a stored library selection into the scope a run acts on (DESIGN §6.1).
/// </summary>
/// <remarks>
/// <para>Separated from the Jellyfin adapter that feeds it so the decision can be
/// tested against every shape a selection can take, rather than only against the
/// shapes a live server is easy to arrange.</para>
/// <para>Every refusal here exists because its alternative is a run that reports
/// a clean, empty, entirely wrong result. That is this codebase's standing
/// failure mode, and a selection is exactly where it starts: nothing downstream
/// can tell "no library held anything to recover" from "the libraries the
/// operator chose were never looked at".</para>
/// </remarks>
public static class LibraryScopeResolver
{
    /// <summary>
    /// Resolves the stored selection against the libraries that exist now.
    /// </summary>
    /// <param name="configuredLibraryIds">The library IDs an operator selected, if any.</param>
    /// <param name="recoverable">Every movie and TV library on the server.</param>
    /// <returns>The scope, or the refusal that replaces it.</returns>
    public static LibraryScopeResult Resolve(
        IReadOnlyList<string>? configuredLibraryIds,
        IReadOnlyList<RecoverableLibrary> recoverable)
    {
        ArgumentNullException.ThrowIfNull(recoverable);

        var selection = LibrarySelection.Parse(configuredLibraryIds);

        // A selection that cannot be read is not an empty selection. Treating it
        // as one would answer "which libraries may this run write into?" with
        // silence, and a run that does nothing because it could not read its own
        // configuration looks exactly like a run that found nothing to do.
        if (selection.Kind == LibrarySelectionKind.Malformed)
        {
            var offending = string.Join(", ", selection.MalformedValues.Select(value => "\"" + value + "\""));
            return Refuse(
                "The configured libraries cannot be read: " + offending + " "
                + (selection.MalformedValues.Count == 1 ? "is not a library ID" : "are not library IDs") + ". "
                + "Refusing to run rather than guess at the scope. Open the plugin's settings page, tick the "
                + "libraries you want, and save.");
        }

        // Nothing ticked means nothing runs. This task writes to user data, so an
        // empty selection is read as the instruction it is rather than as a gap to
        // fill in with every library on the server — the reading that turned the
        // gesture for "narrow this to nothing" into the widest scope available.
        if (selection.Kind == LibrarySelectionKind.None)
        {
            return Refuse(
                recoverable.Count == 0
                    ? "No movie or TV libraries were found on this server, so there is nothing to select and "
                        + "nothing this plugin can recover into."
                    : "No libraries are selected, so there is nothing in scope and nothing to do. Open the "
                        + "plugin's settings page, tick the libraries you want recovered, and save. Nothing is "
                        + "ticked on a fresh install: this task writes to user data, and it writes only where "
                        + "it was told to.");
        }

        var present = recoverable.Select(library => library.Id).ToHashSet();
        var missing = selection.LibraryIds.Where(id => !present.Contains(id)).ToArray();

        // Every ID here was posted by a page listing the server's own libraries,
        // so none of them matching means the libraries were removed or replaced
        // since. Saying so beats reporting an empty run against a page that still
        // shows ticked boxes.
        if (missing.Length == selection.LibraryIds.Count)
        {
            return Refuse(
                "None of the selected libraries exist on this server any more, so there is nothing in scope. "
                + "Open the plugin's settings page, tick the libraries you want recovered, and save.");
        }

        // The partial case, and it gets the same answer rather than a lesser one.
        // Deleting and recreating a library gives it a new ID, so a two-library
        // selection silently becomes a one-library run: the survivor is recovered,
        // the replacement is skipped in this run and every later one, and nothing
        // says so while the settings page still shows both boxes ticked. Narrowing
        // somebody's write scope without telling them is not a smaller version of
        // getting it wrong.
        if (missing.Length > 0)
        {
            return Refuse(
                "Refusing to run: " + Describe(missing.Length, selection.LibraryIds.Count) + " no longer "
                + "exists on this server (" + string.Join(", ", missing.Select(Format)) + "). Recreating a "
                + "library gives it a new ID, so a selection can go stale without anything on the settings "
                + "page changing. Running anyway would quietly recover only the libraries that survived. Open "
                + "the plugin's settings page, tick the libraries you want recovered, and save.");
        }

        var inScope = recoverable.Where(library => present.Contains(library.Id) && selection.LibraryIds.Contains(library.Id));

        return new LibraryScopeResult(
            [.. selection.LibraryIds],
            [.. inScope.SelectMany(library => library.Locations ?? [])],
            null);
    }

    /// <summary>
    /// Renders a library ID for a message or a log line.
    /// </summary>
    /// <param name="id">The library ID.</param>
    /// <returns>The canonical string form.</returns>
    public static string Format(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static LibraryScopeResult Refuse(string message) => new([], [], message);

    private static string Describe(int missing, int selected) => missing == 1
        ? (selected == 1 ? "the selected library" : "one of the selected libraries")
        : string.Create(CultureInfo.InvariantCulture, $"{missing} of the {selected} selected libraries");
}
