namespace RayTagger.Core.IO;

/// <summary>
/// Resolves the OS-appropriate per-user data directory for RayTagger. Abstraction so tests
/// can substitute a deterministic temp directory. See docs/ARCHITECTURE.md §4 / §8.4.
/// </summary>
public interface IUserDataDirectoryProvider
{
    /// <summary>
    /// Root data directory:
    /// <list type="bullet">
    ///   <item>macOS: <c>~/Library/Application Support/RayTagger</c></item>
    ///   <item>Linux: <c>~/.local/share/RayTagger</c> (or <c>$XDG_DATA_HOME/RayTagger</c>)</item>
    ///   <item>Windows: <c>%LOCALAPPDATA%\RayTagger</c></item>
    /// </list>
    /// </summary>
    string GetDataDirectory();

    /// <summary>Cache subdirectory under <see cref="GetDataDirectory"/>. Created on access.</summary>
    string GetCacheDirectory();
}
