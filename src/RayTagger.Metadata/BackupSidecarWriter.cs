using System.Globalization;
using RayTagger.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RayTagger.Metadata;

/// <summary>
/// Writes a YAML snapshot of the current tags alongside the audio file before any modification.
/// The sidecar is named <c>&lt;file&gt;.tagger.bak.&lt;timestamp&gt;.yaml</c> so multiple runs against
/// the same file don't overwrite each other — a future <c>tagger restore</c> command can pick the
/// most recent backup.
/// </summary>
public sealed class BackupSidecarWriter
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .Build();

    /// <param name="audioFilePath">Path to the audio file whose current tags are being backed up.</param>
    /// <param name="tags">Current tag snapshot, as just read from the file.</param>
    /// <param name="backupDirectory">
    /// Where to write the sidecar. <c>null</c> = next to the audio file (default and easiest to
    /// discover). Set to a different directory if you want backups grouped centrally.
    /// </param>
    /// <returns>Absolute path of the written sidecar file.</returns>
    /// <remarks>
    /// CA1822: kept as an instance method because it's resolved through DI in the host (a single
    /// registered <see cref="BackupSidecarWriter"/>); future versions may gain injected dependencies
    /// (clock, path provider) that test-doubles need to substitute.
    /// </remarks>
#pragma warning disable CA1822
    public string Write(string audioFilePath, TrackTags tags, string? backupDirectory = null)
#pragma warning restore CA1822
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(tags);

        var dir = backupDirectory ?? Path.GetDirectoryName(audioFilePath)
            ?? throw new InvalidOperationException($"Cannot determine directory of {audioFilePath}.");

        Directory.CreateDirectory(dir);

        var stem = Path.GetFileName(audioFilePath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var sidecarPath = Path.Combine(dir, $"{stem}.tagger.bak.{stamp}.yaml");

        var doc = BuildBackupDocument(audioFilePath, tags, stamp);
        var yaml = Serializer.Serialize(doc);
        File.WriteAllText(sidecarPath, yaml);

        return sidecarPath;
    }

    /// <summary>
    /// Builds a plain dictionary representation of the backup so future schema changes don't
    /// break old sidecars. Restore code reads this back into the same shape.
    /// </summary>
    private static Dictionary<string, object?> BuildBackupDocument(string audioFilePath, TrackTags tags, string timestamp) =>
        new()
        {
            ["schema_version"] = 1,
            ["audio_file"] = audioFilePath,
            ["backed_up_at_utc"] = timestamp,
            ["tags"] = new Dictionary<string, object?>
            {
                ["title"] = tags.Title,
                ["artist"] = tags.Artist,
                ["album_artist"] = tags.AlbumArtist,
                ["album"] = tags.Album,
                ["year"] = tags.Year,
                ["genre"] = tags.Genre,
                ["sub_genre"] = tags.SubGenre,
                ["bpm"] = tags.Bpm,
                ["key_standard"] = tags.Key?.Standard,
                ["key_camelot"] = tags.Key?.Camelot,
                ["energy"] = tags.Energy,
                ["mood"] = tags.Mood,
                ["set_position"] = tags.SetPosition,
                ["custom"] = tags.Custom.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase),
            },
        };
}
