using System.Globalization;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin;

/// <summary>The libraries an analysis run may recover into, and their locations.</summary>
/// <param name="LibraryIds">The library IDs in scope.</param>
/// <param name="Locations">Every configured location of those libraries.</param>
public readonly record struct ResolvedLibraryScope(
    IReadOnlyList<Guid> LibraryIds,
    IReadOnlyList<string> Locations);

/// <summary>
/// Resolves which libraries are in scope, from configuration or from the server.
/// </summary>
/// <remarks>
/// Only movie and TV libraries are ever offered. Nothing in a music, photo, or
/// book library can be a recovery target — the analyzer handles movies and
/// episodes — so listing them is an invitation to tick a box that does nothing.
///
/// Every library in scope is one somebody ticked. There is no defaulting here:
/// this class resolves a selection, and a run with no selection has nothing to
/// resolve.
/// </remarks>
public sealed class LibraryScope(ILibraryManager libraryManager)
{
    private readonly ILibraryManager _libraryManager = libraryManager;

    /// <summary>
    /// Resolves the scope.
    /// </summary>
    /// <param name="configuredLibraryIds">The library IDs an operator selected, if any.</param>
    /// <returns>The libraries in scope and their locations.</returns>
    /// <exception cref="InvalidOperationException">
    /// The stored selection is not readable, or there is nothing selected to run
    /// against, so no scope can be derived from it.
    /// </exception>
    public ResolvedLibraryScope Resolve(IReadOnlyList<string>? configuredLibraryIds)
    {
        var folders = _libraryManager.GetVirtualFolders()
            .Where(folder => IsRecoverable(folder.CollectionType))
            .ToArray();

        var selection = LibrarySelection.Parse(configuredLibraryIds);

        // A selection that cannot be read is not an empty selection. Treating it
        // as one would answer "which libraries may this run write into?" with
        // silence, and a run that does nothing because it could not read its own
        // configuration looks exactly like a run that found nothing to do.
        if (selection.Kind == LibrarySelectionKind.Malformed)
        {
            var offending = string.Join(", ", selection.MalformedValues.Select(value => "\"" + value + "\""));
            throw new InvalidOperationException(
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
            throw new InvalidOperationException(
                folders.Length == 0
                    ? "No movie or TV libraries were found on this server, so there is nothing to select and "
                        + "nothing this plugin can recover into."
                    : "No libraries are selected, so there is nothing in scope and nothing to do. Open the "
                        + "plugin's settings page, tick the libraries you want recovered, and save. Nothing is "
                        + "ticked on a fresh install: this task writes to user data, and it writes only where "
                        + "it was told to.");
        }

        var selected = selection.LibraryIds.ToHashSet();
        var inScope = folders
            .Where(folder => Guid.TryParse(folder.ItemId, out var id) && selected.Contains(id))
            .ToArray();

        // Every ID here was posted by a page listing the server's own libraries,
        // so none of them matching means the libraries were removed or replaced
        // since. Saying so beats reporting an empty run against a page that still
        // shows ticked boxes.
        if (inScope.Length == 0)
        {
            throw new InvalidOperationException(
                "None of the selected libraries exist on this server any more, so there is nothing in scope. "
                + "Open the plugin's settings page, tick the libraries you want recovered, and save.");
        }

        var ids = inScope
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => !id.Equals(Guid.Empty))
            .Distinct()
            .ToArray();

        var locations = inScope
            .SelectMany(folder => folder.Locations ?? [])
            .ToArray();

        return new ResolvedLibraryScope(ids, locations);
    }

    /// <summary>
    /// Renders a library ID for a log line.
    /// </summary>
    /// <param name="id">The library ID.</param>
    /// <returns>The canonical string form.</returns>
    public static string Format(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static bool IsRecoverable(CollectionTypeOptions? collectionType) =>
        collectionType is CollectionTypeOptions.movies or CollectionTypeOptions.tvshows;
}
