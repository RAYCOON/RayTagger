using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Analysis.Genre;
using RayTagger.Analysis.Internal;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;

namespace RayTagger.Analysis.Tests.Genre;

/// <summary>
/// End-to-end run of <see cref="HeuristicGenreClassifier"/> against a real audio fixture from
/// <c>./music/</c>. Skipped when the Essentia binary isn't on PATH (CI without native deps).
/// </summary>
[Trait("Category", "Integration")]
public class HeuristicGenreClassifierIntegrationTests
{
    [Fact]
    public async Task Classifies_a_real_dj_track_when_essentia_is_available()
    {
        var repoRoot = LocateRepoRoot();
        var musicDir = Path.Combine(repoRoot, "music");
        if (!Directory.Exists(musicDir))
        {
            // ./music is gitignored per CLAUDE.md — skip when no fixture library is present.
            return;
        }

        var fixture = Directory.EnumerateFiles(musicDir, "*.mp3").FirstOrDefault()
                      ?? Directory.EnumerateFiles(musicDir, "*.flac").FirstOrDefault();
        if (fixture is null)
        {
            return;
        }

        var probe = new AnalysisToolProbe(new NativeProcessRunner(NullLogger<NativeProcessRunner>.Instance));
        var probeResult = await probe.ProbeAsync(EssentiaAnalysisService.Executable);
        if (!probeResult.IsAvailable)
        {
            return;
        }

        var essentia = new EssentiaAnalysisService(
            new NativeProcessRunner(NullLogger<NativeProcessRunner>.Instance),
            NullLogger<EssentiaAnalysisService>.Instance,
            TimeSpan.FromMinutes(2));
        var classifier = new HeuristicGenreClassifier(
            essentia,
            new HeuristicClassifierOptions { Enabled = true, MinConfidence = 0.0 },
            NullLogger<HeuristicGenreClassifier>.Instance);

        var info = new FileInfo(fixture);
        var ext = Path.GetExtension(fixture);
        var format = string.Equals(ext, ".flac", StringComparison.OrdinalIgnoreCase) ? AudioFormat.Flac
            : string.Equals(ext, ".aiff", StringComparison.OrdinalIgnoreCase) ? AudioFormat.Aiff
            : AudioFormat.Mp3;
        var track = new TrackFile(fixture, format, info.Length, info.LastWriteTimeUtc);

        var result = await classifier.ClassifyAsync(track, CancellationToken.None);

        // We can't assert the genre — fixtures are real-world DJ tracks of varying styles. But
        // with MinConfidence=0 the classifier must produce all eight lineup candidates ranked
        // by descending score (House / Techno / Trance / Drum and Bass / Dubstep / Hip Hop /
        // Ambient / Downtempo).
        result.Candidates.Should().HaveCount(8);
        result.Candidates.Select(c => c.Source).Should().AllSatisfy(s => s.Should().Be("classifier:heuristic"));
        for (var i = 1; i < result.Candidates.Count; i++)
        {
            result.Candidates[i - 1].Confidence.Should().BeGreaterThanOrEqualTo(
                result.Candidates[i].Confidence,
                because: "candidates must be sorted by descending confidence");
        }
    }

    private static string LocateRepoRoot()
    {
        // Walk up from the test's bin directory until we find tagger.yaml or the .slnx file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RayTagger.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from AppContext.BaseDirectory.");
    }
}
