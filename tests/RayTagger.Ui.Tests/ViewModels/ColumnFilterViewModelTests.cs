using System.Collections.ObjectModel;
using RayTagger.Core.Models;
using RayTagger.Ui.ViewModels;

namespace RayTagger.Ui.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="ColumnFilterViewModel"/> — the standalone filter VM extracted from
/// ScanViewModel. Constructible without DI, so we can directly exercise <c>ClearFilters</c>,
/// <c>HasActiveFilter</c>, and the predicate wiring.
/// </summary>
public class ColumnFilterViewModelTests
{
    private static readonly TrackFile DefaultFile =
        new("/library/a.mp3", AudioFormat.Mp3, 4_000_000, new DateTime(2024, 6, 22, 14, 35, 0, DateTimeKind.Utc));

    private static TrackOutcomeViewModel Row(TrackTags tags) => new(DefaultFile, tags);

    private static ColumnFilterViewModel BuildVm(params TrackOutcomeViewModel[] rows)
    {
        var source = new ObservableCollection<TrackOutcomeViewModel>(rows);
        return new ColumnFilterViewModel(source);
    }

    // ===== HasActiveFilter ========================================================================

    [Fact]
    public void HasActiveFilter_is_false_when_nothing_is_set()
    {
        var vm = BuildVm();
        vm.HasActiveFilter.Should().BeFalse();
    }

    [Fact]
    public void HasActiveFilter_flips_true_when_any_filter_set()
    {
        var vm = BuildVm();
        vm.Genre = "house";
        vm.HasActiveFilter.Should().BeTrue();
    }

    [Fact]
    public void HasActiveFilter_returns_to_false_when_filter_cleared_manually()
    {
        var vm = BuildVm();
        vm.Bpm = "120-128";
        vm.Bpm = null;
        vm.HasActiveFilter.Should().BeFalse();
    }

    // ===== ClearFiltersCommand (B4) ===============================================================

    [Fact]
    public void ClearFilters_resets_every_filter_property()
    {
        var vm = BuildVm();
        // Set every filter so the bulk-clear has something to do per column.
        vm.Status = "x";
        vm.FileName = "x";
        vm.Title = "x";
        vm.Artist = "x";
        vm.Year = "x";
        vm.Genre = "x";
        vm.SubGenre = "x";
        vm.Bpm = "x";
        vm.Key = "x";
        vm.CamelotKey = "x";
        vm.Energy = "x";
        vm.Mood = "x";
        vm.SetPosition = "x";
        vm.Album = "x";
        vm.Length = "x";
        vm.Size = "x";
        vm.Modified = "x";

        vm.ClearFiltersCommand.Execute(null);

        vm.Status.Should().BeNull();
        vm.FileName.Should().BeNull();
        vm.Title.Should().BeNull();
        vm.Artist.Should().BeNull();
        vm.Year.Should().BeNull();
        vm.Genre.Should().BeNull();
        vm.SubGenre.Should().BeNull();
        vm.Bpm.Should().BeNull();
        vm.Key.Should().BeNull();
        vm.CamelotKey.Should().BeNull();
        vm.Energy.Should().BeNull();
        vm.Mood.Should().BeNull();
        vm.SetPosition.Should().BeNull();
        vm.Album.Should().BeNull();
        vm.Length.Should().BeNull();
        vm.Size.Should().BeNull();
        vm.Modified.Should().BeNull();
        vm.HasActiveFilter.Should().BeFalse();
    }

    // ===== ClearFiltersCommand.CanExecute (C4) =====================================================

    [Fact]
    public void ClearFiltersCommand_CanExecute_false_when_no_filter_active()
    {
        var vm = BuildVm();
        vm.ClearFiltersCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ClearFiltersCommand_CanExecute_true_after_any_filter_set()
    {
        var vm = BuildVm();
        vm.Artist = "anjun";
        vm.ClearFiltersCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void ClearFiltersCommand_CanExecute_false_after_clear()
    {
        var vm = BuildVm();
        vm.Artist = "anjun";
        vm.ClearFiltersCommand.Execute(null);
        vm.ClearFiltersCommand.CanExecute(null).Should().BeFalse();
    }

    // ===== View / predicate wiring =================================================================

    [Fact]
    public void View_starts_unfiltered_and_passes_every_row()
    {
        var trackA = TrackTags.Empty with { Genre = "House", Bpm = 124 };
        var trackB = TrackTags.Empty with { Genre = "Techno", Bpm = 132 };
        var vm = BuildVm(Row(trackA), Row(trackB));

        vm.View.Count.Should().Be(2);
    }

    [Fact]
    public void View_filters_when_genre_set()
    {
        var trackA = TrackTags.Empty with { Genre = "House", Bpm = 124 };
        var trackB = TrackTags.Empty with { Genre = "Techno", Bpm = 132 };
        var vm = BuildVm(Row(trackA), Row(trackB));

        vm.Genre = "tech";

        vm.View.Count.Should().Be(1);
    }

    [Fact]
    public void View_filter_AND_combines_multiple_columns()
    {
        var trackA = TrackTags.Empty with { Genre = "House", Bpm = 124 };
        var trackB = TrackTags.Empty with { Genre = "House", Bpm = 132 };
        var trackC = TrackTags.Empty with { Genre = "Techno", Bpm = 132 };
        var vm = BuildVm(Row(trackA), Row(trackB), Row(trackC));

        vm.Genre = "house";
        vm.Bpm = "130-140";

        // Only trackB matches: House AND BPM in 130-140.
        vm.View.Count.Should().Be(1);
    }

    [Fact]
    public void FilterChanged_fires_on_property_set()
    {
        var vm = BuildVm();
        var fireCount = 0;
        vm.FilterChanged += (_, _) => fireCount++;

        vm.Genre = "house";
        fireCount.Should().Be(1);

        vm.Genre = "techno";
        fireCount.Should().Be(2);
    }

    [Fact]
    public void FilterChanged_fires_once_for_bulk_clear()
    {
        var vm = BuildVm();
        vm.Artist = "x";
        vm.Genre = "y";
        vm.Bpm = "z";

        var fireCount = 0;
        vm.FilterChanged += (_, _) => fireCount++;

        vm.ClearFiltersCommand.Execute(null);

        // Despite clearing 3 set + 14 already-null props, only one refresh event fires.
        fireCount.Should().Be(1);
    }
}
