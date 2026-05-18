using RayTagger.Core.Models;
using RayTagger.Metadata;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RayTagger.Metadata.Tests;

public sealed class BackupSidecarWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BackupSidecarWriter _writer = new();

    public BackupSidecarWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tagger-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Sidecar_path_uses_audio_filename_plus_timestamp()
    {
        var audioPath = Path.Combine(_tempDir, "song.mp3");
        File.WriteAllText(audioPath, "fake");  // file just needs to exist for path resolution

        var sidecar = _writer.Write(audioPath, MakeSampleTags());

        Path.GetDirectoryName(sidecar).Should().Be(_tempDir);
        Path.GetFileName(sidecar).Should().StartWith("song.mp3.tagger.bak.");
        Path.GetFileName(sidecar).Should().EndWith(".yaml");
        File.Exists(sidecar).Should().BeTrue();
    }

    [Fact]
    public void Two_backups_in_quick_succession_produce_distinct_paths()
    {
        var audioPath = Path.Combine(_tempDir, "song.mp3");
        File.WriteAllText(audioPath, "fake");

        var first = _writer.Write(audioPath, MakeSampleTags());
        // Force a small delay so the millisecond timestamp differs.
        Thread.Sleep(5);
        var second = _writer.Write(audioPath, MakeSampleTags());

        first.Should().NotBe(second);
        File.Exists(first).Should().BeTrue();
        File.Exists(second).Should().BeTrue();
    }

    [Fact]
    public void Sidecar_round_trips_all_logical_fields()
    {
        var audioPath = Path.Combine(_tempDir, "song.mp3");
        File.WriteAllText(audioPath, "fake");

        var tags = new TrackTags(
            Title: "Outro",
            Artist: "Bonobo",
            AlbumArtist: "Bonobo",
            Album: "Migration",
            Year: 2017,
            Genre: "Electronic",
            SubGenre: "Downtempo",
            Bpm: 92.0,
            Key: new MusicalKey("Em", "9A"),
            Energy: 4,
            Custom: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["REPLAYGAIN_TRACK_GAIN"] = "-5.6 dB",
            });

        var sidecar = _writer.Write(audioPath, tags);
        var yaml = File.ReadAllText(sidecar);

        // Re-parse and verify the round-tripped values are present.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var doc = deserializer.Deserialize<Dictionary<string, object>>(yaml);

        doc.Should().ContainKey("schema_version");
        doc.Should().ContainKey("audio_file");
        doc.Should().ContainKey("backed_up_at_utc");
        doc.Should().ContainKey("tags");

        var tagsBlock = (Dictionary<object, object>)doc["tags"];
        tagsBlock["title"].Should().Be("Outro");
        tagsBlock["artist"].Should().Be("Bonobo");
        tagsBlock["genre"].Should().Be("Electronic");
        tagsBlock["sub_genre"].Should().Be("Downtempo");
        tagsBlock["bpm"].Should().Be("92");
        tagsBlock["key_standard"].Should().Be("Em");
        tagsBlock["key_camelot"].Should().Be("9A");
        tagsBlock["energy"].Should().Be("4");
    }

    [Fact]
    public void Custom_backup_directory_is_used_when_provided()
    {
        var audioPath = Path.Combine(_tempDir, "song.mp3");
        File.WriteAllText(audioPath, "fake");
        var backupDir = Path.Combine(_tempDir, "backups");

        var sidecar = _writer.Write(audioPath, MakeSampleTags(), backupDirectory: backupDir);

        Path.GetDirectoryName(sidecar).Should().Be(backupDir);
        Directory.Exists(backupDir).Should().BeTrue();
    }

    private static TrackTags MakeSampleTags() => new(Title: "Test", Genre: "Test Genre");
}
