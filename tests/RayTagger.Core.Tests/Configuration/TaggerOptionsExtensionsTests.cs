using RayTagger.Core.Configuration;

namespace RayTagger.Core.Tests.Configuration;

public class TaggerOptionsExtensionsTests
{
    [Fact]
    public void ForceOverwriteExistingTags_zeroes_every_per_dimension_existing_confidence()
    {
        var options = new TaggerOptions();
        // Sanity-check: defaults are 1.0 across the board.
        options.Analysis.Bpm.ExistingConfidence.Should().Be(1.0);
        options.Analysis.Key.ExistingConfidence.Should().Be(1.0);
        options.Analysis.Energy.ExistingConfidence.Should().Be(1.0);
        options.Lookup.ExistingConfidence.Should().Be(1.0);

        options.ForceOverwriteExistingTags();

        options.Analysis.Bpm.ExistingConfidence.Should().Be(0.0);
        options.Analysis.Key.ExistingConfidence.Should().Be(0.0);
        options.Analysis.Energy.ExistingConfidence.Should().Be(0.0);
        options.Lookup.ExistingConfidence.Should().Be(0.0);
    }

    [Fact]
    public void ForceOverwriteExistingTags_overrides_explicit_per_dimension_values()
    {
        var options = new TaggerOptions();
        options.Analysis.Bpm.ExistingConfidence = 0.7;
        options.Analysis.Key.ExistingConfidence = 0.5;
        options.Lookup.ExistingConfidence = 0.8;

        options.ForceOverwriteExistingTags();

        options.Analysis.Bpm.ExistingConfidence.Should().Be(0.0);
        options.Analysis.Key.ExistingConfidence.Should().Be(0.0);
        options.Lookup.ExistingConfidence.Should().Be(0.0);
    }

    [Fact]
    public void ForceOverwriteExistingTags_is_idempotent()
    {
        var options = new TaggerOptions();

        options.ForceOverwriteExistingTags();
        options.ForceOverwriteExistingTags();

        options.Analysis.Bpm.ExistingConfidence.Should().Be(0.0);
    }
}
