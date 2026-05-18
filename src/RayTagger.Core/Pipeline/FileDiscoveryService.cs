using System.Runtime.CompilerServices;
using Microsoft.Extensions.FileSystemGlobbing;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Filesystem walker. Filtering pipeline (per file):
/// <list type="number">
///   <item>Path must be under <see cref="ScanOptions.Source"/> (recursion controlled by config).</item>
///   <item>Extension must be in <see cref="ScanOptions.Formats"/>.</item>
///   <item>Path must match <see cref="ScanOptions.IncludeGlobs"/> (or include-globs is empty).</item>
///   <item>Path must NOT match any <see cref="ScanOptions.ExcludeGlobs"/>.</item>
/// </list>
/// </summary>
public sealed class FileDiscoveryService : IFileDiscoveryService
{
    public async IAsyncEnumerable<TrackFile> EnumerateAsync(
        ScanOptions scan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);

        if (!Directory.Exists(scan.Source))
        {
            yield break;
        }

        var matcher = BuildMatcher(scan);
        var allowedExtensions = new HashSet<string>(
            scan.Formats.Select(f => "." + f.ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = scan.Recursive,
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.PlatformDefault,
        };

        foreach (var path in Directory.EnumerateFiles(scan.Source, "*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(path);
            if (ext.Length < 2 || !allowedExtensions.Contains(ext.ToUpperInvariant()))
            {
                continue;
            }

            var relative = Path.GetRelativePath(scan.Source, path);
            if (!matcher.Match(relative).HasMatches)
            {
                continue;
            }

            var trackFile = await BuildTrackFileAsync(path, ext, cancellationToken).ConfigureAwait(false);
            if (trackFile is not null)
            {
                yield return trackFile;
            }
        }
    }

    private static Matcher BuildMatcher(ScanOptions scan)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        if (scan.IncludeGlobs.Count == 0)
        {
            matcher.AddInclude("**/*");
        }
        else
        {
            foreach (var pattern in scan.IncludeGlobs)
            {
                matcher.AddInclude(pattern);
            }
        }
        foreach (var pattern in scan.ExcludeGlobs)
        {
            matcher.AddExclude(pattern);
        }
        return matcher;
    }

    private static async Task<TrackFile?> BuildTrackFileAsync(string path, string extension, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();  // keeps the enumerator responsive on huge directories

        var format = extension.ToUpperInvariant() switch
        {
            ".MP3" => AudioFormat.Mp3,
            ".FLAC" => AudioFormat.Flac,
            ".AIF" or ".AIFF" or ".AIFC" => AudioFormat.Aiff,
            _ => (AudioFormat?)null,
        };

        if (format is null)
        {
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }

        return new TrackFile(path, format.Value, info.Length, info.LastWriteTimeUtc);
    }
}
