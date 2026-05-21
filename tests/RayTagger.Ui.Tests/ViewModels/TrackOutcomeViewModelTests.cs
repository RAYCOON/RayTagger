using RayTagger.Core.Models;
using RayTagger.Ui.ViewModels;

namespace RayTagger.Ui.Tests.ViewModels;

public class TrackOutcomeViewModelTests
{
    private static readonly TrackFile DefaultFile =
        new("/library/song.mp3", AudioFormat.Mp3, 4_000_000, new DateTime(2024, 6, 22, 14, 35, 0, DateTimeKind.Utc));

    // ===== Preview constructor =====================================================================

    [Fact]
    public void Preview_ctor_seeds_existing_from_disk_tags_and_leaves_status_empty()
    {
        var existing = new TrackTags(
            Title: "Title", Artist: "Artist", Album: "Album", Year: 2020,
            Genre: "House", SubGenre: "Tech", Bpm: 124.5, Key: new MusicalKey("Am", "8A"),
            Energy: 7, Mood: "Driving", SetPosition: "Peak Time", DurationSeconds: 381);

        var vm = new TrackOutcomeViewModel(DefaultFile, existing);

        vm.Title.Should().Be("Title");
        vm.Artist.Should().Be("Artist");
        vm.Album.Should().Be("Album");
        vm.Year.Should().Be("2020");
        vm.ExistingGenre.Should().Be("House");
        vm.ExistingSubGenre.Should().Be("Tech");
        vm.ExistingBpm.Should().Be(124.5);
        vm.ExistingKey.Should().Be("Am");
        vm.ExistingEnergy.Should().Be(7);
        vm.ExistingMood.Should().Be("Driving");
        vm.ExistingSetPosition.Should().Be("Peak Time");
        vm.LengthDisplay.Should().Be("6:21");

        // Proposed-* stays null (no scan output yet) → HasXxxDiff is false everywhere → no highlights.
        vm.ProposedGenre.Should().BeNull();
        vm.HasGenreDiff.Should().BeFalse();
        vm.HasBpmDiff.Should().BeFalse();

        // Pre-scan: status pill is blank, no error, no Anwenden / Revert clickable.
        vm.StatusLabel.Should().BeEmpty();
        vm.StatusBadge.Should().BeEmpty();
        vm.ApplyError.Should().BeNull();
        vm.CanApply.Should().BeFalse();
    }

    // ===== Camelot sort-key normalization (B1) =====================================================

    [Theory]
    [InlineData("1A", "01A")]
    [InlineData("9B", "09B")]
    [InlineData("12A", "12A")]
    [InlineData("12B", "12B")]
    [InlineData("10A", "10A")]
    [InlineData("5a", "05A")]   // lower-case letter normalised to upper
    [InlineData("11b", "11B")]
    public void Camelot_sort_key_zero_pads_single_digit_numbers(string camelot, string expectedSortKey)
    {
        var existing = TrackTags.Empty with { Key = new MusicalKey("Am", camelot) };
        var vm = new TrackOutcomeViewModel(DefaultFile, existing);
        vm.EffectiveCamelotSortKey.Should().Be(expectedSortKey);
    }

    [Fact]
    public void Camelot_sort_key_passes_unexpected_shapes_through()
    {
        // "12C" is not a real Camelot code (only A / B suffixes exist). Pass through unchanged
        // rather than silently producing a misleading sort key.
        var existing = TrackTags.Empty with { Key = new MusicalKey("X", "12C") };
        var vm = new TrackOutcomeViewModel(DefaultFile, existing);
        vm.EffectiveCamelotSortKey.Should().Be("12C");
    }

    [Fact]
    public void Camelot_sort_key_null_when_both_existing_and_proposed_are_missing()
    {
        // No key at all — Effective falls back to null, sort-key is null.
        var existing = TrackTags.Empty;
        var vm = new TrackOutcomeViewModel(DefaultFile, existing);
        vm.EffectiveCamelotSortKey.Should().BeNull();
    }

    // ===== Camelot pipeline through lifecycle helpers (B2) =========================================

    [Fact]
    public void Preview_ctor_seeds_Existing_camelot_key()
    {
        var existing = TrackTags.Empty with { Key = new MusicalKey("F#m", "11A") };
        var vm = new TrackOutcomeViewModel(DefaultFile, existing);
        vm.ExistingCamelotKey.Should().Be("11A");
        vm.ProposedCamelotKey.Should().BeNull();
        vm.HasCamelotKeyDiff.Should().BeFalse();
    }

    [Fact]
    public void UpdateFromOutcome_overwrites_both_existing_and_proposed_camelot()
    {
        var vm = new TrackOutcomeViewModel(DefaultFile, TrackTags.Empty);

        var existingAtScan = TrackTags.Empty with { Key = new MusicalKey("Am", "8A") };
        var resolved = ResolvedTrackTags.Empty with
        {
            Key = new ResolvedField<MusicalKey>(new MusicalKey("Cm", "5A"), TagFieldSource.Analysis, 1.0),
        };
        var outcome = new PipelineOutcome(DefaultFile, resolved, [], null, PipelineStatus.Skipped, [],
            PreMapResolved: null, ExistingAtScan: existingAtScan);
        vm.UpdateFromOutcome(outcome, existingAtScan);

        vm.ExistingCamelotKey.Should().Be("8A");
        vm.ProposedCamelotKey.Should().Be("5A");
        vm.HasCamelotKeyDiff.Should().BeTrue();
        vm.CamelotKeyDisplay.Should().Be("8A → 5A");
    }

    [Fact]
    public void EndApplySuccess_lifts_camelot_existing_to_proposed()
    {
        var existingAtScan = TrackTags.Empty with { Key = new MusicalKey("Am", "8A") };
        var resolved = ResolvedTrackTags.Empty with
        {
            Key = new ResolvedField<MusicalKey>(new MusicalKey("Cm", "5A"), TagFieldSource.Analysis, 1.0),
        };
        var outcome = new PipelineOutcome(DefaultFile, resolved, [], null, PipelineStatus.Skipped, [],
            PreMapResolved: null, ExistingAtScan: existingAtScan);
        var vm = new TrackOutcomeViewModel(outcome, existingAtScan);

        vm.EndApplySuccess();

        vm.ExistingCamelotKey.Should().Be("5A"); // lifted
        vm.ProposedCamelotKey.Should().Be("5A");
        vm.HasCamelotKeyDiff.Should().BeFalse();
    }

    [Fact]
    public void EndRevertSuccess_restores_camelot_from_snapshot()
    {
        var existingAtScan = TrackTags.Empty with { Key = new MusicalKey("Cm", "5A") };
        var resolved = ResolvedTrackTags.Empty with
        {
            Key = new ResolvedField<MusicalKey>(new MusicalKey("Gm", "6A"), TagFieldSource.Analysis, 1.0),
        };
        var outcome = new PipelineOutcome(DefaultFile, resolved, [], null, PipelineStatus.Skipped, [],
            PreMapResolved: null, ExistingAtScan: existingAtScan);
        var vm = new TrackOutcomeViewModel(outcome, existingAtScan);
        vm.HasSidecar = true;
        vm.EndApplySuccess(); // simulate prior write — existing now mirrors "6A"

        var restored = TrackTags.Empty with { Key = new MusicalKey("Cm", "5A") };
        vm.EndRevertSuccess(restored);

        vm.ExistingCamelotKey.Should().Be("5A");
        vm.HasCamelotKeyDiff.Should().BeTrue(); // Proposed stayed at "6A"
    }

    [Fact]
    public void Effective_props_prefer_Proposed_when_set()
    {
        // Post-scan row: rule engine overrode genre/bpm/key/energy/mood. Existing-* stays as
        // disk values, Proposed-* carries the new ones. EffectiveXxx must surface Proposed.
        var existing = TrackTags.Empty with
        {
            Genre = "House",
            Bpm = 100.0,
            Key = new MusicalKey("Am", "8A"),
            Energy = 5,
        };
        var resolved = ResolvedTrackTags.Empty with
        {
            Genre = new ResolvedField<string>("Techno", TagFieldSource.Rules, 1.0),
            Bpm = new ResolvedValueField<double>(128.0, TagFieldSource.Analysis, 1.0),
            Key = new ResolvedField<MusicalKey>(new MusicalKey("Cm", "5A"), TagFieldSource.Analysis, 1.0),
            Energy = new ResolvedValueField<int>(8, TagFieldSource.Analysis, 1.0),
        };
        var outcome = new PipelineOutcome(DefaultFile, resolved, [], null, PipelineStatus.Skipped, [],
            PreMapResolved: null, ExistingAtScan: existing);
        var vm = new TrackOutcomeViewModel(outcome, existing);

        vm.EffectiveGenre.Should().Be("Techno");
        vm.EffectiveBpm.Should().Be(128.0);
        vm.EffectiveKey.Should().Be("Cm");
        vm.EffectiveCamelotKey.Should().Be("5A");
        vm.EffectiveCamelotSortKey.Should().Be("05A");
        vm.EffectiveEnergy.Should().Be(8);
    }

    [Fact]
    public void Effective_props_fall_back_to_Existing_when_Proposed_is_null()
    {
        // Pre-scan / discovery-only row: only Existing-* is populated, every Proposed-* is null.
        // EffectiveXxx must surface the Existing value so the BPM range filter and the
        // SortMemberPath bindings still see the disk reality instead of treating the row as null.
        var existing = new TrackTags(
            Title: null, Artist: null, Album: null, Year: null,
            Genre: "House", SubGenre: "Tech", Bpm: 124.5, Key: new MusicalKey("Am", "8A"),
            Energy: 7, Mood: "Driving", SetPosition: "Peak Time", DurationSeconds: 381);

        var vm = new TrackOutcomeViewModel(DefaultFile, existing);

        vm.EffectiveGenre.Should().Be("House");
        vm.EffectiveSubGenre.Should().Be("Tech");
        vm.EffectiveBpm.Should().Be(124.5);
        vm.EffectiveKey.Should().Be("Am");
        vm.EffectiveCamelotKey.Should().Be("8A");
        vm.EffectiveCamelotSortKey.Should().Be("08A");
        vm.EffectiveEnergy.Should().Be(7);
        vm.EffectiveMood.Should().Be("Driving");
        vm.EffectiveSetPosition.Should().Be("Peak Time");
    }

    [Fact]
    public void Preview_ctor_with_discovery_error_flips_status_to_Fehler_and_stores_message()
    {
        var vm = new TrackOutcomeViewModel(DefaultFile, TrackTags.Empty, discoveryError: "unreadable header");

        vm.StatusLabel.Should().Be("Fehler");
        vm.StatusBadge.Should().Be("ERR");
        vm.ApplyError.Should().Be("unreadable header");
        vm.Errors.Should().ContainSingle()
            .Which.Should().StartWith("[Discovery]");
    }

    // ===== StatusBadge mapping =====================================================================

    [Theory]
    [InlineData("", "")]
    [InlineData("Scannen", "SCN")]
    [InlineData("Würde ändern", "CNG")]
    [InlineData("Fehler", "ERR")]
    [InlineData("Unverändert", "OK")]
    [InlineData("Geschrieben", "OK")]
    public void StatusBadge_maps_each_canonical_label(string label, string expectedBadge)
    {
        var vm = new TrackOutcomeViewModel(DefaultFile, TrackTags.Empty);
        // Drive through the lifecycle helpers — direct StatusLabel setter is internal-only and
        // wouldn't catch a regression where the helpers themselves stopped firing the change.
        switch (label)
        {
            case "Scannen": vm.BeginScan(); break;
            case "Fehler": vm.EndApplyFailure("x"); break;
            case "Geschrieben": vm.EndApplySuccess(); break;
            case "":
            case "Würde ändern":
            case "Unverändert":
                // Synthesize via UpdateFromOutcome with a synthetic PipelineOutcome.
                var status = label switch
                {
                    "Würde ändern" => PipelineStatus.Skipped,
                    "Unverändert" => PipelineStatus.Skipped,
                    _ => PipelineStatus.Skipped,
                };
                var existing = label == "Würde ändern"
                    ? new TrackTags(Genre: "House")
                    : TrackTags.Empty;
                var proposedTags = label == "Würde ändern"
                    ? ResolvedTrackTags.Empty with { Mood = new ResolvedField<string>("Driving", TagFieldSource.Rules, 1.0) }
                    : ResolvedTrackTags.Empty;
                var outcome = new PipelineOutcome(DefaultFile, proposedTags, [], null, status, [],
                    PreMapResolved: null, ExistingAtScan: existing);
                vm.UpdateFromOutcome(outcome, existing);
                if (label.Length == 0) vm.ResetStatus();
                break;
        }
        vm.StatusBadge.Should().Be(expectedBadge);
    }

    // ===== Lifecycle ===============================================================================

    [Fact]
    public void BeginScan_then_UpdateFromOutcome_transitions_SCN_to_terminal_state()
    {
        var vm = new TrackOutcomeViewModel(DefaultFile, TrackTags.Empty);

        vm.BeginScan();
        vm.StatusBadge.Should().Be("SCN");

        var resolved = ResolvedTrackTags.Empty with
        {
            Mood = new ResolvedField<string>("Driving", TagFieldSource.Rules, 1.0),
        };
        var outcome = new PipelineOutcome(DefaultFile, resolved, [], null, PipelineStatus.Skipped, [],
            PreMapResolved: null, ExistingAtScan: TrackTags.Empty);
        vm.UpdateFromOutcome(outcome, TrackTags.Empty);

        vm.StatusBadge.Should().Be("CNG");
        vm.ProposedMood.Should().Be("Driving");
    }

    [Fact]
    public void ResetStatus_clears_label_and_apply_error()
    {
        var vm = new TrackOutcomeViewModel(DefaultFile, TrackTags.Empty, discoveryError: "boom");
        vm.StatusBadge.Should().Be("ERR");

        vm.ResetStatus();

        vm.StatusBadge.Should().BeEmpty();
        vm.StatusLabel.Should().BeEmpty();
        vm.ApplyError.Should().BeNull();
    }

    // ===== File-derived display columns ============================================================

    [Fact]
    public void SizeDisplay_formats_bytes_with_locale_decimal_separator()
    {
        // 4_000_000 bytes ≈ 3.8 MB. Format uses CurrentCulture so test stays valid across locales.
        var vm = new TrackOutcomeViewModel(DefaultFile, TrackTags.Empty);
        vm.SizeDisplay.Should().MatchRegex(@"^3[.,]8 MB$");
    }

    [Fact]
    public void ModifiedDisplay_uses_dd_MM_yy_HH_mm_format()
    {
        var vm = new TrackOutcomeViewModel(DefaultFile, TrackTags.Empty);
        // The file's mtime is 2024-06-22 14:35 UTC. ToLocalTime() shifts by the host's offset, so
        // we can't pin the hour exactly — but format shape must hold.
        vm.ModifiedDisplay.Should().MatchRegex(@"^\d{2}\.\d{2}\.\d{2} \d{2}:\d{2}$");
    }

    [Fact]
    public void LengthDisplay_zero_or_missing_duration_renders_blank()
    {
        var vm = new TrackOutcomeViewModel(DefaultFile, new TrackTags(DurationSeconds: 0));
        vm.LengthDisplay.Should().BeEmpty();

        var vm2 = new TrackOutcomeViewModel(DefaultFile, new TrackTags(DurationSeconds: null));
        vm2.LengthDisplay.Should().BeEmpty();
    }
}
