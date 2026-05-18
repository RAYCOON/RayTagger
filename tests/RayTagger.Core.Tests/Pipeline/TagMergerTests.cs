using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Core.Tests.Pipeline;

public class TagMergerTests
{
    [Fact]
    public void Analysis_overrides_when_existing_empty_under_skip_if_present()
    {
        var existing = new TrackTags(Genre: "House");  // no BPM
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.9);

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig(), ExistingTagsPolicy.SkipIfPresent);

        resolved.Bpm.Value.Should().Be(128);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Analysis);
    }

    [Fact]
    public void Existing_wins_when_present_under_skip_if_present()
    {
        var existing = new TrackTags(Bpm: 120);
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.9);

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig(), ExistingTagsPolicy.SkipIfPresent);

        resolved.Bpm.Value.Should().Be(120);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void Analysis_overrides_when_policy_is_always_overwrite()
    {
        var existing = new TrackTags(Bpm: 120);
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.9);

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig(), ExistingTagsPolicy.AlwaysOverwrite);

        resolved.Bpm.Value.Should().Be(128);
        resolved.Bpm.Source.Should().Be(TagFieldSource.Analysis);
    }

    [Fact]
    public void Low_confidence_analysis_is_rejected_even_when_existing_is_empty()
    {
        var existing = new TrackTags();  // no BPM
        var analysis = MakeAnalysis(bpm: 128, bpmConfidence: 0.2);  // below default 0.6

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig(), ExistingTagsPolicy.SkipIfPresent);

        resolved.Bpm.Value.Should().BeNull();
        resolved.Bpm.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void Genre_always_flows_through_as_existing_in_phase_2()
    {
        // Phase 2 boundary: genre comes from existing tags only. Lookup-sourced genre arrives in
        // Phase 4 and will overlay on top of this.
        var existing = new TrackTags(Genre: "Tech House");
        var analysis = AnalysisResult.Empty;

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig(), ExistingTagsPolicy.SkipIfPresent);

        resolved.Genre.Value.Should().Be("Tech House");
        resolved.Genre.Source.Should().Be(TagFieldSource.Existing);
    }

    [Fact]
    public void Key_with_high_confidence_replaces_missing_existing_key()
    {
        var existing = new TrackTags();
        var analysis = MakeAnalysis(key: new MusicalKey("Am", "8A"), keyConfidence: 0.9);

        var resolved = TagMerger.Merge(existing, analysis, lookup: null, DefaultAnalysisConfig(), ExistingTagsPolicy.SkipIfPresent);

        resolved.Key.Value.Should().NotBeNull();
        resolved.Key.Value!.Camelot.Should().Be("8A");
        resolved.Key.Source.Should().Be(TagFieldSource.Analysis);
    }

    private static AnalysisOptions DefaultAnalysisConfig() => new()
    {
        Bpm = new AnalyzerOptions { Provider = "aubio", MinConfidence = 0.6 },
        Key = new KeyAnalyzerOptions { Provider = "keyfinder-cli", MinConfidence = 0.55 },
        Energy = new EnergyAnalyzerOptions { Provider = "essentia", MinConfidence = 0.5 },
        Fingerprint = new AnalyzerOptions { Provider = "chromaprint", MinConfidence = 0.0 },
    };

    private static AnalysisResult MakeAnalysis(
        double? bpm = null,
        double bpmConfidence = 0,
        MusicalKey? key = null,
        double keyConfidence = 0,
        int? energy = null,
        double energyConfidence = 0) =>
        new(
            new BpmResult(bpm, bpmConfidence),
            new KeyResult(key, keyConfidence),
            new EnergyResult(energy, energyConfidence),
            new FingerprintResult(null, 0),
            AcoustIdMbid: null);
}
