namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// Component-aware path prefix tests (DESIGN §6.1).
/// </summary>
/// <remarks>
/// <c>/data/library/tv2</c> is not beneath <c>/data/library/tv</c>. A plain
/// <c>StartsWith</c> says otherwise, which is how a recovery lands in the wrong
/// library.
/// </remarks>
public static class PathScope
{
    /// <summary>
    /// Determines whether a path is the prefix itself or sits beneath it.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <param name="prefix">The allowed prefix.</param>
    /// <param name="comparison">Host path comparison semantics.</param>
    /// <returns><see langword="true"/> if <paramref name="path"/> is within <paramref name="prefix"/>.</returns>
    public static bool IsBeneath(string? path, string? prefix, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        var normalizedPath = Normalize(path);
        var normalizedPrefix = Normalize(prefix);

        if (normalizedPrefix.Length == 0)
        {
            return false;
        }

        if (string.Equals(normalizedPath, normalizedPrefix, comparison))
        {
            return true;
        }

        // Root prefixes ("/" or "C:\") already end in a separator; adding another
        // would never match.
        var boundary = normalizedPrefix[^1] == Path.DirectorySeparatorChar
            ? normalizedPrefix
            : normalizedPrefix + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(boundary, comparison);
    }

    /// <summary>
    /// Determines whether a path is beneath any of several prefixes.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <param name="prefixes">The allowed prefixes.</param>
    /// <param name="comparison">Host path comparison semantics.</param>
    /// <returns><see langword="true"/> if any prefix contains the path.</returns>
    public static bool IsBeneathAny(string? path, IEnumerable<string> prefixes, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        return prefixes.Any(prefix => IsBeneath(path, prefix, comparison));
    }

    /// <summary>
    /// Collapses separators and relative segments so two spellings of the same
    /// location compare equal.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>The normalized path, without a trailing separator.</returns>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var candidate = path.Trim();
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        candidate = candidate
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd();

        // GetFullPath collapses "." and ".." and duplicate separators. It resolves
        // a relative path against the process directory, which would be a
        // meaningless comparison, so relative input is left alone and simply fails
        // to match any absolute prefix.
        if (Path.IsPathRooted(candidate))
        {
            try
            {
                candidate = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Keep the original spelling; an unparseable path matches nothing.
            }
        }

        if (candidate.Length > 1)
        {
            candidate = candidate.TrimEnd(Path.DirectorySeparatorChar);
            if (candidate.Length == 0)
            {
                return Path.DirectorySeparatorChar.ToString();
            }
        }

        return candidate;
    }
}
