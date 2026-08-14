using System.Globalization;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin;

/// <summary>The libraries an analysis run may recover into, and their locations.</summary>
/// <param name="LibraryIds">The library IDs in scope.</param>
/// <param name="Locations">Every configured location of those libraries.</param>
/// <param name="Defaulted">Whether the operator picked these or they were defaulted.</param>
public readonly record struct ResolvedLibraryScope(
    IReadOnlyList<Guid> LibraryIds,
    IReadOnlyList<string> Locations,
    bool Defaulted);

/// <summary>
/// Resolves which libraries are in scope, from configuration or from the server.
/// </summary>
/// <remarks>
/// Only movie and TV libraries are ever offered or defaulted to. Nothing in a
/// music, photo, or book library can be a recovery target — the analyzer handles
/// movies and episodes — so listing them is an invitation to tick a box that does
/// nothing.
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
    /// The stored selection is not readable, so no scope can be derived from it.
    /// </exception>
    public ResolvedLibraryScope Resolve(IReadOnlyList<string>? configuredLibraryIds)
    {
        var folders = _libraryManager.GetVirtualFolders()
            .Where(folder => IsRecoverable(folder.CollectionType))
            .ToArray();

        var selection = LibrarySelection.Parse(configuredLibraryIds);

        // A selection that cannot be read is not an absent selection. Treating it
        // as one would answer "which libraries may this run write into?" with
        // "all of them" on the strength of a value the plugin just failed to
        // parse, which is the one direction a scope error must never go.
        if (selection.Kind == LibrarySelectionKind.Malformed)
        {
            var offending = string.Join(", ", selection.MalformedValues.Select(value => "\"" + value + "\""));
            throw new InvalidOperationException(
                "The configured libraries cannot be read: " + offending + " "
                + (selection.MalformedValues.Count == 1 ? "is not a library ID" : "are not library IDs") + ". "
                + "Refusing to run rather than guess at the scope, because the alternative reading of an "
                + "unreadable selection is \"every library\". Open the plugin's settings page, tick the "
                + "libraries you want, and save.");
        }

        // Nothing chosen means every library the plugin could ever recover into.
        // Fail-closed made sense while this was a required field; as a default it
        // only guarantees that the first run of a fresh install does nothing.
        var defaulted = selection.Kind == LibrarySelectionKind.Defaulted;
        var selected = selection.LibraryIds.ToHashSet();
        var inScope = folders
            .Where(folder => defaulted || (Guid.TryParse(folder.ItemId, out var id) && selected.Contains(id)))
            .ToArray();

        var ids = inScope
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => !id.Equals(Guid.Empty))
            .Distinct()
            .ToArray();

        var locations = inScope
            .SelectMany(folder => folder.Locations ?? [])
            .ToArray();

        return new ResolvedLibraryScope(ids, locations, defaulted);
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
