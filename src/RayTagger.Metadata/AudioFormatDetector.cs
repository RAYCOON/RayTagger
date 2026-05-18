using RayTagger.Core.Models;

namespace RayTagger.Metadata;

/// <summary>
/// Resolves <see cref="AudioFormat"/> from a file path. Extension-based; magic-byte detection
/// could be added later if real-world files turn out to have misleading extensions, but for a
/// directory-walk scenario the extension is authoritative.
/// </summary>
public static class AudioFormatDetector
{
    /// <summary>Returns the format for a supported extension, or <c>null</c> for anything else.</summary>
    public static AudioFormat? TryDetect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var ext = Path.GetExtension(path);
        if (ext.Length < 2)
        {
            return null;
        }

        return ext.ToUpperInvariant() switch
        {
            ".MP3" => AudioFormat.Mp3,
            ".FLAC" => AudioFormat.Flac,
            ".AIF" or ".AIFF" or ".AIFC" => AudioFormat.Aiff,
            _ => null,
        };
    }

    /// <summary>True if the path's extension maps to a supported audio format.</summary>
    public static bool IsSupported(string path) => TryDetect(path).HasValue;
}
