using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Core.Tests.Pipeline;

public sealed class SortServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "tagger-sort-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SortService _service = new(NullLogger<SortService>.Instance);

    public SortServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void Reports_disabled_when_sort_is_off()
    {
        var source = SeedFile("song.mp3");
        var outcome = _service.Execute(source, MakeExisting(), MakeResolved(), new SortOptions { Enabled = false }, dryRun: false);

        outcome.Action.Should().Be(SortAction.Disabled);
        outcome.DestinationPath.Should().BeNull();
    }

    [Fact]
    public void Reports_planned_in_dry_run_without_moving_anything()
    {
        var source = SeedFile("song.mp3");
        var dest = Path.Combine(_tempRoot, "library");
        var options = new SortOptions { Enabled = true, Destination = dest, Pattern = "{genre}/{title}.{ext}" };

        var outcome = _service.Execute(source, new TrackTags(Title: "Song"), MakeResolved("Rock"), options, dryRun: true);

        outcome.Action.Should().Be(SortAction.Planned);
        outcome.DestinationPath.Should().Be(Path.Combine(dest, "Rock", "Song.mp3"));
        File.Exists(source.Path).Should().BeTrue();
    }

    [Fact]
    public void Moves_file_into_destination_tree()
    {
        var source = SeedFile("song.mp3");
        var dest = Path.Combine(_tempRoot, "library");
        var options = new SortOptions { Enabled = true, Destination = dest, Pattern = "{genre}/{title}.{ext}" };

        var outcome = _service.Execute(source, new TrackTags(Title: "Song"), MakeResolved("Rock"), options, dryRun: false);

        outcome.Action.Should().Be(SortAction.Moved);
        File.Exists(source.Path).Should().BeFalse();
        File.Exists(outcome.DestinationPath!).Should().BeTrue();
        Path.GetFullPath(outcome.DestinationPath!).Should().Be(Path.GetFullPath(Path.Combine(dest, "Rock", "Song.mp3")));
    }

    [Fact]
    public void Copies_when_preserve_source_is_true()
    {
        var source = SeedFile("song.mp3");
        var dest = Path.Combine(_tempRoot, "library");
        var options = new SortOptions
        {
            Enabled = true, Destination = dest, Pattern = "{genre}/{title}.{ext}",
            PreserveSource = true,
        };

        var outcome = _service.Execute(source, new TrackTags(Title: "Song"), MakeResolved("Rock"), options, dryRun: false);

        outcome.Action.Should().Be(SortAction.Copied);
        File.Exists(source.Path).Should().BeTrue();
        File.Exists(outcome.DestinationPath!).Should().BeTrue();
    }

    [Fact]
    public void Renames_on_conflict_when_policy_is_rename()
    {
        var source = SeedFile("song.mp3");
        var dest = Path.Combine(_tempRoot, "library");
        Directory.CreateDirectory(Path.Combine(dest, "Rock"));
        File.WriteAllText(Path.Combine(dest, "Rock", "Song.mp3"), "existing");

        var options = new SortOptions
        {
            Enabled = true, Destination = dest, Pattern = "{genre}/{title}.{ext}",
            Conflict = SortConflictPolicy.Rename,
        };
        var outcome = _service.Execute(source, new TrackTags(Title: "Song"), MakeResolved("Rock"), options, dryRun: false);

        outcome.Action.Should().Be(SortAction.Moved);
        outcome.DestinationPath.Should().EndWith("Song (2).mp3");
        File.ReadAllText(Path.Combine(dest, "Rock", "Song.mp3")).Should().Be("existing");
    }

    [Fact]
    public void Skips_on_conflict_when_policy_is_skip()
    {
        var source = SeedFile("song.mp3");
        var dest = Path.Combine(_tempRoot, "library");
        Directory.CreateDirectory(Path.Combine(dest, "Rock"));
        var existingTarget = Path.Combine(dest, "Rock", "Song.mp3");
        File.WriteAllText(existingTarget, "existing");

        var options = new SortOptions
        {
            Enabled = true, Destination = dest, Pattern = "{genre}/{title}.{ext}",
            Conflict = SortConflictPolicy.Skip,
        };
        var outcome = _service.Execute(source, new TrackTags(Title: "Song"), MakeResolved("Rock"), options, dryRun: false);

        outcome.Action.Should().Be(SortAction.SkippedConflict);
        File.Exists(source.Path).Should().BeTrue();
        File.ReadAllText(existingTarget).Should().Be("existing");
    }

    [Fact]
    public void Overwrites_on_conflict_when_policy_is_overwrite()
    {
        var source = SeedFile("song.mp3", contents: "new");
        var dest = Path.Combine(_tempRoot, "library");
        Directory.CreateDirectory(Path.Combine(dest, "Rock"));
        var existingTarget = Path.Combine(dest, "Rock", "Song.mp3");
        File.WriteAllText(existingTarget, "old");

        var options = new SortOptions
        {
            Enabled = true, Destination = dest, Pattern = "{genre}/{title}.{ext}",
            Conflict = SortConflictPolicy.Overwrite,
        };
        var outcome = _service.Execute(source, new TrackTags(Title: "Song"), MakeResolved("Rock"), options, dryRun: false);

        outcome.Action.Should().Be(SortAction.Moved);
        File.ReadAllText(existingTarget).Should().Be("new");
    }

    [Fact]
    public void Carries_sidecar_backups_along_when_moving()
    {
        var source = SeedFile("song.mp3");
        var sidecar = source.Path + ".tagger.bak.20260518-100000-000.yaml";
        File.WriteAllText(sidecar, "schema_version: 1");

        var dest = Path.Combine(_tempRoot, "library");
        var options = new SortOptions { Enabled = true, Destination = dest, Pattern = "{genre}/{title}.{ext}" };

        var outcome = _service.Execute(source, new TrackTags(Title: "Song"), MakeResolved("Rock"), options, dryRun: false);

        File.Exists(sidecar).Should().BeFalse("the sidecar must follow the audio");
        var newSidecar = outcome.DestinationPath + ".tagger.bak.20260518-100000-000.yaml";
        File.Exists(newSidecar).Should().BeTrue();
    }

    [Fact]
    public void Reports_already_sorted_when_destination_equals_source()
    {
        var dest = Path.Combine(_tempRoot, "library", "Rock");
        Directory.CreateDirectory(dest);
        var path = Path.Combine(dest, "Song.mp3");
        File.WriteAllText(path, "x");
        var file = new TrackFile(path, AudioFormat.Mp3, 1, DateTime.UtcNow);

        var options = new SortOptions
        {
            Enabled = true,
            Destination = Path.Combine(_tempRoot, "library"),
            Pattern = "{genre}/{title}.{ext}",
        };
        var outcome = _service.Execute(file, new TrackTags(Title: "Song"), MakeResolved("Rock"), options, dryRun: false);

        outcome.Action.Should().Be(SortAction.AlreadySorted);
        File.Exists(path).Should().BeTrue();
    }

    private TrackFile SeedFile(string name, string contents = "audio bytes")
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, contents);
        return new TrackFile(path, AudioFormat.Mp3, contents.Length, DateTime.UtcNow);
    }

    private static TrackTags MakeExisting() => new(Title: "Song", Artist: "Artist");

    private static ResolvedTrackTags MakeResolved(string? genre = null) =>
        new(
            genre is null
                ? ResolvedField.Empty<string>()
                : new ResolvedField<string>(genre, TagFieldSource.Rules, 1),
            ResolvedField.Empty<string>(),
            ResolvedField.EmptyValue<double>(),
            ResolvedField.Empty<MusicalKey>(),
            ResolvedField.EmptyValue<int>(),
            new Dictionary<string, ResolvedField<string>>(StringComparer.OrdinalIgnoreCase));
}
