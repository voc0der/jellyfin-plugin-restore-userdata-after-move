using System.Reflection;

namespace Jellyfin.Plugin.UserDataRestore;

/// <summary>
/// What server this build is for, as stamped in by MSBuild.
/// </summary>
/// <remarks>
/// Single source of truth: the csproj sets the package version, the runtime
/// version, and the manifest ABI together, and they arrive here as assembly
/// metadata. Nothing hardcodes a version in two places where they could drift.
/// </remarks>
public static class BuildInfo
{
    private static readonly Assembly Self = typeof(BuildInfo).Assembly;

    /// <summary>Gets the exact Jellyfin version this build supports at runtime.</summary>
    public static string JellyfinRuntimeVersion { get; } = Read("JellyfinRuntimeVersion");

    /// <summary>Gets the Jellyfin NuGet package version this build compiled against.</summary>
    public static string JellyfinPackageVersion { get; } = Read("JellyfinPackageVersion");

    /// <summary>Gets the manifest ABI this build declares.</summary>
    public static string JellyfinTargetAbi { get; } = Read("JellyfinTargetAbi");

    /// <summary>Gets the plugin version.</summary>
    public static string PluginVersion { get; } = Self.GetName().Version?.ToString() ?? "0.0.0.0";

    private static string Read(string key) => Self
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
        ?.Value ?? string.Empty;
}
