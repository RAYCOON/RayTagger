using System.Reflection;

namespace RayTagger.Hosting;

/// <summary>
/// Builds the HTTP <c>User-Agent</c> header for every RayTagger outbound request.
/// Format matches the MusicBrainz API contract — <c>Application/Version ( contact )</c> —
/// because MusicBrainz throttles or blocks anonymous / generic clients per
/// <see href="https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting"/>. Discogs and Last.fm
/// don't enforce a format but accept the same string, so we use one shape everywhere.
/// </summary>
public static class UserAgentFactory
{
    /// <summary>Application name component. Matches the assembly product name.</summary>
    public const string ApplicationName = "RayTagger";

    /// <summary>
    /// Default contact when the user hasn't supplied one via <c>lookup.user_agent_contact</c>.
    /// Public RayTagger repository — MusicBrainz admins can open an issue there if they need
    /// to reach the maintainers.
    /// </summary>
    public const string DefaultContact = "https://github.com/RAYCOON/RayTagger";

    /// <summary>
    /// Returns the canonical UA string. Version is read from
    /// <see cref="AssemblyInformationalVersionAttribute"/> (auto-populated from
    /// <c>Directory.Build.props</c>'s <c>$(Version)</c>) so it tracks releases automatically;
    /// falls back to <see cref="AssemblyName.Version"/> if the informational attribute is missing.
    /// </summary>
    /// <param name="contact">
    /// Optional override. <c>null</c>, empty, or whitespace-only → <see cref="DefaultContact"/>.
    /// Surrounding whitespace is trimmed; the inner value is otherwise passed through verbatim so
    /// callers can use an email <i>or</i> a URL (MusicBrainz accepts both).
    /// </param>
    public static string Build(string? contact = null)
    {
        var resolved = string.IsNullOrWhiteSpace(contact) ? DefaultContact : contact.Trim();
        return $"{ApplicationName}/{ResolveVersion()} ( {resolved} )";
    }

    private static string ResolveVersion()
    {
        var assembly = typeof(UserAgentFactory).Assembly;

        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Deterministic-build SDKs append a "+<commit-sha>" build metadata segment. Strip it
            // so the UA stays short and stable across rebuilds of the same source revision.
            var plus = info.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? info : info[..plus];
        }

        var version = assembly.GetName().Version;
        return version is not null ? version.ToString(3) : "0.0.0";
    }
}
