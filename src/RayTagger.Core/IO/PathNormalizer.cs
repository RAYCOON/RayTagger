namespace RayTagger.Core.IO;

/// <summary>
/// Normalises a path string from a YAML config value into a fully-qualified absolute path.
/// Handles tilde expansion and resolves relative paths against the directory of the config file
/// (not the current working directory — see docs/ARCHITECTURE.md §8.2).
/// </summary>
public static class PathNormalizer
{
    /// <param name="rawPath">Path as written in the YAML config.</param>
    /// <param name="configBaseDirectory">
    /// Absolute directory of the YAML file being loaded. Relative paths in <paramref name="rawPath"/>
    /// resolve against this, not the process CWD.
    /// </param>
    /// <returns>
    /// An absolute, fully-qualified path. The path is NOT checked for existence — callers
    /// validate that separately so a missing directory is a domain error, not an IO error here.
    /// </returns>
    /// <remarks>
    /// Empty or whitespace input is returned as-is; callers decide whether that's an error or a
    /// signal to use a default.
    /// </remarks>
    public static string Normalize(string rawPath, string configBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(rawPath);
        ArgumentNullException.ThrowIfNull(configBaseDirectory);

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return rawPath;
        }

        var expanded = ExpandTilde(rawPath);

        return Path.IsPathFullyQualified(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(expanded, configBaseDirectory);
    }

    private static string ExpandTilde(string path)
    {
        if (path.Length == 0 || path[0] != '~')
        {
            return path;
        }

        // Accept only "~" or "~/..." (or "~\..." on Windows). "~user" is not supported.
        if (path.Length > 1 && path[1] != '/' && path[1] != Path.DirectorySeparatorChar)
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            // Silently leaving a literal `~` in the path propagates to filesystem calls that
            // then fail far away from the config load with a confusing "file not found ~/foo"
            // error. Fail loud here so the user sees the env problem immediately.
            throw new InvalidOperationException(
                $"Cannot expand '{path}': user-home directory is not set (Environment.SpecialFolder.UserProfile is empty). " +
                "Set HOME (or USERPROFILE on Windows) or rewrite the path as absolute.");
        }

        return path.Length == 1
            ? home
            : Path.Combine(home, path[2..]);
    }
}
