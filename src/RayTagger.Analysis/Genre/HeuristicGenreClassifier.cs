using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Genre;

/// <summary>
/// Tier-3 heuristic genre classifier — pure-rule scoring over DSP descriptors from the existing
/// Essentia run. Emits at most one candidate per genre in the 8-entry lineup
/// (House / Techno / Trance / Drum and Bass / Dubstep / Hip Hop / Ambient / Downtempo) with the
/// composite score as confidence. Candidates below
/// <see cref="HeuristicClassifierOptions.MinConfidence"/> are dropped — when no genre clears
/// the floor the classifier emits nothing, by design. See
/// <c>docs/PLAN_GENRE_CLASSIFICATION.md §3.5</c>.
/// </summary>
/// <remarks>
/// Same-genre subgenres (Tech House vs Deep House, Detroit vs Berlin Techno, Liquid vs Neurofunk
/// DnB) are deliberately out of scope — those need timbral/harmonic analysis the heuristic
/// doesn't have. Phase B's <c>discogs_effnet</c> TF model is the subgenre source.
/// </remarks>
public sealed class HeuristicGenreClassifier : IGenreClassifier
{
    private readonly IEssentiaAnalysisService _essentia;
    private readonly HeuristicClassifierOptions _options;
    // Field kept for future structured-logging hooks; currently unused — the classifier is
    // silent on the happy path and only the runner logs failures.
    private readonly ILogger<HeuristicGenreClassifier> _logger;

    public HeuristicGenreClassifier(
        IEssentiaAnalysisService essentia,
        HeuristicClassifierOptions options,
        ILogger<HeuristicGenreClassifier> logger)
    {
        ArgumentNullException.ThrowIfNull(essentia);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _essentia = essentia;
        _options = options;
        _logger = logger;
    }

    public string Name => "heuristic";

    public async Task<GenreClassificationResult> ClassifyAsync(TrackFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = await _essentia.RunAsync(file, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return GenreClassificationResult.Empty;
        }

        var scored = ScoreAll(result);
        var candidates = new List<GenreCandidate>();
        foreach (var (genre, confidence) in scored.OrderByDescending(s => s.Confidence))
        {
            if (confidence < _options.MinConfidence)
            {
                continue;
            }
            var normalised = ClassifierLabelNormaliser.Normalise(genre);
            if (normalised.Length == 0)
            {
                continue;
            }
            candidates.Add(new GenreCandidate(normalised, confidence, $"classifier:{Name}"));
        }
        return new GenreClassificationResult(candidates);
    }

    /// <summary>
    /// Static for testability. Returns one entry per genre in the lineup — callers filter by
    /// <see cref="HeuristicClassifierOptions.MinConfidence"/>. The score is a weighted average
    /// over feature contributions; <c>null</c> features are skipped (not zeroed) so a partial
    /// Essentia output degrades gracefully.
    /// </summary>
    /// <remarks>
    /// Lineup tracks the team taxonomy's top-level electronic + adjacent genres that have
    /// DSP-distinguishable signatures: House / Techno / Trance / Drum and Bass / Dubstep /
    /// Hip Hop / Ambient / Downtempo. Same-genre subgenres (Tech House vs Deep House) are out
    /// of scope — heuristics propose, taxonomy + rules dispose.
    /// </remarks>
    internal static IReadOnlyList<(string Genre, double Confidence)> ScoreAll(EssentiaResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return
        [
            ("House", ScoreGenre(r, GenreProfile.House)),
            ("Techno", ScoreGenre(r, GenreProfile.Techno)),
            ("Trance", ScoreGenre(r, GenreProfile.Trance)),
            ("Drum and Bass", ScoreGenre(r, GenreProfile.DrumAndBass)),
            ("Dubstep", ScoreGenre(r, GenreProfile.Dubstep)),
            ("Hip Hop", ScoreGenre(r, GenreProfile.HipHop)),
            ("Ambient", ScoreGenre(r, GenreProfile.Ambient)),
            ("Downtempo", ScoreGenre(r, GenreProfile.Downtempo)),
        ];
    }

    private static double ScoreGenre(EssentiaResult r, GenreProfile p)
    {
        // Stack-based accumulation — called 8× per track (once per genre), so an allocation
        // here multiplies. The seven score methods all return Nullable<double>; null means
        // "feature absent for this track" and skips the contribution (does not zero it).
        double sum = 0;
        var count = 0;
        Accumulate(p.ScoreBpm(r.Bpm), ref sum, ref count);
        Accumulate(p.ScoreKeyScale(r.KeyScale), ref sum, ref count);
        Accumulate(p.ScoreChordsChangesRate(r.ChordsChangesRate), ref sum, ref count);
        Accumulate(p.ScoreSpectralCentroid(r.SpectralCentroidMean), ref sum, ref count);
        Accumulate(p.ScoreDynamicComplexity(r.DynamicComplexity), ref sum, ref count);
        Accumulate(p.ScoreDanceability(r.Danceability), ref sum, ref count);
        Accumulate(p.ScoreBeatsLoudness(r.BeatsLoudness), ref sum, ref count);

        return count == 0 ? 0 : sum / count;
    }

    private static void Accumulate(double? value, ref double sum, ref int count)
    {
        if (value.HasValue)
        {
            sum += value.Value;
            count++;
        }
    }

    /// <summary>
    /// Per-genre scoring profile. Each method returns <c>null</c> when the underlying feature
    /// was absent — the composite skips those instead of zeroing them. Numeric thresholds match
    /// <c>docs/PLAN_GENRE_CLASSIFICATION.md §3.5</c>.
    /// </summary>
    private sealed class GenreProfile
    {
        public static GenreProfile House { get; } = new(
            bpmCenter: 123.0, bpmCenterHalfWidth: 5.0, bpmFadeWidth: 10.0,
            scaleScore: (major: 0.4, minor: 0.6),
            chordsLow: 0.04, chordsHigh: 0.10,
            chordsScores: (low: 0.4, mid: 0.7, high: 0.4),
            centroidLowMax: 2200.0, centroidHighMin: 2800.0,
            centroidScores: (low: 0.7, mid: 0.5, high: 0.35),
            dynLow: 3.0, dynHigh: 5.0,
            dynScores: (low: 0.4, mid: 0.6, high: 0.4),
            danceabilityScores: (low: 0.3, mid: 0.5, high: 0.7),
            beatsLoudnessProfile: BeatsLoudnessProfile.High);

        public static GenreProfile Techno { get; } = new(
            bpmCenter: 135.0, bpmCenterHalfWidth: 10.0, bpmFadeWidth: 10.0,
            scaleScore: (major: 0.5, minor: 0.5),
            chordsLow: 0.04, chordsHigh: 0.10,
            chordsScores: (low: 0.9, mid: 0.45, high: 0.3),
            centroidLowMax: 2200.0, centroidHighMin: 2800.0,
            centroidScores: (low: 0.4, mid: 0.55, high: 0.8),
            dynLow: 3.0, dynHigh: 5.0,
            dynScores: (low: 0.8, mid: 0.5, high: 0.3),
            danceabilityScores: (low: 0.3, mid: 0.5, high: 0.7),
            beatsLoudnessProfile: BeatsLoudnessProfile.VeryHigh);

        public static GenreProfile Trance { get; } = new(
            bpmCenter: 136.5, bpmCenterHalfWidth: 8.5, bpmFadeWidth: 10.0,
            scaleScore: (major: 0.7, minor: 0.4),
            chordsLow: 0.04, chordsHigh: 0.10,
            chordsScores: (low: 0.3, mid: 0.5, high: 0.8),
            centroidLowMax: 2200.0, centroidHighMin: 2800.0,
            centroidScores: (low: 0.45, mid: 0.7, high: 0.45),
            dynLow: 3.0, dynHigh: 5.0,
            dynScores: (low: 0.3, mid: 0.5, high: 0.7),
            danceabilityScores: (low: 0.3, mid: 0.7, high: 0.55),
            beatsLoudnessProfile: BeatsLoudnessProfile.Mid);

        // Half-time-feel breakbeat genre. 170–175 BPM is the discriminator — no other top-level
        // club genre lands here. High beats-loudness from the loud break + sub-bass; centroid is
        // typically high because of cymbals and hi-hats riding above the break.
        public static GenreProfile DrumAndBass { get; } = new(
            bpmCenter: 172.5, bpmCenterHalfWidth: 7.5, bpmFadeWidth: 10.0,
            scaleScore: (major: 0.4, minor: 0.7),
            chordsLow: 0.04, chordsHigh: 0.10,
            chordsScores: (low: 0.7, mid: 0.55, high: 0.3),
            centroidLowMax: 2200.0, centroidHighMin: 2800.0,
            centroidScores: (low: 0.3, mid: 0.5, high: 0.8),
            dynLow: 3.0, dynHigh: 5.0,
            dynScores: (low: 0.4, mid: 0.6, high: 0.75),
            danceabilityScores: (low: 0.3, mid: 0.5, high: 0.8),
            beatsLoudnessProfile: BeatsLoudnessProfile.VeryHigh);

        // 140-ish main tempo with crushing dynamics — wobble basses and drops push dynamic
        // complexity well above techno's steady loop. Chord motion is essentially absent. The
        // half-time-feel doesn't change Essentia's reported BPM unless a profile forces it; we
        // score the 140 range and let the half-time correction (post-classifier) do the rest.
        public static GenreProfile Dubstep { get; } = new(
            bpmCenter: 141.5, bpmCenterHalfWidth: 3.5, bpmFadeWidth: 10.0,
            scaleScore: (major: 0.4, minor: 0.65),
            chordsLow: 0.04, chordsHigh: 0.10,
            chordsScores: (low: 0.9, mid: 0.3, high: 0.2),
            centroidLowMax: 2200.0, centroidHighMin: 2800.0,
            centroidScores: (low: 0.4, mid: 0.55, high: 0.75),
            dynLow: 3.0, dynHigh: 5.0,
            dynScores: (low: 0.2, mid: 0.5, high: 0.9),
            danceabilityScores: (low: 0.3, mid: 0.55, high: 0.65),
            beatsLoudnessProfile: BeatsLoudnessProfile.VeryHigh);

        // Boom-bap / trap / lo-fi all sit 80–100 BPM. The 90 center catches that range; chord
        // changes come from sampled chops (mid-band), spectral centroid skews low because of
        // warm low-mid emphasis. The discriminator vs Downtempo at the same tempo is the punch:
        // hip hop kicks hit harder (Mid beats-loudness peaks at 0.10–0.14) and the sample-chop
        // mid chord-rate scores higher here than in Downtempo, which leans on full progressions.
        public static GenreProfile HipHop { get; } = new(
            bpmCenter: 90.0, bpmCenterHalfWidth: 10.0, bpmFadeWidth: 10.0,
            scaleScore: (major: 0.5, minor: 0.65),
            chordsLow: 0.04, chordsHigh: 0.10,
            chordsScores: (low: 0.4, mid: 0.85, high: 0.4),
            centroidLowMax: 2200.0, centroidHighMin: 2800.0,
            centroidScores: (low: 0.8, mid: 0.5, high: 0.3),
            dynLow: 3.0, dynHigh: 5.0,
            dynScores: (low: 0.45, mid: 0.8, high: 0.35),
            danceabilityScores: (low: 0.45, mid: 0.8, high: 0.5),
            beatsLoudnessProfile: BeatsLoudnessProfile.Mid);

        // BPM is unreliable on ambient (drone material), so we use a very wide window with a
        // capped peak score. The real discriminators are very low danceability, very low beats
        // loudness, low dynamic complexity and warm spectrum.
        public static GenreProfile Ambient { get; } = new(
            bpmCenter: 95.0, bpmCenterHalfWidth: 55.0, bpmFadeWidth: 30.0,
            scaleScore: (major: 0.55, minor: 0.55),
            chordsLow: 0.04, chordsHigh: 0.10,
            chordsScores: (low: 0.65, mid: 0.6, high: 0.5),
            centroidLowMax: 2200.0, centroidHighMin: 2800.0,
            centroidScores: (low: 0.8, mid: 0.5, high: 0.3),
            dynLow: 3.0, dynHigh: 5.0,
            dynScores: (low: 0.85, mid: 0.45, high: 0.25),
            danceabilityScores: (low: 0.85, mid: 0.4, high: 0.2),
            beatsLoudnessProfile: BeatsLoudnessProfile.VeryLow);

        // Trip-hop / chill-out / lo-fi-instrumental. 80–110 BPM with more rhythmic spine than
        // ambient but less drive than club music. Often samples-driven and harmonically richer
        // than hip-hop, so chord movement is rewarded in the high band. Beats are soft (Low
        // profile peaks at <0.10), which is what separates it from Hip Hop's chunky kicks.
        public static GenreProfile Downtempo { get; } = new(
            bpmCenter: 92.5, bpmCenterHalfWidth: 17.5, bpmFadeWidth: 15.0,
            scaleScore: (major: 0.5, minor: 0.65),
            chordsLow: 0.04, chordsHigh: 0.10,
            chordsScores: (low: 0.4, mid: 0.6, high: 0.85),
            centroidLowMax: 2200.0, centroidHighMin: 2800.0,
            centroidScores: (low: 0.7, mid: 0.65, high: 0.4),
            dynLow: 3.0, dynHigh: 5.0,
            dynScores: (low: 0.6, mid: 0.7, high: 0.5),
            danceabilityScores: (low: 0.75, mid: 0.7, high: 0.4),
            beatsLoudnessProfile: BeatsLoudnessProfile.Low);

        private readonly double _bpmCenter;
        private readonly double _bpmCenterHalfWidth;
        private readonly double _bpmFadeWidth;
        private readonly (double Major, double Minor) _scaleScore;
        private readonly double _chordsLow;
        private readonly double _chordsHigh;
        private readonly (double Low, double Mid, double High) _chordsScores;
        private readonly double _centroidLowMax;
        private readonly double _centroidHighMin;
        private readonly (double Low, double Mid, double High) _centroidScores;
        private readonly double _dynLow;
        private readonly double _dynHigh;
        private readonly (double Low, double Mid, double High) _dynScores;
        private readonly (double Low, double Mid, double High) _danceabilityScores;
        private readonly BeatsLoudnessProfile _beatsLoudnessProfile;

        private GenreProfile(
            double bpmCenter, double bpmCenterHalfWidth, double bpmFadeWidth,
            (double major, double minor) scaleScore,
            double chordsLow, double chordsHigh,
            (double low, double mid, double high) chordsScores,
            double centroidLowMax, double centroidHighMin,
            (double low, double mid, double high) centroidScores,
            double dynLow, double dynHigh,
            (double low, double mid, double high) dynScores,
            (double low, double mid, double high) danceabilityScores,
            BeatsLoudnessProfile beatsLoudnessProfile)
        {
            _bpmCenter = bpmCenter;
            _bpmCenterHalfWidth = bpmCenterHalfWidth;
            _bpmFadeWidth = bpmFadeWidth;
            _scaleScore = scaleScore;
            _chordsLow = chordsLow;
            _chordsHigh = chordsHigh;
            _chordsScores = chordsScores;
            _centroidLowMax = centroidLowMax;
            _centroidHighMin = centroidHighMin;
            _centroidScores = centroidScores;
            _dynLow = dynLow;
            _dynHigh = dynHigh;
            _dynScores = dynScores;
            _danceabilityScores = danceabilityScores;
            _beatsLoudnessProfile = beatsLoudnessProfile;
        }

        public double? ScoreBpm(double? bpm)
        {
            if (!bpm.HasValue) return null;
            var v = bpm.Value;
            var distanceFromCenter = Math.Abs(v - _bpmCenter);
            if (distanceFromCenter <= _bpmCenterHalfWidth)
            {
                return 1.0;
            }
            var excess = distanceFromCenter - _bpmCenterHalfWidth;
            if (excess >= _bpmFadeWidth) return 0.0;
            return 1.0 - (excess / _bpmFadeWidth);
        }

        public double? ScoreKeyScale(string? scale)
        {
            if (string.IsNullOrWhiteSpace(scale)) return null;
            return scale.Equals("major", StringComparison.OrdinalIgnoreCase)
                ? _scaleScore.Major
                : _scaleScore.Minor;
        }

        public double? ScoreChordsChangesRate(double? value)
        {
            if (!value.HasValue) return null;
            var v = value.Value;
            if (v < _chordsLow) return _chordsScores.Low;
            if (v > _chordsHigh) return _chordsScores.High;
            return _chordsScores.Mid;
        }

        public double? ScoreSpectralCentroid(double? value)
        {
            if (!value.HasValue) return null;
            var v = value.Value;
            if (v < _centroidLowMax) return _centroidScores.Low;
            if (v > _centroidHighMin) return _centroidScores.High;
            return _centroidScores.Mid;
        }

        public double? ScoreDynamicComplexity(double? value)
        {
            if (!value.HasValue) return null;
            var v = value.Value;
            if (v < _dynLow) return _dynScores.Low;
            if (v > _dynHigh) return _dynScores.High;
            return _dynScores.Mid;
        }

        public double? ScoreDanceability(double? value)
        {
            if (!value.HasValue) return null;
            var v = value.Value;
            if (v > 1.0) return _danceabilityScores.High;
            if (v >= 0.7) return _danceabilityScores.Mid;
            return _danceabilityScores.Low;
        }

        public double? ScoreBeatsLoudness(double? value)
        {
            if (!value.HasValue) return null;
            var v = value.Value;
            return _beatsLoudnessProfile switch
            {
                // Ambient: anything above a whisper kills the score.
                BeatsLoudnessProfile.VeryLow => v < 0.08 ? 0.85 : v < 0.12 ? 0.5 : 0.2,
                // Downtempo: rhythmic but reserved.
                BeatsLoudnessProfile.Low => v < 0.10 ? 0.7 : v < 0.14 ? 0.55 : 0.3,
                // Trance / Hip Hop: present but not slamming.
                BeatsLoudnessProfile.Mid => v is >= 0.08 and <= 0.14 ? 0.6 : 0.35,
                // House: solid kick.
                BeatsLoudnessProfile.High => v >= 0.14 ? 0.6 : 0.4,
                // Techno / DnB / Dubstep: crushing.
                BeatsLoudnessProfile.VeryHigh => v >= 0.16 ? 0.8 : 0.4,
                _ => 0.5,
            };
        }

        private enum BeatsLoudnessProfile { VeryLow, Low, Mid, High, VeryHigh }
    }
}
