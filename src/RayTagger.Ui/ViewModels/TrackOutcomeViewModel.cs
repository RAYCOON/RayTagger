using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RayTagger.Core.Models;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// One row in the results grid. Captures the diff between the file's existing tags and what the
/// pipeline proposes (or actually wrote in non-dry-run mode), plus enough provenance for the user
/// to understand WHY each field changed.
/// </summary>
/// <remarks>
/// Mutable state is intentional: after Apply succeeds we flip the Status / StatusLabel and lift
/// the "Existing" values to match "Proposed" (the file on disk now holds those). That keeps the
/// row visible in the grid with a "Geschrieben" badge instead of forcing a re-scan.
/// </remarks>
public sealed partial class TrackOutcomeViewModel : ObservableObject
{
    public string Path { get; }
    public string FileName { get; }

    /// <summary>The underlying pipeline outcome — used as the input to a deferred write.</summary>
    public PipelineOutcome SourceOutcome { get; private set; }

    /// <summary>The pre-map resolved tags from the scan, kept so live-preview can re-evaluate
    /// the rule engine against a newly-edited mappings.yaml without re-reading the file.</summary>
    public ResolvedTrackTags? PreMapResolved => SourceOutcome.PreMapResolved;

    /// <summary>The existing tags at scan time — feeds rule predicates that look at artist / path.</summary>
    public TrackTags? ExistingAtScan => SourceOutcome.ExistingAtScan;

    [ObservableProperty] private TagFieldSource _genreSource;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGenreDiff))]
    [NotifyPropertyChangedFor(nameof(GenreDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveGenre))]
    private string? _proposedGenre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubGenreDiff))]
    [NotifyPropertyChangedFor(nameof(SubGenreDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveSubGenre))]
    private string? _proposedSubGenre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBpmDiff))]
    [NotifyPropertyChangedFor(nameof(BpmDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveBpm))]
    private double? _proposedBpm;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyDiff))]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveKey))]
    private string? _proposedKey;
    // Camelot is the same musical key as Standard but in Wheel notation (e.g. "Am" ↔ "8A").
    // Carried separately so the grid can render both columns and highlight each diff
    // independently — even though they always flip together (1:1 mapping in MusicalKey).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCamelotKeyDiff))]
    [NotifyPropertyChangedFor(nameof(CamelotKeyDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveCamelotKey))]
    [NotifyPropertyChangedFor(nameof(EffectiveCamelotSortKey))]
    private string? _proposedCamelotKey;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEnergyDiff))]
    [NotifyPropertyChangedFor(nameof(EnergyDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveEnergy))]
    private int? _proposedEnergy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoodDiff))]
    [NotifyPropertyChangedFor(nameof(MoodDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveMood))]
    private string? _proposedMood;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetPositionDiff))]
    [NotifyPropertyChangedFor(nameof(SetPositionDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveSetPosition))]
    private string? _proposedSetPosition;
    [ObservableProperty] private IReadOnlyList<string> _appliedRules = [];
    [ObservableProperty] private string? _destinationPath;

    /// <summary>
    /// True when the pipeline's BPM-snap post-step (or the analyzer's own snap on a folded
    /// in-range value) rounded the value (e.g. 122.07 → 122). Drives the BPM cell's dark-red
    /// foreground so the user can see which values were corrected vs. emitted verbatim. Takes
    /// effect only when <see cref="BpmIsForcedFallback"/> is false — forced-fallback wins.
    /// </summary>
    [ObservableProperty] private bool _bpmWasSnapped;

    /// <summary>
    /// True when the BPM analyzer's genre-range fold (×2 / ÷2 + snap) couldn't bring the value
    /// back into the configured genre interval and fell back to <c>snap(raw)</c>. Drives the BPM
    /// cell's dark-blue foreground so the user notices the unresolved disagreement between the
    /// configured range and the audio's natural tempo.
    /// </summary>
    [ObservableProperty] private bool _bpmIsForcedFallback;
    /// <summary>
    /// Per-stage error messages. Settable so <see cref="UpdateFromOutcome"/> can refresh the list
    /// when a preview row transitions into a scanned row (the original ctor's init-only assignment
    /// would lock pre-scan blanks in forever).
    /// </summary>
    public IReadOnlyList<string> Errors { get; private set; } = [];

    // Per-field diff flags drive the gelbes Cell-Highlight in the results grid. True iff the
    // proposed value differs from the existing one (and we actually have a proposed value to
    // highlight — null proposed → no highlight, otherwise every Unverändert row would glow).
    public bool HasGenreDiff => ProposedGenre is not null && !string.Equals(ExistingGenre, ProposedGenre, StringComparison.Ordinal);
    public bool HasSubGenreDiff => ProposedSubGenre is not null && !string.Equals(ExistingSubGenre, ProposedSubGenre, StringComparison.Ordinal);
    public bool HasBpmDiff => ProposedBpm is not null && ProposedBpm != ExistingBpm;
    public bool HasKeyDiff => ProposedKey is not null && !string.Equals(ExistingKey, ProposedKey, StringComparison.Ordinal);
    public bool HasCamelotKeyDiff => ProposedCamelotKey is not null && !string.Equals(ExistingCamelotKey, ProposedCamelotKey, StringComparison.Ordinal);
    public bool HasEnergyDiff => ProposedEnergy is not null && ProposedEnergy != ExistingEnergy;
    public bool HasMoodDiff => ProposedMood is not null && !string.Equals(ExistingMood, ProposedMood, StringComparison.Ordinal);
    public bool HasSetPositionDiff => ProposedSetPosition is not null && !string.Equals(ExistingSetPosition, ProposedSetPosition, StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(IsApplied))]
    private PipelineStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(IsApplied))]
    [NotifyPropertyChangedFor(nameof(StatusBadge))]
    private string _statusLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    private bool _isApplying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    private bool _isReverting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    private bool _hasSidecar;

    [ObservableProperty] private string? _applyError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGenreDiff))]
    [NotifyPropertyChangedFor(nameof(GenreDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveGenre))]
    private string? _existingGenre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubGenreDiff))]
    [NotifyPropertyChangedFor(nameof(SubGenreDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveSubGenre))]
    private string? _existingSubGenre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBpmDiff))]
    [NotifyPropertyChangedFor(nameof(BpmDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveBpm))]
    private double? _existingBpm;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyDiff))]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveKey))]
    private string? _existingKey;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCamelotKeyDiff))]
    [NotifyPropertyChangedFor(nameof(CamelotKeyDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveCamelotKey))]
    [NotifyPropertyChangedFor(nameof(EffectiveCamelotSortKey))]
    private string? _existingCamelotKey;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEnergyDiff))]
    [NotifyPropertyChangedFor(nameof(EnergyDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveEnergy))]
    private int? _existingEnergy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoodDiff))]
    [NotifyPropertyChangedFor(nameof(MoodDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveMood))]
    private string? _existingMood;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetPositionDiff))]
    [NotifyPropertyChangedFor(nameof(SetPositionDisplay))]
    [NotifyPropertyChangedFor(nameof(EffectiveSetPosition))]
    private string? _existingSetPosition;

    // Display strings used by the results grid. Show "current → new" only when the proposal
    // actually differs from disk; otherwise just the current value so unchanged rows aren't
    // visually noisy with redundant arrows. Empty / null values render as blank (rather than
    // "—") so the column stays clean for tracks that legitimately have no value in this field.
    public string GenreDisplay => FormatDiff(ExistingGenre, ProposedGenre, HasGenreDiff);
    public string SubGenreDisplay => FormatDiff(ExistingSubGenre, ProposedSubGenre, HasSubGenreDiff);
    public string BpmDisplay => FormatBpmDiff(ExistingBpm, ProposedBpm, HasBpmDiff);
    public string KeyDisplay => FormatDiff(ExistingKey, ProposedKey, HasKeyDiff);
    public string CamelotKeyDisplay => FormatDiff(ExistingCamelotKey, ProposedCamelotKey, HasCamelotKeyDiff);

    /// <summary>
    /// Wheel-ordered sort key for the Camelot column. Delegates to
    /// <see cref="KeyNotationConverter.CamelotSortKey"/> in the Core layer — keeps the pure
    /// notation logic out of the view model. Driven off <see cref="EffectiveCamelotKey"/> so
    /// rows still in Discovery (no proposed yet) sort by the value on disk instead of
    /// dropping to the bottom as null.
    /// </summary>
    public string? EffectiveCamelotSortKey => KeyNotationConverter.CamelotSortKey(EffectiveCamelotKey);

    // Effective values = "what the cell would show as the authoritative number/string", i.e.
    // proposed when the pipeline produced one, otherwise the existing value from disk. Used
    // by SortMemberPath bindings and by the BPM range/comparison filter so:
    //  - pre-scan rows (only Existing populated) still appear in numeric range filters,
    //  - mid-scan rows (proposed not yet emitted) sort by their visible disk value,
    //  - failed analyzer rows (proposed stays null) don't all collapse to the bottom.
    public string? EffectiveGenre => ProposedGenre ?? ExistingGenre;
    public string? EffectiveSubGenre => ProposedSubGenre ?? ExistingSubGenre;
    public double? EffectiveBpm => ProposedBpm ?? ExistingBpm;
    public string? EffectiveKey => ProposedKey ?? ExistingKey;
    public string? EffectiveCamelotKey => ProposedCamelotKey ?? ExistingCamelotKey;
    public int? EffectiveEnergy => ProposedEnergy ?? ExistingEnergy;
    public string? EffectiveMood => ProposedMood ?? ExistingMood;
    public string? EffectiveSetPosition => ProposedSetPosition ?? ExistingSetPosition;
    public string EnergyDisplay => FormatIntDiff(ExistingEnergy, ProposedEnergy, HasEnergyDiff);
    public string MoodDisplay => FormatDiff(ExistingMood, ProposedMood, HasMoodDiff);
    public string SetPositionDisplay => FormatDiff(ExistingSetPosition, ProposedSetPosition, HasSetPositionDiff);

    // Display rules (apply to every dimension):
    //   - No diff                 → just the existing value (or blank when it's null)
    //   - Diff, existing empty    → just the proposed value (no leading "→ X" with empty prefix)
    //   - Diff, both have a value → "existing → proposed"
    // The empty-existing case matters: tracks without a prior BPM/key/etc. shouldn't render
    // as " → Am" (whitespace, arrow, value) — it reads like an artifact rather than information.
    private static string FormatDiff(string? existing, string? proposed, bool hasDiff)
    {
        if (!hasDiff) return existing ?? string.Empty;
        if (string.IsNullOrEmpty(existing)) return proposed ?? string.Empty;
        return $"{existing} → {proposed ?? string.Empty}";
    }

    private static string FormatBpmDiff(double? existing, double? proposed, bool hasDiff)
    {
        if (!hasDiff) return FormatBpm(existing);
        var existingText = FormatBpm(existing);
        var proposedText = FormatBpm(proposed);
        if (existingText.Length == 0) return proposedText;
        return $"{existingText} → {proposedText}";
    }

    private static string FormatBpm(double? value) =>
        value is null ? string.Empty : value.Value.ToString("0.##", CultureInfo.CurrentCulture);

    private static string FormatIntDiff(int? existing, int? proposed, bool hasDiff)
    {
        if (!hasDiff) return FormatInt(existing);
        var existingText = FormatInt(existing);
        var proposedText = FormatInt(proposed);
        if (existingText.Length == 0) return proposedText;
        return $"{existingText} → {proposedText}";
    }

    private static string FormatInt(int? value) =>
        value is null ? string.Empty : value.Value.ToString(CultureInfo.CurrentCulture);

    // Read-only metadata columns. These are frozen at scan time — the rule engine doesn't write
    // Title/Artist/Album/Year, and file size / modified-time live in the filesystem, not in tags.
    public string Title => ExistingAtScan?.Title ?? string.Empty;
    public string Artist => ExistingAtScan?.Artist ?? string.Empty;
    public string Album => ExistingAtScan?.Album ?? string.Empty;
    public string Year => ExistingAtScan?.Year is { } y ? y.ToString(CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>Track length in <c>M:SS</c> form (or blank if duration is unknown).</summary>
    public string LengthDisplay
    {
        get
        {
            var seconds = ExistingAtScan?.DurationSeconds;
            if (seconds is null || seconds <= 0) return string.Empty;
            var s = seconds.Value;
            return $"{s / 60}:{(s % 60).ToString("D2", CultureInfo.InvariantCulture)}";
        }
    }

    /// <summary>File size with locale-aware decimal — e.g. "12,4 MB" on German systems.</summary>
    public string SizeDisplay => FormatSize(SourceOutcome.File.SizeBytes);

    /// <summary>Last-modified timestamp in <c>dd.MM.yy HH:mm</c> form (24-hour, German short date).</summary>
    public string ModifiedDisplay =>
        SourceOutcome.File.LastModifiedUtc.ToLocalTime().ToString("dd.MM.yy HH:mm", CultureInfo.CurrentCulture);

    // Raw sort keys — the DataGrid sorts lexicographically by default, which would order
    // "10:05" before "1:23" and "9 MB" before "10 MB". The grid binds these via SortMemberPath
    // so the header click compares the underlying number/date instead of the formatted string.
    public int? LengthSeconds => ExistingAtScan?.DurationSeconds;
    public long SizeBytes => SourceOutcome.File.SizeBytes;
    public DateTime LastModifiedUtc => SourceOutcome.File.LastModifiedUtc;

    /// <summary>
    /// Short three-letter status pill. The full <see cref="StatusLabel"/> still drives the tooltip;
    /// this just gives the leftmost column a fixed-width glyph the user can scan at a glance.
    /// Empty during discovery (no scan yet); SCN while the pipeline is actively working on the row.
    /// </summary>
    public string StatusBadge => StatusLabel switch
    {
        "" => "",
        "Scannen" => "SCN",
        "Würde ändern" => "CNG",
        "Fehler" => "ERR",
        _ => "OK",
    };

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb.ToString("0.#", CultureInfo.CurrentCulture)} KB";
        var mb = kb / 1024.0;
        if (mb < 1024) return $"{mb.ToString("0.#", CultureInfo.CurrentCulture)} MB";
        var gb = mb / 1024.0;
        return $"{gb.ToString("0.##", CultureInfo.CurrentCulture)} GB";
    }

    /// <summary>True iff this row has a pending change the user can apply.</summary>
    public bool CanApply => !IsApplying && !IsReverting && StatusLabel == "Würde ändern";

    /// <summary>True after a successful apply — drives the green check glyph + suppresses the button.</summary>
    public bool IsApplied => Status == PipelineStatus.Written;

    /// <summary>True iff a backup-sidecar exists on disk and no Apply/Revert is in flight.</summary>
    public bool CanRevert => HasSidecar && !IsApplying && !IsReverting;

    public TrackOutcomeViewModel(PipelineOutcome outcome, TrackTags existing)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(existing);

        SourceOutcome = outcome;
        Path = outcome.File.Path;
        FileName = System.IO.Path.GetFileName(outcome.File.Path);
        Status = outcome.Status;

        StatusLabel = outcome.Status switch
        {
            PipelineStatus.Failed => "Fehler",
            PipelineStatus.Written => "Geschrieben",
            _ when HasProposedChanges(outcome, existing) => "Würde ändern",
            _ => "Unverändert",
        };

        ExistingGenre = existing.Genre;
        ExistingSubGenre = existing.SubGenre;
        ExistingBpm = existing.Bpm;
        ExistingKey = existing.Key?.Standard;
        ExistingCamelotKey = existing.Key?.Camelot;
        ExistingEnergy = existing.Energy;
        ExistingMood = existing.Mood;
        ExistingSetPosition = existing.SetPosition;

        ApplyResolvedFromOutcome(outcome);
        BpmWasSnapped = outcome.BpmWasSnapped;
        BpmIsForcedFallback = outcome.BpmIsForcedFallback;
        Errors = [.. outcome.Errors.Select(e => $"[{e.Stage}] {e.Message}")];
    }

    /// <summary>
    /// Preview constructor — used by the Discovery phase when the UI wants to populate the grid
    /// before any scan runs. Existing-* tags come from the disk read; Proposed-* stays null so
    /// the diff highlights are off. <see cref="StatusLabel"/> is blank (StatusBadge renders "")
    /// until either <see cref="BeginScan"/> sets it to "Scannen" or <see cref="UpdateFromOutcome"/>
    /// promotes it to a terminal "OK / CNG / ERR" state.
    /// </summary>
    /// <param name="file">Filesystem identity (path / size / mtime).</param>
    /// <param name="existing">Tags read off disk during discovery.</param>
    /// <param name="discoveryError">
    /// When non-null, the row enters "Fehler" status immediately — used for files whose tag read
    /// blew up (corrupt header, etc.). The pipeline's scan stage will overwrite this if it succeeds.
    /// </param>
    public TrackOutcomeViewModel(TrackFile file, TrackTags existing, string? discoveryError = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(existing);

        // Stub PipelineOutcome lets SourceOutcome stay non-nullable. Resolved is empty so the
        // ApplyResolvedFromOutcome path (not called here) would leave Proposed-* null anyway;
        // ExistingAtScan carries the disk tags for the UI's Title/Artist/Album/Length getters.
        SourceOutcome = new PipelineOutcome(
            File: file,
            Resolved: ResolvedTrackTags.Empty,
            AppliedRules: [],
            DestinationPath: null,
            Status: PipelineStatus.Skipped,
            Errors: [],
            PreMapResolved: null,
            ExistingAtScan: existing);

        Path = file.Path;
        FileName = System.IO.Path.GetFileName(file.Path);
        Status = PipelineStatus.Skipped;
        StatusLabel = discoveryError is null ? string.Empty : "Fehler";
        ApplyError = discoveryError;
        Errors = discoveryError is null ? [] : [$"[Discovery] {discoveryError}"];

        ExistingGenre = existing.Genre;
        ExistingSubGenre = existing.SubGenre;
        ExistingBpm = existing.Bpm;
        ExistingKey = existing.Key?.Standard;
        ExistingCamelotKey = existing.Key?.Camelot;
        ExistingEnergy = existing.Energy;
        ExistingMood = existing.Mood;
        ExistingSetPosition = existing.SetPosition;
        // Proposed-* stay null — no scan output yet, so HasXxxDiff is false everywhere.
    }

    /// <summary>
    /// Promotes a preview row to a scanned row: swaps the stub <see cref="SourceOutcome"/> for the
    /// real pipeline result and recomputes <see cref="StatusLabel"/> + every Existing-/Proposed-*.
    /// Called by <see cref="ScanViewModel"/> once the pipeline finishes processing the file.
    /// </summary>
    public void UpdateFromOutcome(PipelineOutcome outcome, TrackTags existing)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(existing);

        SourceOutcome = outcome;
        Status = outcome.Status;
        StatusLabel = outcome.Status switch
        {
            PipelineStatus.Failed => "Fehler",
            PipelineStatus.Written => "Geschrieben",
            _ when HasProposedChanges(outcome, existing) => "Würde ändern",
            _ => "Unverändert",
        };
        ApplyError = null;
        Errors = [.. outcome.Errors.Select(e => $"[{e.Stage}] {e.Message}")];

        ExistingGenre = existing.Genre;
        ExistingSubGenre = existing.SubGenre;
        ExistingBpm = existing.Bpm;
        ExistingKey = existing.Key?.Standard;
        ExistingCamelotKey = existing.Key?.Camelot;
        ExistingEnergy = existing.Energy;
        ExistingMood = existing.Mood;
        ExistingSetPosition = existing.SetPosition;

        ApplyResolvedFromOutcome(outcome);
        BpmWasSnapped = outcome.BpmWasSnapped;
        BpmIsForcedFallback = outcome.BpmIsForcedFallback;
    }

    /// <summary>Mark the row as "currently being scanned" — flips the badge to SCN.</summary>
    public void BeginScan() => StatusLabel = "Scannen";

    /// <summary>
    /// Reset the row to the pre-scan state (empty StatusLabel → empty badge). Used by a re-scan
    /// to clear any prior CNG/OK/ERR before the pipeline picks rows back up.
    /// </summary>
    public void ResetStatus()
    {
        StatusLabel = string.Empty;
        ApplyError = null;
    }

    private void ApplyResolvedFromOutcome(PipelineOutcome outcome)
    {
        ProposedGenre = outcome.Resolved.Genre.Value;
        GenreSource = outcome.Resolved.Genre.Source;
        ProposedSubGenre = outcome.Resolved.SubGenre.Value;
        ProposedBpm = outcome.Resolved.Bpm.Value;
        ProposedKey = outcome.Resolved.Key.Value?.Standard;
        ProposedCamelotKey = outcome.Resolved.Key.Value?.Camelot;
        ProposedEnergy = outcome.Resolved.Energy.Value;
        ProposedMood = outcome.Resolved.Mood.Value;
        ProposedSetPosition = outcome.Resolved.SetPosition.Value;
        DestinationPath = outcome.DestinationPath;
        AppliedRules = [.. outcome.AppliedRules.Select(r => r.RuleName)];
    }

    /// <summary>
    /// Replaces the cached <see cref="SourceOutcome"/> with the result of re-evaluating the rule
    /// engine on top of the existing pre-map state. Used by Live-Preview so a rule edit shows up
    /// in the grid immediately without re-scanning the file. Recomputes <see cref="StatusLabel"/>
    /// off the new resolved tags so "Würde ändern" / "Unverändert" stay accurate.
    /// </summary>
    public void UpdatePreview(PipelineOutcome refreshedOutcome, TrackTags existing)
    {
        ArgumentNullException.ThrowIfNull(refreshedOutcome);
        ArgumentNullException.ThrowIfNull(existing);

        SourceOutcome = refreshedOutcome;
        ApplyResolvedFromOutcome(refreshedOutcome);

        // Keep the row's StatusLabel honest after the preview swap: a previously "Würde ändern"
        // row might collapse to "Unverändert" if the user's new rule chain produces no diff.
        if (Status is PipelineStatus.Written or PipelineStatus.Failed)
        {
            // Don't overwrite terminal labels — the user has already applied / failed this row.
            return;
        }
        StatusLabel = HasProposedChanges(refreshedOutcome, existing) ? "Würde ändern" : "Unverändert";
    }

    /// <summary>Mark the row "in progress" — disables its button + raises CanApply.</summary>
    public void BeginApply()
    {
        ApplyError = null;
        IsApplying = true;
    }

    /// <summary>
    /// Flip the row to <see cref="PipelineStatus.Written"/> after a successful write. Lifts every
    /// Existing-* to match Proposed-* because the file on disk now holds those values; the diff
    /// arrow effectively collapses to "X → X" so the user can still see the new state without
    /// re-scanning.
    /// </summary>
    public void EndApplySuccess()
    {
        ExistingGenre = ProposedGenre;
        ExistingSubGenre = ProposedSubGenre;
        ExistingBpm = ProposedBpm;
        ExistingKey = ProposedKey;
        ExistingCamelotKey = ProposedCamelotKey;
        ExistingEnergy = ProposedEnergy;
        ExistingMood = ProposedMood;
        ExistingSetPosition = ProposedSetPosition;

        IsApplying = false;
        Status = PipelineStatus.Written;
        StatusLabel = "Geschrieben";
    }

    /// <summary>Failure path — flip to red status + surface the message via <see cref="ApplyError"/>.</summary>
    public void EndApplyFailure(string error)
    {
        IsApplying = false;
        Status = PipelineStatus.Failed;
        StatusLabel = "Fehler";
        ApplyError = error;
    }

    /// <summary>Mark the row "in progress" for a Revert operation.</summary>
    public void BeginRevert()
    {
        ApplyError = null;
        IsReverting = true;
    }

    /// <summary>
    /// After a successful Revert: lift every Existing-* to the snapshot values so the diff
    /// column reflects what's actually on disk. The Proposed-* values stay as they were (cached
    /// from the original scan) — that's intentional, it lets the user see "the rules would
    /// re-apply these changes if you click Anwenden again". HasSidecar clears because the
    /// coordinator deleted the file. StatusLabel flips back to "Würde ändern" iff the proposed
    /// state still differs from the restored existing state — otherwise "Unverändert".
    /// </summary>
    public void EndRevertSuccess(TrackTags restored)
    {
        ArgumentNullException.ThrowIfNull(restored);

        ExistingGenre = restored.Genre;
        ExistingSubGenre = restored.SubGenre;
        ExistingBpm = restored.Bpm;
        ExistingKey = restored.Key?.Standard;
        ExistingCamelotKey = restored.Key?.Camelot;
        ExistingEnergy = restored.Energy;
        ExistingMood = restored.Mood;
        ExistingSetPosition = restored.SetPosition;

        IsReverting = false;
        HasSidecar = false;
        // Reset Status to neutral. The label below picks the right tone based on whether the
        // proposed state would still change anything against the restored existing state.
        Status = PipelineStatus.Skipped;
        StatusLabel = StillHasProposedChanges(restored) ? "Würde ändern" : "Unverändert";
    }

    private bool StillHasProposedChanges(TrackTags restored) =>
        !string.Equals(restored.Genre, ProposedGenre, StringComparison.Ordinal)
        || !string.Equals(restored.SubGenre, ProposedSubGenre, StringComparison.Ordinal)
        || restored.Bpm != ProposedBpm
        || !string.Equals(restored.Key?.Standard, ProposedKey, StringComparison.Ordinal)
        || restored.Energy != ProposedEnergy
        || !string.Equals(restored.Mood, ProposedMood, StringComparison.Ordinal)
        || !string.Equals(restored.SetPosition, ProposedSetPosition, StringComparison.Ordinal);

    /// <summary>Revert failed — show the error, keep HasSidecar true so the user can retry.</summary>
    public void EndRevertFailure(string error)
    {
        IsReverting = false;
        Status = PipelineStatus.Failed;
        StatusLabel = "Fehler";
        ApplyError = error;
    }

    /// <summary>
    /// Detects "the pipeline would change something on disk" without re-running it: any logical
    /// field with a non-<see cref="TagFieldSource.Existing"/> source means we have a proposal,
    /// regardless of whether dry-run gated the actual write.
    /// </summary>
    private static bool HasProposedChanges(PipelineOutcome outcome, TrackTags existing)
    {
        var r = outcome.Resolved;
        if (r.Genre.Source != TagFieldSource.Existing) return true;
        if (r.SubGenre.Source != TagFieldSource.Existing) return true;
        if (r.Bpm.Source != TagFieldSource.Existing) return true;
        if (r.Key.Source != TagFieldSource.Existing) return true;
        if (r.Energy.Source != TagFieldSource.Existing) return true;
        if (r.Mood.Source != TagFieldSource.Existing) return true;
        if (r.SetPosition.Source != TagFieldSource.Existing) return true;
        foreach (var kv in r.Custom)
        {
            if (kv.Value.Source != TagFieldSource.Existing) return true;
        }
        // Sort stage proposing a move counts as a change too.
        return outcome.DestinationPath is not null
            && !string.Equals(outcome.DestinationPath, outcome.File.Path, StringComparison.Ordinal);
    }
}
