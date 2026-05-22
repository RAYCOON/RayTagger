using RayTagger.Core.Models;
using RayTagger.Hosting;

namespace RayTagger.Ui.Services;

/// <inheritdoc cref="ITrackLookupExecutor"/>
public sealed class TrackLookupExecutor : ITrackLookupExecutor
{
    private readonly IPerTrackLookupService _service;
    private readonly ScanCoordinator _coordinator;

    public TrackLookupExecutor(IPerTrackLookupService service, ScanCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(coordinator);
        _service = service;
        _coordinator = coordinator;
    }

    public bool IsAvailable
    {
        get
        {
            var opts = _coordinator.LastOptions;
            return opts is not null && _service.IsAvailable(opts);
        }
    }

    public string? UnavailableReason
    {
        get
        {
            var opts = _coordinator.LastOptions;
            if (opts is null) return "Noch kein Scan gelaufen — Optionen werden beim Scan-Start geladen.";
            if (!_service.IsAvailable(opts)) return "Keine API konfiguriert (kein Provider mit Credentials).";
            return null;
        }
    }

    public Task<PerTrackLookupResult> ExecuteAsync(
        TrackTags existing,
        string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        var opts = _coordinator.LastOptions
            ?? throw new InvalidOperationException("ExecuteAsync called before options were loaded.");
        return _service.ResolveAsync(opts, existing, filePath, cancellationToken);
    }
}
