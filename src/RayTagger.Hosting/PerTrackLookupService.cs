using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using RayTagger.Analysis;
using RayTagger.Analysis.Internal;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;
using RayTagger.Core.Mapping;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Hosting;

/// <summary>
/// Runs a single online lookup for one track on demand — the workhorse behind the UI's per-row
/// "API" button. Reuses the same provider wiring as the bulk-scan path (via
/// <see cref="LookupRunnerBuilder"/>) so a per-track button-click and a bulk scan against the
/// same options produce identical raw API responses, the same cache entries, and the same
/// rate-limit behaviour.
/// </summary>
/// <remarks>
/// The <see cref="ILookupRunner"/> per-options is cached via
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>: the same <see cref="TaggerOptions"/>
/// instance reuses the same runner (and therefore the same provider HttpClients, rate-limit
/// gates, and cache handle). A new options instance — i.e. after the user reloads
/// <c>tagger.yaml</c> — gets a fresh runner; the old one is eligible for GC.
/// </remarks>
public sealed class PerTrackLookupService : IPerTrackLookupService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUserDataDirectoryProvider _dataDirs;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TaxonomyGenreResolver _genreResolver;
    private readonly UserAgentState _userAgentState;
    private readonly NativeProcessRunner _processRunner;
    private readonly ConditionalWeakTable<TaggerOptions, ILookupRunner> _runnerCache = new();

    public PerTrackLookupService(
        IHttpClientFactory httpClientFactory,
        IUserDataDirectoryProvider dataDirs,
        ILoggerFactory loggerFactory,
        TaxonomyGenreResolver genreResolver,
        UserAgentState userAgentState,
        NativeProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(dataDirs);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(genreResolver);
        ArgumentNullException.ThrowIfNull(userAgentState);
        ArgumentNullException.ThrowIfNull(processRunner);

        _httpClientFactory = httpClientFactory;
        _dataDirs = dataDirs;
        _loggerFactory = loggerFactory;
        _genreResolver = genreResolver;
        _userAgentState = userAgentState;
        _processRunner = processRunner;
    }

    public bool IsAvailable(TaggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return LookupRunnerBuilder.HasAnyAvailableProvider(options.Lookup);
    }

    public async Task<PerTrackLookupResult> ResolveAsync(
        TaggerOptions options,
        TrackTags existing,
        string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(existing);

        // Mirror PipelineFactory.ApplyUserAgentContact so per-track calls also identify
        // themselves with the configured contact.
        _userAgentState.SetContact(options.Lookup.UserAgentContact);

        var runner = GetOrBuildRunner(options);

        // If a path is supplied AND the AcoustID provider is configured, compute a fingerprint
        // ahead of the lookup. Without this AcoustID's CanHandle would return false and the
        // provider would be skipped — losing the high-precision MBID handshake that makes the
        // downstream MB lookup actually find the track.
        string? fingerprint = null;
        if (filePath is not null && AcoustIdConfigured(options))
        {
            fingerprint = await TryComputeFingerprintAsync(filePath, cancellationToken).ConfigureAwait(false);
        }

        var query = BuildQuery(existing, fingerprint);
        if (!query.HasAnySignal)
        {
            return PerTrackLookupResult.NoSignal;
        }

        // Force the runner to fire even if `lookup.enabled` is off — the UI button deliberately
        // bypasses that flag (it governs the automatic scan-time lookup only). LookupRunnerBuilder
        // already handles this: when Enabled=false the per-track-built runner is the noop, so we
        // override that selection here by always building a "live" runner.
        LookupRunResult runResult;
        try
        {
            runResult = await runner.RunAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loggerFactory.CreateLogger<PerTrackLookupService>()
                .LogWarning(ex, "Per-track lookup failed for '{Artist} - {Title}': {Message}",
                    existing.Artist, existing.Title, ex.Message);
            return PerTrackLookupResult.Failed(ex.Message);
        }
        var lookupResult = runResult.Result;

        var resolution = _genreResolver.Resolve(
            lookupResult.GenreCandidates,
            lookupResult.SubGenreCandidates,
            options.Taxonomy.Loaded,
            existing.Genre,
            existing.SubGenre,
            options.Mapping.SourcePriority);

        var rawCount = lookupResult.GenreCandidates.Count + lookupResult.SubGenreCandidates.Count;
        var apiFoundTrack = rawCount > 0
            || lookupResult.MbRecordingId is not null
            || lookupResult.MbReleaseId is not null;

        return new PerTrackLookupResult(
            ProposedGenre: resolution.ProposedGenre,
            ProposedSubgenre: resolution.ProposedSubgenre,
            ChosenGenre: resolution.ChosenGenre,
            ChosenSubgenre: resolution.ChosenSubgenre,
            FallbackApplied: resolution.FallbackApplied,
            MatchedCandidate: resolution.MatchedCandidate,
            Trace: resolution.Trace,
            ErrorMessage: null)
        {
            ApiFoundTrack = apiFoundTrack,
            RawCandidateCount = rawCount,
            ProviderTrace = runResult.Trace,
        };
    }

    private ILookupRunner GetOrBuildRunner(TaggerOptions options)
    {
        if (_runnerCache.TryGetValue(options, out var cached)) return cached;

        // The UI button must work even when lookup.enabled = false. Build a *temporary* options
        // record with Enabled flipped on for the builder; we don't mutate the original.
        var effectiveLookup = options.Lookup.Enabled
            ? options.Lookup
            : CloneWithEnabled(options.Lookup, enabled: true);

        var runner = LookupRunnerBuilder.Build(
            effectiveLookup,
            _httpClientFactory,
            _dataDirs,
            _loggerFactory,
            NoopToolStatusReporter.Instance);
        _runnerCache.Add(options, runner);
        return runner;
    }

    private static LookupOptions CloneWithEnabled(LookupOptions src, bool enabled) => new()
    {
        Enabled = enabled,
        OnlineRequired = src.OnlineRequired,
        Providers = src.Providers,
        Cache = src.Cache,
        ApiKeys = src.ApiKeys,
        RateLimits = src.RateLimits,
        UserAgentContact = src.UserAgentContact,
        TaxonomyResolution = src.TaxonomyResolution,
    };

    private static LookupQuery BuildQuery(TrackTags existing, string? fingerprint) => new()
    {
        Artist = existing.Artist,
        Title = existing.Title,
        Album = existing.Album,
        DurationSeconds = existing.DurationSeconds,
        Fingerprint = fingerprint,
    };

    private static bool AcoustIdConfigured(TaggerOptions options) =>
        options.Lookup.Providers.Any(p => string.Equals(p, "acoustid", StringComparison.OrdinalIgnoreCase))
        && !string.IsNullOrWhiteSpace(options.Lookup.ApiKeys.Acoustid);

    /// <summary>
    /// Computes a Chromaprint fingerprint for the given file by reusing
    /// <see cref="ChromaprintFingerprintAnalyzer"/>. Returns null on any failure (fpcalc not on
    /// PATH, file unreadable, timeout) — the lookup then proceeds without AcoustID.
    /// </summary>
    private async Task<string?> TryComputeFingerprintAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            _loggerFactory.CreateLogger<PerTrackLookupService>()
                .LogWarning("Per-track fingerprint skipped: file not found at {Path}", filePath);
            return null;
        }

        // Synthesize a minimal TrackFile — the analyzer only reads Path, the other fields are
        // unused for fingerprinting.
        var trackFile = new TrackFile(filePath, AudioFormat.Mp3, SizeBytes: 0, LastModifiedUtc: DateTime.UtcNow);
        var analyzer = new ChromaprintFingerprintAnalyzer(
            _processRunner,
            _loggerFactory.CreateLogger<ChromaprintFingerprintAnalyzer>());
        try
        {
            var result = await analyzer.AnalyzeAsync(trackFile, cancellationToken).ConfigureAwait(false);
            return result.Chromaprint;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loggerFactory.CreateLogger<PerTrackLookupService>()
                .LogWarning(ex, "fpcalc failed for {Path}: {Message} — AcoustID will be skipped", filePath, ex.Message);
            return null;
        }
    }
}
