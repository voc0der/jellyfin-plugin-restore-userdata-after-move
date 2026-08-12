namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// Turns "the operator configured nothing" into a concrete scope (DESIGN §6.1).
/// </summary>
/// <remarks>
/// <para>The scope used to be two required fields. Requiring them meant the first
/// run of a fresh install always failed, and the path field in particular asked
/// for something the server already knows — the libraries' own locations — in a
/// form that is easy to get wrong. Typing a host path where the server sees a
/// container path excluded every item and reported "nothing recoverable", which is
/// indistinguishable from a correct empty answer.</para>
/// <para>So both fields default from the server and stay editable for the one case
/// that needs them: a library spanning two roots where only one is the
/// destination. The resolved values are still what the plan records, so an audit
/// reads the same whether they were typed or derived.</para>
/// </remarks>
public static class ScopeDefaults
{
    /// <summary>
    /// Resolves the final path prefixes.
    /// </summary>
    /// <param name="configured">What the operator typed, if anything.</param>
    /// <param name="libraryLocations">The locations of the in-scope libraries.</param>
    /// <returns>Normalized prefixes: trimmed, deduplicated, ordered.</returns>
    public static IReadOnlyList<string> ResolvePrefixes(
        IReadOnlyList<string>? configured,
        IEnumerable<string>? libraryLocations)
    {
        var typed = Normalize(configured);
        return typed.Count > 0 ? typed : Normalize(libraryLocations);
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return [];
        }

        return
        [
            .. paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(TrimTrailingSeparators)
                .Where(path => path.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
    }

    // A configured location may arrive with a trailing separator; the prefix test
    // is component-aware and would not match "/data/tv/" against "/data/tv/show".
    // The root itself is left alone, since trimming it away leaves nothing.
    private static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.Trim();
        var result = trimmed.TrimEnd('/', '\\');
        return result.Length == 0 ? trimmed : result;
    }
}
