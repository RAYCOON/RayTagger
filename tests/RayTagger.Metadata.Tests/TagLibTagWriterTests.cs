using RayTagger.Core.Models;
using RayTagger.Metadata;

namespace RayTagger.Metadata.Tests;

public class TagLibTagWriterTests
{
    private static TagLibTagWriter MakeWriter() =>
        new(new TagLibTagReader(), new BackupSidecarWriter());

    [Fact]
    public void Throws_MetadataException_for_missing_file()
    {
        var writer = MakeWriter();
        var resolved = AllRulesSourced();
        var options = new TagWriteOptions(DryRun: false, Backup: false);

        Action act = () => writer.Write("/nonexistent/path/song.mp3", resolved, options);

        act.Should().Throw<MetadataException>()
            .Which.FilePath.Should().Be("/nonexistent/path/song.mp3");
    }

    [Fact]
    public void Dry_run_reports_fields_without_touching_filesystem()
    {
        // Note: still requires a real file to exist for path validation, so we create an empty
        // placeholder. We don't actually pass it to TagLib (DryRun short-circuits before that).
        var tempPath = Path.Combine(Path.GetTempPath(), "tagger-dry-" + Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(tempPath, []);
        try
        {
            var writer = MakeWriter();
            var resolved = AllRulesSourced();
            var options = new TagWriteOptions(DryRun: true, Backup: false);

            var result = writer.Write(tempPath, resolved, options);

            result.DryRun.Should().BeTrue();
            result.BackupPath.Should().BeNull();
            result.WrittenFields.Should().BeEquivalentTo(["Genre", "SubGenre", "Bpm", "Key", "Energy"]);

            // No tagger sidecars should exist in the temp dir.
            var sidecars = Directory.GetFiles(Path.GetTempPath(), "*.tagger.bak.*", SearchOption.TopDirectoryOnly);
            sidecars.Should().NotContain(p => p.Contains(Path.GetFileName(tempPath), StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void Empty_resolved_tags_produce_no_writes()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "tagger-empty-" + Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(tempPath, []);
        try
        {
            var writer = MakeWriter();
            // ResolvedTrackTags.Empty has every field with Source=Existing → nothing to write.
            var resolved = ResolvedTrackTags.Empty;
            var options = new TagWriteOptions(DryRun: false, Backup: false);

            var result = writer.Write(tempPath, resolved, options);

            result.WrittenFields.Should().BeEmpty();
            result.BackupPath.Should().BeNull();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void Custom_field_from_rules_is_reported_as_written()
    {
        // Regression: previously TagLibTagWriter.ApplyChanges only switched on Genre/SubGenre/
        // Bpm/Key/Energy. Custom-fields set by mapping rules (e.g. `set: { tag.mood: "Driving" }`)
        // populated the resolved state but were never written, silently dropping user intent.
        var tempPath = Path.Combine(Path.GetTempPath(), "tagger-custom-dry-" + Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(tempPath, []);
        try
        {
            var writer = MakeWriter();
            var resolved = new ResolvedTrackTags(
                Genre: ResolvedField.Empty<string>(),
                SubGenre: ResolvedField.Empty<string>(),
                Bpm: ResolvedField.EmptyValue<double>(),
                Key: ResolvedField.Empty<MusicalKey>(),
                Energy: ResolvedField.EmptyValue<int>(),
                Custom: new Dictionary<string, ResolvedField<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MOOD"] = new ResolvedField<string>("Driving", TagFieldSource.Rules, 1.0),
                });
            var options = new TagWriteOptions(DryRun: true, Backup: false);

            var result = writer.Write(tempPath, resolved, options);

            result.WrittenFields.Should().Contain("tag.MOOD");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void Custom_field_with_existing_source_is_skipped()
    {
        // The Custom dict carries every existing TXXX/Vorbis frame so we don't accidentally drop
        // ReplayGain / MBID etc. on round-trip. Those entries have Source=Existing — the writer
        // must NOT report them as written or it'll mark every scan as having changes.
        var tempPath = Path.Combine(Path.GetTempPath(), "tagger-custom-existing-" + Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(tempPath, []);
        try
        {
            var writer = MakeWriter();
            var resolved = new ResolvedTrackTags(
                Genre: ResolvedField.Empty<string>(),
                SubGenre: ResolvedField.Empty<string>(),
                Bpm: ResolvedField.EmptyValue<double>(),
                Key: ResolvedField.Empty<MusicalKey>(),
                Energy: ResolvedField.EmptyValue<int>(),
                Custom: new Dictionary<string, ResolvedField<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["REPLAYGAIN_TRACK_GAIN"] = new ResolvedField<string>("-6.2 dB", TagFieldSource.Existing, 1.0),
                });
            var options = new TagWriteOptions(DryRun: true, Backup: false);

            var result = writer.Write(tempPath, resolved, options);

            result.WrittenFields.Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>Builds a fully-populated ResolvedTrackTags where every field's Source is Rules.</summary>
    private static ResolvedTrackTags AllRulesSourced() => new(
        Genre: new ResolvedField<string>("Tech House", TagFieldSource.Rules, 1.0),
        SubGenre: new ResolvedField<string>("Peak Time", TagFieldSource.Rules, 1.0),
        Bpm: new ResolvedValueField<double>(128.0, TagFieldSource.Rules, 1.0),
        Key: new ResolvedField<MusicalKey>(new MusicalKey("Am", "8A"), TagFieldSource.Rules, 1.0),
        Energy: new ResolvedValueField<int>(8, TagFieldSource.Rules, 1.0),
        Custom: new Dictionary<string, ResolvedField<string>>(StringComparer.OrdinalIgnoreCase));
}
