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
/// Asks the server which movie and TV libraries exist, and hands the answer to
/// <see cref="LibraryScopeResolver"/>.
/// </summary>
/// <remarks>
/// Only movie and TV libraries are ever offered. Nothing in a music, photo, or
/// book library can be a recovery target — the analyzer handles movies and
/// episodes — so listing them is an invitation to tick a box that does nothing.
///
/// Every library in scope is one somebody ticked. There is no defaulting here:
/// this class resolves a selection, and a run with no selection has nothing to
/// resolve. Which selections resolve and which are refused is decided in the
/// core, where it can be tested without a server; all this does is the
/// translation either side of that decision.
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
    /// The stored selection is not readable, is empty, or names a library this
    /// server no longer has, so no scope can honestly be derived from it.
    /// </exception>
    public ResolvedLibraryScope Resolve(IReadOnlyList<string>? configuredLibraryIds)
    {
        var recoverable = _libraryManager.GetVirtualFolders()
            .Where(folder => IsRecoverable(folder.CollectionType))
            .Select(folder => (Parsed: Guid.TryParse(folder.ItemId, out var id), Id: id, folder.Locations))
            .Where(folder => folder.Parsed && !folder.Id.Equals(Guid.Empty))
            .Select(folder => new RecoverableLibrary(folder.Id, folder.Locations ?? []))
            .ToArray();

        var resolved = LibraryScopeResolver.Resolve(configuredLibraryIds, recoverable);

        return resolved.Refusal is { } refusal
            ? throw new InvalidOperationException(refusal)
            : new ResolvedLibraryScope(resolved.LibraryIds, resolved.Locations);
    }

    /// <summary>
    /// Renders a library ID for a log line.
    /// </summary>
    /// <param name="id">The library ID.</param>
    /// <returns>The canonical string form.</returns>
    public static string Format(Guid id) => LibraryScopeResolver.Format(id);

    private static bool IsRecoverable(CollectionTypeOptions? collectionType) =>
        collectionType is CollectionTypeOptions.movies or CollectionTypeOptions.tvshows;
}
