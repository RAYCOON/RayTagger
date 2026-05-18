namespace RayTagger.Core.Tests.Support;

/// <summary>
/// Locates the repository root by walking up from the test assembly directory looking for
/// <c>RayTagger.slnx</c>. Avoids hard-coding relative paths that break when the test
/// output structure changes.
/// </summary>
internal static class RepoRoot
{
    private static readonly Lazy<string> _path = new(Discover);

    public static string Path => _path.Value;

    public static string Samples(string fileName) =>
        System.IO.Path.Combine(Path, "samples", fileName);

    private static string Discover()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "RayTagger.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate RayTagger.slnx walking up from {AppContext.BaseDirectory}");
    }
}
