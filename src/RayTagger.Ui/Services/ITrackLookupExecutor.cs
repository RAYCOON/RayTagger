using RayTagger.Core.Models;
using RayTagger.Hosting;

namespace RayTagger.Ui.Services;

/// <summary>
/// UI-side wrapper around <see cref="IPerTrackLookupService"/>. Resolves the "current options"
/// from <see cref="ScanCoordinator.LastOptions"/> at call time so per-row ViewModels don't have
/// to plumb a TaggerOptions reference around — they just call <see cref="ExecuteAsync"/>.
/// </summary>
public interface ITrackLookupExecutor
{
    /// <summary>
    /// True when a scan (or discovery) has loaded options AND at least one provider is
    /// configured with credentials. False before the first scan, or when no provider can fire.
    /// Drives the "API" button's enabled state.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Reason the button is disabled — non-null only when <see cref="IsAvailable"/> is false.
    /// Surfaces as the button's tooltip so the user knows why nothing happens on click.
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Runs the lookup + taxonomy resolution for one track using the last loaded options. Throws
    /// <see cref="InvalidOperationException"/> when called while <see cref="IsAvailable"/> is false.
    /// </summary>
    /// <param name="filePath">
    /// Optional absolute path to the audio file. When supplied, the service computes a
    /// Chromaprint fingerprint so AcoustID can fire — without it AcoustID is silently skipped.
    /// </param>
    Task<PerTrackLookupResult> ExecuteAsync(
        TrackTags existing,
        string? filePath = null,
        CancellationToken cancellationToken = default);
}
