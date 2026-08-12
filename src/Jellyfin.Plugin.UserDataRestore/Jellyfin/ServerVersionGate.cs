using System.Globalization;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin;

/// <summary>
/// The exact runtime version check (DESIGN §11).
/// </summary>
/// <remarks>
/// <para>Not optional and not belt-and-braces. §17.3 established that Jellyfin
/// treats <c>targetAbi</c> as a minimum-version filter: the 10.11.11-targeted
/// probe loaded on 12.0 RC5, registered its task, ran, and bound to the host's
/// 12.0 assemblies without a single warning. This plugin depends on database
/// entities and implementation packages, so "it loaded" is not "it is
/// compatible".</para>
/// <para><b>How exact this can be.</b> The comparison is on
/// <c>major.minor.build</c>, and that is the limit of what the host exposes:
/// Jellyfin's assemblies carry no prerelease marker anywhere — 12.0 RC5 reports
/// <c>12.0.0</c> as its assembly version, file version, <em>and</em> informational
/// version, indistinguishable from RC4 or from stable 12.0.0. A build made
/// against RC5 therefore also runs on any other 12.0.0-reporting server. That gap
/// is closed by <see cref="DatabaseModelGate"/>, which checks the entity shape
/// this plugin actually depends on rather than the label the server puts on
/// it.</para>
/// </remarks>
public static class ServerVersionGate
{
    /// <summary>
    /// Checks the running server against the version this build was made for.
    /// </summary>
    /// <param name="running">The running server version.</param>
    /// <param name="message">An explanation, on failure.</param>
    /// <returns><see langword="true"/> when the versions match.</returns>
    public static bool IsSupported(Version? running, out string message)
    {
        if (!Version.TryParse(BuildInfo.JellyfinRuntimeVersion, out var supported))
        {
            message = string.Create(
                CultureInfo.InvariantCulture,
                $"This build declares an unparseable target version ('{BuildInfo.JellyfinRuntimeVersion}'). Refusing to run.");
            return false;
        }

        if (running is null)
        {
            message = "The running Jellyfin version could not be determined. Refusing to run.";
            return false;
        }

        // Revision is ignored: Jellyfin reports 10.11.11 as 10.11.11.0, and the
        // 12.0 RC5 server reports 12.0.0 with no prerelease tag.
        if (running.Major == supported.Major && running.Minor == supported.Minor && running.Build == supported.Build)
        {
            message = string.Empty;
            return true;
        }

        message = string.Create(CultureInfo.InvariantCulture, $"This plugin build is for Jellyfin {supported.ToString(3)} (built against package {BuildInfo.JellyfinPackageVersion}) but the server is {running.ToString(3)}. Jellyfin's targetAbi is only a minimum-version check, so the wrong build loads without complaint. Install the build matching this server and try again.");
        return false;
    }

    /// <summary>
    /// Throws unless the running server is the one this build supports.
    /// </summary>
    /// <param name="running">The running server version.</param>
    /// <exception cref="InvalidOperationException">The versions do not match.</exception>
    public static void EnsureSupported(Version? running)
    {
        if (!IsSupported(running, out var message))
        {
            throw new InvalidOperationException(message);
        }
    }
}
