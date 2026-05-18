using RayTagger.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using FsFile = System.IO.File;

namespace RayTagger.Metadata;

/// <summary>
/// Reads <see cref="BackupSidecarWriter"/>-format YAML sidecars and reconstructs the
/// <see cref="TrackTags"/> snapshot they captured. Used by <c>tagger restore</c> to undo the
/// most recent tag write.
/// </summary>
/// <remarks>
/// Sidecar naming convention: <c>&lt;audio&gt;.tagger.bak.&lt;yyyyMMdd-HHmmss-fff&gt;.yaml</c>.
/// "Most recent" picks the lexicographically largest timestamp suffix — equivalent to
/// chronological since the stamp is fixed-width ISO-like UTC.
/// </remarks>
public sealed class SidecarRestoreService
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Locates the most recent sidecar for <paramref name="audioPath"/>. Returns <c>null</c> if
    /// none exist — caller decides whether that's an error.
    /// </summary>
    /// <remarks>
    /// CA1822: kept as instance member for symmetry with <see cref="Read"/> and future-proofing
    /// against injecting a clock / file-system abstraction for tests.
    /// </remarks>
#pragma warning disable CA1822
    public string? FindLatestSidecar(string audioPath)
#pragma warning restore CA1822
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);

        var dir = Path.GetDirectoryName(audioPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        var pattern = Path.GetFileName(audioPath) + ".tagger.bak.*.yaml";
        var candidates = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);
        return candidates.OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>
    /// Deserialises a single sidecar YAML into the <see cref="TrackTags"/> snapshot it captured.
    /// Throws <see cref="SidecarRestoreException"/> on malformed or missing files.
    /// </summary>
#pragma warning disable CA1822
    public TrackTags Read(string sidecarPath)
#pragma warning restore CA1822
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);
        if (!FsFile.Exists(sidecarPath))
        {
            throw new SidecarRestoreException($"Sidecar not found: {sidecarPath}");
        }

        SidecarDocument doc;
        try
        {
            var yaml = FsFile.ReadAllText(sidecarPath);
            doc = Deserializer.Deserialize<SidecarDocument>(yaml)
                ?? throw new SidecarRestoreException($"Sidecar is empty: {sidecarPath}");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new SidecarRestoreException($"Sidecar is malformed: {sidecarPath}", ex);
        }

        if (doc.SchemaVersion != 1)
        {
            throw new SidecarRestoreException(
                $"Unsupported sidecar schema_version={doc.SchemaVersion} in {sidecarPath}");
        }
        if (doc.Tags is null)
        {
            throw new SidecarRestoreException($"Sidecar has no 'tags' section: {sidecarPath}");
        }

        var t = doc.Tags;
        MusicalKey? key = (t.KeyStandard, t.KeyCamelot) switch
        {
            (null, null) => null,
            ({ } std, { } cam) => new MusicalKey(std, cam),
            _ => null,  // partial key — restore deliberately drops it; the original sidecar wrote what it had
        };

        return new TrackTags(
            Title: t.Title,
            Artist: t.Artist,
            AlbumArtist: t.AlbumArtist,
            Album: t.Album,
            Year: t.Year,
            Genre: t.Genre,
            SubGenre: t.SubGenre,
            Bpm: t.Bpm,
            Key: key,
            Energy: t.Energy,
            Mood: t.Mood,
            SetPosition: t.SetPosition,
            Custom: t.Custom?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
    }

    // YamlDotNet deserialises into these mutable POCOs via reflection; the public TrackTags is
    // then constructed immutably. CA1812 can't see the reflection-time instantiation — suppress.
#pragma warning disable CA1812
    private sealed class SidecarDocument
    {
        public int SchemaVersion { get; set; }
        public string? AudioFile { get; set; }
        public string? BackedUpAtUtc { get; set; }
        public SidecarTags? Tags { get; set; }
    }

    private sealed class SidecarTags
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? AlbumArtist { get; set; }
        public string? Album { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
        public string? SubGenre { get; set; }
        public double? Bpm { get; set; }
        public string? KeyStandard { get; set; }
        public string? KeyCamelot { get; set; }
        public int? Energy { get; set; }
        public string? Mood { get; set; }
        public string? SetPosition { get; set; }
        public Dictionary<string, string>? Custom { get; set; }
    }
#pragma warning restore CA1812
}

public sealed class SidecarRestoreException : Exception
{
    public SidecarRestoreException(string message) : base(message) { }
    public SidecarRestoreException(string message, Exception inner) : base(message, inner) { }
    public SidecarRestoreException() { }
}
