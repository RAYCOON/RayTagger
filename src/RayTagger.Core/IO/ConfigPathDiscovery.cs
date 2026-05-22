namespace RayTagger.Core.IO;

/// <summary>
/// Locates <c>tagger.yaml</c> on the filesystem. Central helper shared by the CLI command handlers
/// and the UI auto-discovery so all entry points agree on the search order.
/// </summary>
/// <remarks>
/// Search order (first hit wins):
/// <list type="number">
///   <item>The <c>RAYTAGGER_CONFIG</c> environment variable, when set to an existing file path.</item>
///   <item>Walk up from <c>startDirectory</c> (inclusive) for up to <c>maxParents</c> levels; at each
///         level check <c>./tagger.yaml</c> then <c>./config/tagger.yaml</c>.</item>
/// </list>
/// Returns <c>null</c> when nothing matches so callers can decide whether to fall back to in-memory
/// defaults (UI) or surface an error (CLI).
/// </remarks>
public static class ConfigPathDiscovery
{
    /// <summary>Name of the env-var override.</summary>
    public const string EnvVarName = "RAYTAGGER_CONFIG";

    /// <summary>Default config-file name.</summary>
    public const string ConfigFileName = "tagger.yaml";

    /// <summary>Default sub-directory checked at each level of the parent walk.</summary>
    public const string ConfigSubdirectory = "config";

    /// <summary>
    /// Returns the resolved absolute path to <c>tagger.yaml</c>, or <c>null</c> when no candidate
    /// exists. See class remarks for the search order.
    /// </summary>
    /// <param name="startDirectory">Directory to start the upward walk from. Typically the CWD
    /// for the CLI, or the user-picked source folder for the UI.</param>
    /// <param name="maxParents">Maximum number of parent directories to traverse. Defaults to 4
    /// — enough to walk from <c>repo/music/SubGenre/</c> up to <c>repo/config/tagger.yaml</c>
    /// without scanning the entire drive.</param>
    public static string? Find(string startDirectory, int maxParents = 4)
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var dir = new DirectoryInfo(startDirectory);
        for (var i = 0; i <= maxParents && dir is not null; i++)
        {
            var direct = Path.Combine(dir.FullName, ConfigFileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            var inConfig = Path.Combine(dir.FullName, ConfigSubdirectory, ConfigFileName);
            if (File.Exists(inConfig))
            {
                return inConfig;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
