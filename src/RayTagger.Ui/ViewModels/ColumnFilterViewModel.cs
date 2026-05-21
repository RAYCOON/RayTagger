using System.Collections.ObjectModel;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// Owns the 17 per-column filter strings and the <see cref="DataGridCollectionView"/> that
/// applies them to the bound source collection. Extracted from <see cref="ScanViewModel"/> so
/// the filter machinery has its own SRP-clean home — and so the unit-test layer can exercise it
/// without instantiating the full scan VM with its DI dependencies.
/// </summary>
/// <remarks>
/// Construct with the underlying <see cref="ObservableCollection{T}"/> that drives the grid;
/// the view-model wires up a filter predicate that delegates to <see cref="FilterPredicates"/>
/// and refreshes the view on every property change (case-insensitive contains; BPM additionally
/// supports range / comparison syntax). Bulk-clearing all filters is funnelled through one
/// final refresh via the suppression counter, avoiding 17 individual passes through the
/// predicate when the user clicks "Filter zurücksetzen".
/// </remarks>
public sealed partial class ColumnFilterViewModel : ObservableObject
{
    /// <summary>
    /// Filtered view of the source collection. The DataGrid binds <c>ItemsSource</c> against
    /// this — its <see cref="DataGridCollectionView.Filter"/> is set to a predicate that
    /// snapshots all 17 filter strings and runs them through <see cref="FilterPredicates"/>.
    /// </summary>
    public DataGridCollectionView View { get; }

    /// <summary>
    /// Fires whenever any filter property changes. Owners subscribe to re-evaluate commands
    /// whose CanExecute depends on the visible subset (e.g. "Alle anwenden" must grey out when
    /// the active filter hides every pending row).
    /// </summary>
    public event EventHandler? FilterChanged;

    // Per-column filter strings. Bound to the inline TextBox in each column's HeaderTemplate.
    // Empty / null = no filter on that column. Match strategy lives in FilterPredicates.
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _fileName;
    [ObservableProperty] private string? _title;
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private string? _year;
    [ObservableProperty] private string? _genre;
    [ObservableProperty] private string? _subGenre;
    [ObservableProperty] private string? _bpm;
    [ObservableProperty] private string? _key;
    [ObservableProperty] private string? _camelotKey;
    [ObservableProperty] private string? _energy;
    [ObservableProperty] private string? _mood;
    [ObservableProperty] private string? _setPosition;
    [ObservableProperty] private string? _album;
    [ObservableProperty] private string? _length;
    [ObservableProperty] private string? _size;
    [ObservableProperty] private string? _modified;

    /// <summary>
    /// Counter that suppresses <see cref="RaiseRefresh"/>'s actual refresh while
    /// <see cref="ClearFilters"/> is mid-flight setting all properties. We can't use the
    /// view's <c>DeferRefresh()</c> because the partial-method-driven setters call
    /// <see cref="DataGridCollectionView.Refresh"/> directly inside the defer-scope — Avalonia
    /// rejects nested refreshes during deferred mode.
    /// </summary>
    private int _refreshSuppressDepth;

    public ColumnFilterViewModel(ObservableCollection<TrackOutcomeViewModel> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        View = new DataGridCollectionView(source) { Filter = MatchesPredicate };
    }

    private bool MatchesPredicate(object item)
    {
        if (item is not TrackOutcomeViewModel r) return false;
        return FilterPredicates.MatchesAllFilters(r, Snapshot());
    }

    /// <summary>Materializes the current filter state into an immutable snapshot for the predicate.</summary>
    public ColumnFilters Snapshot() => new(
        Status, FileName, Title, Artist, Year,
        Genre, SubGenre, Bpm, Key, CamelotKey,
        Energy, Mood, SetPosition, Album, Length,
        Size, Modified);

    /// <summary>True iff at least one filter string is non-empty. Drives the
    /// <see cref="ClearFiltersCommand"/>'s CanExecute so the button greys out when nothing is filtered.</summary>
    public bool HasActiveFilter =>
        !string.IsNullOrEmpty(Status)
        || !string.IsNullOrEmpty(FileName)
        || !string.IsNullOrEmpty(Title)
        || !string.IsNullOrEmpty(Artist)
        || !string.IsNullOrEmpty(Year)
        || !string.IsNullOrEmpty(Genre)
        || !string.IsNullOrEmpty(SubGenre)
        || !string.IsNullOrEmpty(Bpm)
        || !string.IsNullOrEmpty(Key)
        || !string.IsNullOrEmpty(CamelotKey)
        || !string.IsNullOrEmpty(Energy)
        || !string.IsNullOrEmpty(Mood)
        || !string.IsNullOrEmpty(SetPosition)
        || !string.IsNullOrEmpty(Album)
        || !string.IsNullOrEmpty(Length)
        || !string.IsNullOrEmpty(Size)
        || !string.IsNullOrEmpty(Modified);

    private void RaiseRefresh()
    {
        if (_refreshSuppressDepth > 0) return;
        View.Refresh();
        OnPropertyChanged(nameof(HasActiveFilter));
        ClearFiltersCommand.NotifyCanExecuteChanged();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnStatusChanged(string? value) => RaiseRefresh();
    partial void OnFileNameChanged(string? value) => RaiseRefresh();
    partial void OnTitleChanged(string? value) => RaiseRefresh();
    partial void OnArtistChanged(string? value) => RaiseRefresh();
    partial void OnYearChanged(string? value) => RaiseRefresh();
    partial void OnGenreChanged(string? value) => RaiseRefresh();
    partial void OnSubGenreChanged(string? value) => RaiseRefresh();
    partial void OnBpmChanged(string? value) => RaiseRefresh();
    partial void OnKeyChanged(string? value) => RaiseRefresh();
    partial void OnCamelotKeyChanged(string? value) => RaiseRefresh();
    partial void OnEnergyChanged(string? value) => RaiseRefresh();
    partial void OnMoodChanged(string? value) => RaiseRefresh();
    partial void OnSetPositionChanged(string? value) => RaiseRefresh();
    partial void OnAlbumChanged(string? value) => RaiseRefresh();
    partial void OnLengthChanged(string? value) => RaiseRefresh();
    partial void OnSizeChanged(string? value) => RaiseRefresh();
    partial void OnModifiedChanged(string? value) => RaiseRefresh();

    /// <summary>
    /// Clears every column filter — bound to the toolbar's "Filter zurücksetzen" button.
    /// Suppresses per-setter refreshes via <see cref="_refreshSuppressDepth"/> so the view
    /// re-evaluates once at the end instead of 17 times mid-clear.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasActiveFilter))]
    private void ClearFilters()
    {
        _refreshSuppressDepth++;
        try
        {
            Status = null;
            FileName = null;
            Title = null;
            Artist = null;
            Year = null;
            Genre = null;
            SubGenre = null;
            Bpm = null;
            Key = null;
            CamelotKey = null;
            Energy = null;
            Mood = null;
            SetPosition = null;
            Album = null;
            Length = null;
            Size = null;
            Modified = null;
        }
        finally
        {
            _refreshSuppressDepth--;
        }
        RaiseRefresh();
    }
}
